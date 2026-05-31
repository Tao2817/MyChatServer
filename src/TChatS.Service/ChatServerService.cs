using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TChatS.Core;
using TChatS.Core.Models;
using TChatS.Protocol;
using TChatS.Transport;
using static TChatS.Service.LogHelper;

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

        Info(_logger, $"TChatServer 已启动 - {_bindAddress}:{_port}");

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

        Info(_logger, "正在关闭服务...");

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
            try { conn.Disconnect(); } catch (Exception ex) { Warn(_logger, ex, "StopAsync: 强制断开连接异常"); }
        }

        Info(_logger, "TChatServer 已停止");
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

                Info(_logger, $"<Listen>: 接收客户的一个连接请求 ({socket.RemoteEndPoint?.ToString() ?? "unknown"})");

                // 注册到连接管理器
                TcpConnection conn;
                try
                {
                    conn = _connections.Accept(socket);
                }
                catch (InvalidOperationException ex)
                {
                    Warn(_logger, ex, $"Accept: {ex.Message}");
                    continue;
                }

                // 启动独立的消息处理任务
                var task = HandleClientAsync(conn, ct);
                _clientTasks.Add(task);
            }
        }
        catch (ObjectDisposedException ex)
        {
            Debug(_logger, $"AcceptLoopAsync: listener 已释放，正常退出 ({ex.Message})");
        }
        catch (Exception ex)
        {
            Error(_logger, ex, "Accept 循环异常");
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
            Warn(_logger, ex, $"HandleClientAsync: 异常, 连接 {conn.Id} ({conn.RemoteEndPoint}): {ex.Message}");
        }
        finally
        {
            // 1. 立即从连接池移除，确保无论后续通知是否成功，连接都不会泄漏
            _connections.RemoveConnection(conn.Id);
            var con_left = _connections.ActiveCount;
            Info(_logger, $"_connections.RemoveConnection, 连接移除 {conn.Id} ({conn.RemoteEndPoint}) {con_left}");
            if (con_left < 20)
            {
                var remaining = _connections.GetAllConnections();
                Info(_logger, $"剩余连接数 {con_left}，详情: {string.Join(", ", remaining.Select(c => $"[{c.Id}] {c.RemoteEndPoint}"))}");
            }

            // 2. 处理用户离开（best-effort，失败不影响连接清理）
            try
            {
                var leaveResult = _router.HandleDisconnect(conn.Id);
                // if (leaveResult.Actions.Count > 0)
                //     await ExecuteActionsAsync(leaveResult);
            }
            catch (Exception ex)
            {
                Warn(_logger, ex, "处理离开事件异常");
            }

            Info(_logger, $"连接 {conn.Id} ({conn.RemoteEndPoint}) 已断开");
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
                Warn(_logger, $"连接 {conn.Id} ({conn.RemoteEndPoint}) 心跳超时 ({_heartbeatSeconds}s)");
                break;
            }

            if (bytesRead == 0)
            {
                Info(_logger, $"连接 {conn.Id} ({conn.RemoteEndPoint}) 远端已关闭连接");
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
                Warn(_logger, ex, $"协议解析错误: {ex.Message}");
                return false; // 通知调用方断开连接
            }

            // 回填连接 ID
            msg = msg with { ConnectionId = conn.Id };

            Debug(_logger, $"收到 [{conn.Id}]: {msg.RawContent}");

            // 路由消息
            RouteResult result;
            try
            {
                result = _router.Route(msg.ConnectionId, msg.RawContent);
            }
            catch (ProtocolException ex)
            {
                Warn(_logger, ex, $"业务协议错误: {ex.Message}");
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
                    break;

                case OutgoingAction.BroadcastToChat broadcast:
                    await BroadcastToChatAsync(
                        broadcast.ChatId, broadcast.Content, broadcast.ExcludeConnectionId);
                    break;

                case OutgoingAction.Disconnect disconnect:
                    // 关闭连接，后续清理由 HandleClientAsync 的 finally 处理
                    var conn = _connections.GetConnection(disconnect.ConnectionId);
                    conn?.Disconnect();
                    break;
            }
        }
    }

    /// <summary>
    /// 向指定连接发送消息。发送失败时会关闭 socket 并从聊天室移除，
    /// 避免后续广播继续尝试向已失效的连接发送。
    /// </summary>
    private async Task SendToConnectionAsync(long connectionId, string content)
    {
        var conn = _connections.GetConnection(connectionId);
        if (conn == null || !conn.IsConnected)
            return;
        if (_chatRooms.FindRoomByConnection(connectionId) == null)
            return;
        var protocolMmsg = new ProtocolMessage(content, connectionId);
        var bytes = _protocol.Encode(new ProtocolMessage(content, connectionId));
        try
        {
            // 设置发送超时，避免在已损坏的 socket 上无限等待 TCP 重传（默认可达数分钟）
            using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await conn.SendAsync(bytes, sendCts.Token);
        }
        catch (Exception ex)
        {
            Warn(_logger, $"发送到 {connectionId} ({conn.RemoteEndPoint}) 失败: {ex.Message}，主动断开该连接, {protocolMmsg}");
            _chatRooms.LeaveRoom(connectionId);
            conn.Disconnect();
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
