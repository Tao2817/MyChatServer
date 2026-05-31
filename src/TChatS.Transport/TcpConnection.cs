using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TChatS.Transport;

/// <summary>
/// TCP 连接实现，封装一个 <see cref="Socket"/>。
/// </summary>
public sealed class TcpConnection : IConnection, IDisposable
{
    private readonly Socket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ILogger<TcpConnection>? _logger;
    private int _disposed;
    private int _disconnected;

    public TcpConnection(long id, Socket socket, ILogger<TcpConnection>? logger = null)
    {
        Id = id;
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _logger = logger;
        RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? "(unknown)";
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
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_disconnected != 0)
            throw new InvalidOperationException("连接已被断开。");

        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            _logger?.LogDebug("SendAsync: 连接已被释放，无法获取发送锁 ({Id}): {Message}", Id, ex.Message);
            throw new InvalidOperationException("连接已被释放。");
        }

        try
        {
            await _socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            _logger?.LogDebug("SendAsync: Socket 已被释放 ({Id}): {Message}", Id, ex.Message);
            throw new InvalidOperationException("连接已被释放。");
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException ex)
            {
                _logger?.LogDebug("SendAsync: 释放发送锁时连接已被释放 ({Id}): {Message}", Id, ex.Message);
            }
        }
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
            _logger?.LogDebug("ReceiveAsync: 连接异常断开 ({Id}): {Message}", Id, ex.Message);
            return 0;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 设置断开标记并 shutdown TCP 通道，但不释放 Socket 句柄——
    /// 释放由 <see cref="Dispose()"/> 统一处理。
    /// 幂等：重复调用不会重复操作。
    /// </remarks>
    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
            return; // 已经断开

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
            try { _socket.Dispose(); }
            catch (Exception ex)
            {
                _logger?.LogDebug("Dispose: Socket 释放异常 ({Id}): {Message}", Id, ex.Message);
            }
            try { _sendLock.Dispose(); }
            catch (Exception ex)
            {
                _logger?.LogDebug("Dispose: SemaphoreSlim 释放异常 ({Id}): {Message}", Id, ex.Message);
            }
        }
    }
}
