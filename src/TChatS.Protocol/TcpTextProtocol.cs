using System.Buffers;
using System.Text;

namespace TChatS.Protocol;

/// <summary>
/// TCP + 文本流的协议解析器实现。
/// 使用换行符 <c>'\n'</c> 作为消息分隔符，UTF-8 编码。
/// </summary>
public class TcpTextProtocol : IProtocolParser
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    /// <summary>
    /// 单条消息最大字节数（防止恶意客户端发送超大数据）。
    /// </summary>
    public int MaxMessageSize { get; init; } = 10 * 1024; // 10KB

    /// <inheritdoc />
    public ProtocolMessage? TryParse(ref ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);

        // 查找下一个换行符的位置
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, NewLine, advancePastDelimiter: true))
        {
            // 没有找到完整的行，检查是否超出最大消息大小
            if (buffer.Length > MaxMessageSize)
            {
                throw new ProtocolException(
                    $"消息超出最大长度限制 {MaxMessageSize} 字节，当前 {buffer.Length} 字节。");
            }
            return null;
        }

        // 更新 buffer 指针到已消费的位置
        buffer = buffer.Slice(reader.Position);

        // 解码 UTF-8 文本（去除 \n）
        var content = Encoding.UTF8.GetString(line);
        // 注意：ProtocolMessage.ConnectionId 由调用方（传输层）填充
        return new ProtocolMessage(content, ConnectionId: 0);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encode(ProtocolMessage message)
    {
        // 拼接文本 + '\n'，转为 UTF-8 字节
        var text = message.RawContent + "\n";
        return Encoding.UTF8.GetBytes(text);
    }
}

/// <summary>
/// 协议解析异常。
/// </summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
