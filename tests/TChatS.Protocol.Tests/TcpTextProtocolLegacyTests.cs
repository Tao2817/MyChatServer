using System.Buffers;
using System.Text;
using Xunit;

namespace TChatS.Protocol.Tests;

public class TcpTextProtocolLegacyTests
{
    private readonly TcpTextProtocolLegacy _protocol = new();

    [Fact]
    public void TryParse_CompleteMessage_ReturnsEntireBuffer()
    {
        // 模拟客户端发送的登录消息 (UTF-16 LE 宽字节)
        var buffer = CreateBuffer("#2Ui1n+-#Tao2817@1234>Room1");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("#2Ui1n+-#Tao2817@1234>Room1", result.Value.RawContent);
        Assert.Equal(0, buffer.Length); // 全部消费
    }

    [Fact]
    public void TryParse_EmptyBuffer_ReturnsNull()
    {
        var buffer = ReadOnlySequence<byte>.Empty;
        var result = _protocol.TryParse(ref buffer);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_ChatMessage_ReturnsFullContent()
    {
        var buffer = CreateBuffer("Hello World!");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("Hello World!", result.Value.RawContent);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryParse_PrivateMessage_ReturnsRawCommand()
    {
        // #->7 私聊指令: #->7TargetUser#->Message
        var buffer = CreateBuffer("#->7Tao2817#->Hello!");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("#->7Tao2817#->Hello!", result.Value.RawContent);
    }

    [Fact]
    public void TryParse_ExceedsMaxSize_ThrowsProtocolException()
    {
        var protocol = new TcpTextProtocolLegacy { MaxMessageSize = 10 };
        var bytes = new byte[20]; // 超出 10 字节限制
        new Random(42).NextBytes(bytes);
        var buffer = new ReadOnlySequence<byte>(bytes);

        Assert.Throws<ProtocolException>(() => protocol.TryParse(ref buffer));
    }

    [Fact]
    public void TryParse_AtMaxSize_Succeeds()
    {
        var protocol = new TcpTextProtocolLegacy { MaxMessageSize = 200 };
        // UTF-16 LE: 100 字符 = 200 字节，恰好等于 MaxMessageSize
        var content = new string('A', 100);
        var bytes = Encoding.Unicode.GetBytes(content);
        var buffer = new ReadOnlySequence<byte>(bytes);

        var result = protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal(100, result.Value.RawContent.Length);
    }

    [Fact]
    public void Encode_UsesUtf16LE_NoDelimiter()
    {
        var msg = new ProtocolMessage("Hello", ConnectionId: 42);
        var encoded = _protocol.Encode(msg);

        // UTF-16 LE: ASCII 字符 = 原始字节 + 0x00 padding
        // "Hello" → 5 chars × 2 bytes = 10 bytes
        Assert.Equal(10, encoded.Length);

        // 验证 UTF-16 LE 编码
        var decoded = Encoding.Unicode.GetString(encoded.Span);
        Assert.Equal("Hello", decoded);
    }

    [Fact]
    public void Encode_NoNewlineSuffix()
    {
        var msg = new ProtocolMessage("test", ConnectionId: 0);
        var encoded = _protocol.Encode(msg);

        // 不应以 \n 结尾
        Assert.NotEqual((byte)'\n', encoded.Span[^1]);
    }

    [Fact]
    public void Encode_ChineseCharacters_Utf16LE()
    {
        var msg = new ProtocolMessage("你好", ConnectionId: 0);
        var encoded = _protocol.Encode(msg);

        // UTF-16 LE: 每个中文字符 2 字节
        Assert.Equal(4, encoded.Length);
        var decoded = Encoding.Unicode.GetString(encoded.Span);
        Assert.Equal("你好", decoded);
    }

    [Fact]
    public void Encode_SendEncodingMatchesOriginalBehavior()
    {
        // 原始服务端: Send(msg, msg.GetLength() * 2)
        // CString "ABC" → GetLength() = 3, GetLength()*2 = 6 bytes
        var msg = new ProtocolMessage("ABC", ConnectionId: 0);
        var encoded = _protocol.Encode(msg);

        Assert.Equal(6, encoded.Length); // 3 chars × 2 bytes (UTF-16 LE)
    }

    [Fact]
    public void Encode_ProtocolCommand_PreservesContent()
    {
        // #->0 重新登录成功
        var msg = new ProtocolMessage("#->0", ConnectionId: 0);
        var encoded = _protocol.Encode(msg);

        var decoded = Encoding.Unicode.GetString(encoded.Span);
        Assert.Equal("#->0", decoded);
    }

    [Fact]
    public void RoundTrip_LoginMessage_PreservesContent()
    {
        var original = "#2Ui1n+-#User@Pass>Room1";
        var msg = new ProtocolMessage(original, ConnectionId: 0);

        // 客户端和服务端统一使用 UTF-16 LE
        // Encode 和 TryParse 都使用 Encoding.Unicode
        var encoded = _protocol.Encode(msg);
        var buffer = new ReadOnlySequence<byte>(encoded);
        var parsed = _protocol.TryParse(ref buffer);

        Assert.NotNull(parsed);
        Assert.Equal(original, parsed.Value.RawContent);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void CustomEncoding_CanOverrideDefaults()
    {
        // 可以配置编码以匹配不同的客户端
        var protocol = new TcpTextProtocolLegacy
        {
            ReceiveEncoding = Encoding.ASCII,
            SendEncoding = Encoding.UTF8
        };

        var buffer = CreateBuffer("test123", Encoding.ASCII);
        var result = protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("test123", result.Value.RawContent);
    }

    [Fact]
    public void ReceiveEncoding_Utf8_HandlesChinese()
    {
        // 测试如果客户端以 UTF-8 发送中文（非标准，覆盖 ReceiveEncoding 时使用）
        var protocol = new TcpTextProtocolLegacy
        {
            ReceiveEncoding = Encoding.UTF8
        };

        var buffer = CreateBuffer("你好世界", Encoding.UTF8);
        var result = protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("你好世界", result.Value.RawContent);
    }

    // --- Helpers ---

    private static ReadOnlySequence<byte> CreateBuffer(string text, Encoding? encoding = null)
    {
        var enc = encoding ?? Encoding.Unicode; // 旧版默认 UTF-16 LE
        var bytes = enc.GetBytes(text);
        return new ReadOnlySequence<byte>(bytes);
    }
}
