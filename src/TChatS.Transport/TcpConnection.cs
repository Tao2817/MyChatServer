using System.Net.Sockets;

namespace TChatS.Transport;

/// <summary>
/// TCP 连接实现，封装一个 <see cref="Socket"/>。
/// </summary>
public sealed class TcpConnection : IConnection, IDisposable
{
    private readonly Socket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public TcpConnection(long id, Socket socket)
    {
        Id = id;
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? "(unknown)";
    }

    /// <inheritdoc />
    public long Id { get; }

    /// <inheritdoc />
    public bool IsConnected => _disposed == 0 && _socket.Connected;

    /// <inheritdoc />
    public string RemoteEndPoint { get; }

    /// <summary>
    /// 获取底层 <see cref="Socket"/>（仅供传输层内部使用）。
    /// </summary>
    internal Socket InternalSocket => _socket;

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(TcpConnection));

        // Socket.SendAsync 不是线程安全的，使用 SemaphoreSlim 串行化
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 异步从 Socket 接收数据到缓冲区。
    /// </summary>
    /// <param name="buffer">接收缓冲区</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>接收到的字节数。返回 0 表示对端已关闭连接或 Socket 已释放。</returns>
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_disposed != 0)
            return 0;

        try
        {
            return await _socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return 0; // Socket 已被 Dispose (例如 Disconnect 关闭后)
        }
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        try
        {
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
                _socket.Close();
            }
        }
        catch
        {
            // 静默处理断开时的异常
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _socket.Dispose(); } catch { /* 静默处理 */ }
            try { _sendLock.Dispose(); } catch { /* 静默处理 */ }
        }
    }
}
