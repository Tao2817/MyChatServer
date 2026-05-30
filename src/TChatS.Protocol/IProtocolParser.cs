using System.Buffers;

namespace TChatS.Protocol;

/// <summary>
/// 协议解析器接口。
/// 不绑定任何具体的业务层协议或网络层协议。
/// 实现类负责处理消息分帧和编解码。
/// </summary>
public interface IProtocolParser
{
    /// <summary>
    /// 尝试从字节缓冲区中解析出一条完整的消息。
    /// 返回 <c>null</c> 表示数据不足，需要继续接收。
    /// 成功解析后，实现应从缓冲区中 consume 已解析的数据。
    /// </summary>
    /// <param name="buffer">可读字节序列。解析成功后实现应 advance 此 buffer。</param>
    /// <returns>解析出的 <see cref="ProtocolMessage"/>，或 null 表示数据不完整。</returns>
    ProtocolMessage? TryParse(ref ReadOnlySequence<byte> buffer);

    /// <summary>
    /// 将业务层消息编码为可发送的字节数据。
    /// </summary>
    /// <param name="message">待编码的消息</param>
    /// <returns>编码后的字节数据（含帧分隔符）</returns>
    ReadOnlyMemory<byte> Encode(ProtocolMessage message);
}
