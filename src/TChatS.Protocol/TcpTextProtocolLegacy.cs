using System.Buffers;
using System.Text;

namespace TChatS.Protocol;

/// <summary>
/// 完全依照旧版 TChatServer 原始协议的解析器。
///
/// 与旧版 CAsyncSocket 行为一致:
/// <list type="bullet">
///   <item><b>接收 (C→S):</b> 无分帧 — 每次 Receive 收到的数据即为一条完整消息。
///        旧版客户端发送窄字节 (ANSI/ASCII)，服务端用 <c>CString.Format("%s", char_buffer)</c> 转宽字符。</item>
///   <item><b>发送 (S→C):</b> 无分帧 — 每条消息直接转为 UTF-16 LE 字节发送。
///        旧版服务端用 <c>Send(msg, msg.GetLength() * 2)</c> 发送宽字符字节。</item>
///   <item><b>最大长度:</b> 固定 200 字节，与旧版 <c>Receive(Rmessage, 200)</c> 一致。</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>使用说明:</b> 旧版协议无消息分隔符，传输层<b>不应</b>在两次 <see cref="TryParse"/> 之间累积数据。
/// 每次 Socket 读取后应立即调用 <see cref="TryParse"/>，消费全部数据作为一条消息。
///
/// <b>已知限制 (与旧版一致):</b>
/// <list type="bullet">
///   <item>如果 TCP 粘包导致两条消息在一次 Receive 中到达，它们会被错误地合并为一条。</item>
///   <item>旧版通过在多次 Send 之间插入 <c>Sleep(70)</c> 来降低粘包概率（非可靠方案）。</item>
/// </list>
/// </remarks>
public class TcpTextProtocolLegacy : IProtocolParser
{
    /// <summary>
    /// 接收缓冲区最大字节数，与旧版 Rmessage[200] 一致。
    /// </summary>
    public int MaxMessageSize { get; init; } = 200;

    /// <summary>
    /// 接收 (客户端→服务端) 使用的编码。
    /// 默认 UTF-8，兼容旧版客户端的窄字节发送 (ASCII 兼容)。
    /// </summary>
    public Encoding ReceiveEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// 发送 (服务端→客户端) 使用的编码。
    /// 默认 UTF-16 LE，与旧版 <c>Send(msg, msg.GetLength() * 2)</c> 行为一致。
    /// </summary>
    public Encoding SendEncoding { get; init; } = Encoding.Unicode;

    /// <inheritdoc />
    /// <remarks>
    /// 旧版协议无消息分隔符。此方法始终将缓冲区中<b>全部数据</b>作为一条消息消费。
    /// 仅当缓冲区为空时返回 <c>null</c>。
    /// 若缓冲区超出 <see cref="MaxMessageSize"/> 则抛出 <see cref="ProtocolException"/>。
    /// </remarks>
    public ProtocolMessage? TryParse(ref ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsEmpty)
            return null;

        if (buffer.Length > MaxMessageSize)
        {
            throw new ProtocolException(
                $"消息超出旧版最大长度限制 {MaxMessageSize} 字节，当前 {buffer.Length} 字节。");
        }

        // 旧版协议: 每次 Receive 的全部数据 = 一条完整消息
        var content = ReceiveEncoding.GetString(buffer);
        // ConnectionId 由传输层在拿到消息后回填
        var message = new ProtocolMessage(content, ConnectionId: 0);

        // 消费全部数据
        buffer = buffer.Slice(buffer.End);

        return message;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 使用 UTF-16 LE 编码 (<see cref="Encoding.Unicode"/>)，不添加任何分隔符。
    /// 与旧版 <c>Send(msg, msg.GetLength() * 2)</c> 行为一致。
    /// </remarks>
    public ReadOnlyMemory<byte> Encode(ProtocolMessage message)
    {
        return SendEncoding.GetBytes(message.RawContent);
    }
}
