namespace TChatS.Transport;

/// <summary>
/// 抽象的网络连接，表示与一个远程端点的通信通道。
/// </summary>
public interface IConnection
{
    /// <summary>
    /// 连接的唯一标识符。
    /// </summary>
    long Id { get; }

    /// <summary>
    /// 连接是否仍然活跃。
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 远程端点地址（用于日志）。
    /// </summary>
    string RemoteEndPoint { get; }

    /// <summary>
    /// 异步发送数据。
    /// </summary>
    /// <param name="data">要发送的字节数据</param>
    /// <param name="ct">取消令牌</param>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// 断开连接并释放资源。
    /// </summary>
    void Disconnect();
}
