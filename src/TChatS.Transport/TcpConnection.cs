using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TChatS.Transport;

/// <summary>
/// TCP 连接实现，封装一个 <see cref="Socket"/>。
/// 出站消息通过内部有界 Channel 串行化，由独立的 writer 任务实际写 socket，
/// 避免业务侧 broadcast 被慢/阻塞客户端拖慢。
/// </summary>
public sealed class TcpConnection : IConnection, IDisposable
{
    private readonly Socket _socket;
    private readonly Channel<ReadOnlyMemory<byte>> _outbound;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _writerCts = new();
    private readonly ILogger<TcpConnection>? _logger;
    private int _disposed;
    private int _disconnected;

    private const int OutboundCapacity = 256;
    private static readonly TimeSpan WriterSendTimeout = TimeSpan.FromSeconds(5);

    public TcpConnection(long id, Socket socket, ILogger<TcpConnection>? logger = null)
    {
        Id = id;
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _logger = logger;
        RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? "(unknown)";

        _outbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    /// <inheritdoc />
    public long Id { get; }

    /// <inheritdoc />
    /// <remarks>
    /// 不依赖 <see cref="Socket.Connected"/>（该属性只在最近一次 Send/Receive 后才反映真实状态，
    /// 远端 RST 后可能仍为 true）。改用内部标记位，<see cref="Disconnect()"/> 后立即变为 false。
    /// </remarks>
    public bool IsConnected => _disposed == 0 && _disconnected == 0;

    /// <inheritdoc />
    public string RemoteEndPoint { get; }

    /// <summary>
    /// 获取底层 <see cref="Socket"/>（仅供传输层内部使用）。
    /// </summary>
    internal Socket InternalSocket => _socket;

    /// <inheritdoc />
    /// <remarks>
    /// 仅入队到出站 Channel 后立即返回，实际 socket I/O 在 writer 任务中完成。
    /// 队列已满或连接已断开时同步抛出 <see cref="InvalidOperationException"/>，
    /// 同时主动 <see cref="Disconnect"/> 该连接，避免慢消费者无限挤占资源。
    /// <paramref name="ct"/> 仅为接口兼容性保留，当前实现下不参与等待。
    /// </remarks>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_disconnected != 0)
            throw new InvalidOperationException("连接已被断开。");

        if (!_outbound.Writer.TryWrite(data))
        {
            _logger?.LogDebug("SendAsync: 出站通道已满或已关闭 ({Id})", Id);
            Disconnect();
            throw new InvalidOperationException("出站通道已满或已关闭。");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 异步从 Socket 接收数据到缓冲区。
    /// </summary>
    /// <param name="buffer">接收缓冲区</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>接收到的字节数。返回 0 表示对端已关闭连接、连接已断开、或 Socket 已释放。</returns>
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_disposed != 0 || _disconnected != 0)
            return 0;

        try
        {
            return await _socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            _logger?.LogDebug("ReceiveAsync: Socket 已被释放 ({Id}): {Message}", Id, ex.Message);
            return 0;
        }
        catch (SocketException ex)
        {
            // 客户端 RST（非优雅关闭）。优雅 FIN 走 return 0 不进这里。
            _logger?.LogInformation(
                "ReceiveAsync: 连接被对端强制重置 ({Id} {Remote}): {SocketError} - {Message}",
                Id, RemoteEndPoint, ex.SocketErrorCode, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// 出站 writer：从 Channel 读出消息并写到 socket。每条消息有独立的发送超时；
    /// 任一发送失败/超时即关闭连接，避免慢客户端无限拖延。
    /// </summary>
    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var data in _outbound.Reader.ReadAllAsync(_writerCts.Token).ConfigureAwait(false))
            {
                try
                {
                    using var sendCts = new CancellationTokenSource(WriterSendTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_writerCts.Token, sendCts.Token);
                    await _socket.SendAsync(data, SocketFlags.None, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_writerCts.IsCancellationRequested)
                {
                    break; // 连接已显式关闭，正常退出
                }
                catch (OperationCanceledException)
                {
                    // sendCts 触发，对端 ACK 太慢导致单次 send 5 秒还没完成
                    _logger?.LogWarning(
                        "WriterLoop: 单次 send 超时 ({WriterTimeoutSec}s)，对端处理慢 ({Id} {Remote})，主动断开",
                        (int)WriterSendTimeout.TotalSeconds, Id, RemoteEndPoint);
                    Disconnect();
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        "WriterLoop: 出站 send 失败 ({Id} {Remote}): {Type} - {Message}，主动断开",
                        Id, RemoteEndPoint, ex.GetType().Name, ex.Message);
                    Disconnect();
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* writer 取消，正常 */ }
        catch (Exception ex)
        {
            _logger?.LogDebug("WriterLoop: 异常退出 ({Id}): {Message}", Id, ex.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 设置断开标记、关闭出站通道、取消 writer，并 shutdown TCP 通道。
    /// 不释放 Socket 句柄（由 <see cref="Dispose()"/> 统一处理）。幂等：重复调用不会重复操作。
    /// </remarks>
    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
            return;

        _outbound.Writer.TryComplete();
        try { _writerCts.Cancel(); } catch (ObjectDisposedException) { }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Disconnect: Shutdown 异常 ({Id}): {Message}", Id, ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _disconnected, 1);
            _outbound.Writer.TryComplete();
            try { _writerCts.Cancel(); } catch (ObjectDisposedException) { }
            try { _socket.Dispose(); }
            catch (Exception ex)
            {
                _logger?.LogDebug("Dispose: Socket 释放异常 ({Id}): {Message}", Id, ex.Message);
            }
            try { _writerCts.Dispose(); } catch { }
        }
    }
}
