using System.Buffers;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private long _nextClientTaskId;
    private int _stopped;

    /// <summary>
    /// StopAsync 等待客户端任务收尾的最长时间。
    /// 必须 ≥ TcpConnection 的 writer 发送超时，否则 socket I/O 还没超时就被强制关。
    /// </summary>
    private static readonly TimeSpan StopGraceTimeout = TimeSpan.FromSeconds(7);

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

        // 广播 #->3 服务器关闭到所有客户端（仅入队，立即返回）
        BroadcastGlobal(_fmt.ServerShutdown());

        // 等待所有客户端任务收尾。超时必须 ≥ writer 的 send 超时，
        // 否则 socket I/O 还没机会超时就被强制关，看起来像连接残留。
        if (!_clientTasks.IsEmpty)
        {
            var pending = _clientTasks.Values.ToArray();
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(StopGraceTimeout));
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

                // 启动独立的消息处理任务，注册到字典并在完成时自移除
                var clientTaskId = Interlocked.Increment(ref _nextClientTaskId);
                var task = HandleClientAsync(conn, ct);
                _clientTasks.TryAdd(clientTaskId, task);
                _ = task.ContinueWith(
                    _ => _clientTasks.TryRemove(clientTaskId, out Task? _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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
            ExecuteActions(result);
        }

        // 重建未消费的数据
        accumulated.Clear();
        if (sequence.Length > 0)
            accumulated.AddRange(sequence.ToArray());

        return true;
    }

    // ─── 执行输出动作 ───
    //
    // 现在所有 send/broadcast 都只负责把字节入队到 TcpConnection 的出站 Channel，
    // 实际 socket I/O 由各连接的 writer 任务异步完成。因此这里全部是同步快速操作，
    // sender 的 ReadLoop 不再被慢/卡住的目标连接拖住。

    private void ExecuteActions(RouteResult result)
    {
        foreach (var action in result.Actions)
        {
            switch (action)
            {
                case OutgoingAction.Send send:
                    SendToConnection(send.ConnectionId, send.Content);
                    break;

                case OutgoingAction.BroadcastToChat broadcast:
                    BroadcastToChat(
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
    /// 向单个目标入队一条消息（先编码再分发）。仅用于 1:1 的 Send 动作；
    /// 广播路径请用 <see cref="EnqueueBytes"/> 直接复用同一份 bytes。
    /// </summary>
    private void SendToConnection(long connectionId, string content)
    {
        var bytes = _protocol.Encode(new ProtocolMessage(content, ConnectionId: 0));
        EnqueueBytes(connectionId, bytes);
    }

    /// <summary>
    /// 把已编码好的字节入队给指定连接。入队失败 → 主动断开，由 HandleClientAsync 的
    /// finally 统一清理（移除 _connections + LeaveRoom）。
    /// </summary>
    private void EnqueueBytes(long connectionId, ReadOnlyMemory<byte> bytes)
    {
        var conn = _connections.GetConnection(connectionId);
        if (conn == null || !conn.IsConnected)
            return;
        if (_chatRooms.FindRoomByConnection(connectionId) == null)
            return;

        try
        {
            conn.SendAsync(bytes); // 仅入队，立即返回；失败时同步抛 InvalidOperationException
        }
        catch (Exception ex)
        {
            Warn(_logger, $"入队到 {connectionId} ({conn.RemoteEndPoint}) 失败: {ex.Message}，主动断开");
            _chatRooms.LeaveRoom(connectionId);
            conn.Disconnect();
        }
    }

    private void BroadcastToChat(string chatId, string content, long excludeConnectionId)
    {
        var room = _chatRooms.FindRoom(chatId);
        if (room == null) return;

        // 编码一次，所有目标共享同一份字节，避免 N 倍重复 Encode + GC 分配
        var bytes = _protocol.Encode(new ProtocolMessage(content, ConnectionId: 0));
        foreach (var id in room.GetOtherConnectionIds(excludeConnectionId))
            EnqueueBytes(id, bytes);
    }

    private void BroadcastGlobal(string content)
    {
        var bytes = _protocol.Encode(new ProtocolMessage(content, ConnectionId: 0));
        foreach (var (connectionId, _) in _chatRooms.GetAllConnections())
            EnqueueBytes(connectionId, bytes);
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
