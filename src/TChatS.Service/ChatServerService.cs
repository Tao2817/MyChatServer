using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TChatS.Core;
using TChatS.Core.Models;
using TChatS.Protocol;
using TChatS.Transport;

namespace TChatS.Service;

/// <summary>
/// 服务编排层。负责 TCP 监听、连接生命周期、消息接收循环、
/// 输出动作执行和优雅关闭。相当于旧版 TChatServerDlg + ListenSocket 的组合。
/// </summary>
public sealed class ChatServerService : IAsyncDisposable
{
    private readonly ConnectionManager _connections;
    private readonly ChatRoomManager _chatRooms;
    private readonly MessageRouter _router;
    private readonly IProtocolParser _protocol;
    private readonly IServiceProtocol _fmt;
    private readonly ILogger<ChatServerService> _logger;

    private readonly IPAddress _bindAddress;
    private readonly int _port;
    private readonly int _heartbeatSeconds;

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private readonly List<Task> _clientTasks = [];
    private int _stopped;

    public ChatServerService(
        ConnectionManager connections,
        ChatRoomManager chatRooms,
        MessageRouter router,
        IProtocolParser protocol,
        IServiceProtocol fmt,
        ChatServerOptions options,
        ILogger<ChatServerService> logger)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _chatRooms = chatRooms ?? throw new ArgumentNullException(nameof(chatRooms));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _fmt = fmt ?? throw new ArgumentNullException(nameof(fmt));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _bindAddress = IPAddress.Parse(options.BindAddress);
        _port = options.Port;
        _heartbeatSeconds = options.HeartbeatSeconds;
    }

    /// <summary>
    /// 启动服务：绑定端口，开始接受连接。
    /// </summary>
    public async Task StartAsync(CancellationToken externalCt = default)
    {
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _listener = new TcpListener(_bindAddress, _port);
        _listener.Start();

        _logger.LogInformation("TChatServer 已启动 - {Address}:{Port}", _bindAddress, _port);

        // 接受连接循环
        _ = AcceptLoopAsync(_serverCts.Token);
    }

    /// <summary>
    /// 停止服务：停止监听，通知所有客户端，关闭所有连接。
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return; // 已停止

        _logger.LogInformation("正在关闭服务...");

        // 停止接受新连接
        _listener?.Stop();
        _serverCts?.Cancel();

        // 广播 #->3 服务器关闭到所有客户端
        await BroadcastGlobalAsync(_fmt.ServerShutdown());

        // 等待所有客户端任务完成（给一点时间发送 #->3）
        if (_clientTasks.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(_clientTasks), Task.Delay(3000));
        }

        // 强制关闭所有连接
        foreach (var conn in _connections.GetActiveConnections())
        {
            try { conn.Disconnect(); } catch { /* 静默 */ }
        }

        _logger.LogInformation("TChatServer 已停止");
    }

    // ─── Accept 循环 ───

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener!.AcceptSocketAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _logger.LogInformation("<Listen>: 接收客户的一个连接请求 ({Remote})",
                    socket.RemoteEndPoint?.ToString() ?? "unknown");

                // 注册到连接管理器
                TcpConnection conn;
                try
                {
                    conn = _connections.Accept(socket);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex.Message);
                    continue;
                }

                // 启动独立的消息处理任务
                var task = HandleClientAsync(conn, ct);
                _clientTasks.Add(task);
            }
        }
        catch (ObjectDisposedException)
        {
            // listener 已释放，正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Accept 循环异常");
        }
    }

    // ─── 客户端消息处理 ───

    private async Task HandleClientAsync(TcpConnection conn, CancellationToken serverCt)
    {
        try
        {
            await ReadLoopAsync(conn, serverCt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("连接 {Id} ({Remote}): {Message}",
                conn.Id, conn.RemoteEndPoint, ex.Message);
        }
        finally
        {
            // 处理用户离开
            try
            {
                var leaveResult = _router.HandleDisconnect(conn.Id);
                if (leaveResult.Actions.Count > 0)
                    await ExecuteActionsAsync(leaveResult);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "处理离开事件异常");
            }

            _connections.RemoveConnection(conn.Id);
            _logger.LogInformation("连接 {Id} ({Remote}) 已断开", conn.Id, conn.RemoteEndPoint);
        }
    }

    // ─── 消息读取循环 ───

    private async Task ReadLoopAsync(TcpConnection conn, CancellationToken serverCt)
    {
        var readBuffer = new byte[4096];
        var accumulated = new List<byte>();

        while (true)
        {
            // 心跳超时: -1 表示禁用
            var timeoutCts = _heartbeatSeconds >= 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_heartbeatSeconds))
                : null;
            using var _ = timeoutCts;
            var linkedCts = timeoutCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(serverCt, timeoutCts.Token)
                : null;
            using var __ = linkedCts;
            var receiveCt = linkedCts?.Token ?? serverCt;

            int bytesRead;
            try
            {
                bytesRead = await conn.ReceiveAsync(readBuffer, receiveCt);
            }
            catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
            {
                _logger.LogWarning("连接 {Id} ({Remote}) 心跳超时 ({Seconds}s)",
                    conn.Id, conn.RemoteEndPoint, _heartbeatSeconds);
                break;
            }

            if (bytesRead == 0)
            {
                _logger.LogInformation("连接 {Id} ({Remote}) 远端已关闭连接",
                    conn.Id, conn.RemoteEndPoint);
                break; // 客户端正常关闭 / Socket 已释放
            }

            // 追加到累积缓冲区
            accumulated.AddRange(readBuffer.AsSpan(0, bytesRead));

            // 尝试解析完整消息
            var ok = await TryParseAndRouteAsync(conn, accumulated, serverCt);
            if (!ok)
            {
                conn.Disconnect();
                break;
            }
        }
    }

    /// <summary>
    /// 从累积缓冲区中解析所有完整消息并路由。
    /// 返回 <c>false</c> 表示协议错误，调用方应断开连接。
    /// </summary>
    private async Task<bool> TryParseAndRouteAsync(
        TcpConnection conn, List<byte> accumulated, CancellationToken ct)
    {
        var sequence = new ReadOnlySequence<byte>(accumulated.ToArray());

        while (true)
        {
            ProtocolMessage msg;
            try
            {
                msg = _protocol.TryParse(ref sequence) ?? default;
                if (msg.Equals(default(ProtocolMessage)))
                    break;
            }
            catch (ProtocolException ex)
            {
                _logger.LogWarning("协议解析错误: {Message}", ex.Message);
                return false; // 通知调用方断开连接
            }

            // 回填连接 ID
            msg = msg with { ConnectionId = conn.Id };

            _logger.LogDebug("收到 [{Id}]: {Content}", conn.Id, msg.RawContent);

            // 路由消息
            RouteResult result;
            try
            {
                result = _router.Route(msg.ConnectionId, msg.RawContent);
            }
            catch (ProtocolException ex)
            {
                _logger.LogWarning("业务协议错误: {Message}", ex.Message);
                return false; // 通知调用方断开此连接
            }
            await ExecuteActionsAsync(result);
        }

        // 重建未消费的数据
        accumulated.Clear();
        if (sequence.Length > 0)
            accumulated.AddRange(sequence.ToArray());

        return true;
    }

    // ─── 执行输出动作 ───

    private async Task ExecuteActionsAsync(RouteResult result)
    {
        foreach (var action in result.Actions)
        {
            switch (action)
            {
                case OutgoingAction.Send send:
                    await SendToConnectionAsync(send.ConnectionId, send.Content);
                    await Task.Delay(100);
                    break;

                case OutgoingAction.BroadcastToChat broadcast:
                    await BroadcastToChatAsync(
                        broadcast.ChatId, broadcast.Content, broadcast.ExcludeConnectionId);
                    await Task.Delay(100);
                    break;

                case OutgoingAction.Disconnect disconnect:
                    // 关闭连接，后续清理由 HandleClientAsync 的 finally 处理
                    var conn = _connections.GetConnection(disconnect.ConnectionId);
                    conn?.Disconnect();
                    break;
            }
        }
    }

    private async Task SendToConnectionAsync(long connectionId, string content)
    {
        var conn = _connections.GetConnection(connectionId);
        if (conn == null || !conn.IsConnected)
            return;

        var bytes = _protocol.Encode(new ProtocolMessage(content, connectionId));
        try
        {
            await conn.SendAsync(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("发送到 {Id} 失败: {Message}", connectionId, ex.Message);
        }
    }

    private async Task BroadcastToChatAsync(string chatId, string content, long excludeConnectionId)
    {
        var room = _chatRooms.FindRoom(chatId);
        if (room == null) return;

        var targetIds = room.GetOtherConnectionIds(excludeConnectionId);
        var tasks = targetIds.Select(id => SendToConnectionAsync(id, content));
        await Task.WhenAll(tasks);
    }

    private async Task BroadcastGlobalAsync(string content)
    {
        var allConns = _chatRooms.GetAllConnections();
        var tasks = allConns
            .Select(c => SendToConnectionAsync(c.connectionId, content));
        await Task.WhenAll(tasks);
    }

    // ─── 清理 ───

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _serverCts?.Dispose();
        _listener?.Dispose();
    }
}

/// <summary>
/// 服务配置选项。
/// </summary>
public class ChatServerOptions
{
    /// <summary>绑定地址，默认 127.0.0.1</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>监听端口，默认 10035（与旧版一致）</summary>
    public int Port { get; set; } = 10035;

    /// <summary>心跳超时秒数，-1 表示禁用，默认 30</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>
    /// 协议类型: <c>"Legacy"</c> (旧版兼容, UTF-16 LE, 无分帧) 或
    /// <c>"Modern"</c> (UTF-8, \n 分帧)，默认 <c>"Legacy"</c>。
    /// </summary>
    public string Protocol { get; set; } = "Legacy";
}
