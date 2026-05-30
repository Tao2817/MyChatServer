using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace TChatS.Protocol;

/// <summary>
/// JSON 协议的传输层实现。使用 4 字节大端长度前缀分帧。
/// </summary>
public class TcpJsonProtocol : IProtocolParser
{
    /// <summary>单条消息最大字节数，默认 64 KB。</summary>
    public int MaxMessageSize { get; init; } = 65536;

    /// <inheritdoc />
    public ProtocolMessage? TryParse(ref ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < 4)
            return null;

        // 读取 4 字节大端长度
        Span<byte> lenBytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lenBytes);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(lenBytes);

        if (payloadLength < 0)
            throw new ProtocolException($"非法的帧长度: {payloadLength}");

        if (payloadLength > MaxMessageSize)
        {
            throw new ProtocolException(
                $"消息超出最大长度限制 {MaxMessageSize} 字节，当前 {payloadLength} 字节。");
        }

        var totalFrame = 4 + payloadLength;
        if (buffer.Length < totalFrame)
            return null;

        var content = Encoding.UTF8.GetString(buffer.Slice(4, payloadLength));
        buffer = buffer.Slice(totalFrame);

        return new ProtocolMessage(content, ConnectionId: 0);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encode(ProtocolMessage message)
    {
        var payload = Encoding.UTF8.GetBytes(message.RawContent);

        if (payload.Length > MaxMessageSize)
        {
            throw new ProtocolException(
                $"消息超出最大长度限制 {MaxMessageSize} 字节，当前 {payload.Length} 字节。");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        return frame;
    }
}
