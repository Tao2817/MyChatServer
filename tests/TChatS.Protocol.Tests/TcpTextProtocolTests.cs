using System.Buffers;
using System.Text;
using Xunit;

namespace TChatS.Protocol.Tests;

public class TcpTextProtocolTests
{
    private readonly TcpTextProtocol _protocol = new();

    [Fact]
    public void TryParse_SingleMessage_ReturnsParsedMessage()
    {
        var buffer = CreateBuffer("Hello World\n");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("Hello World", result.Value.RawContent);
        Assert.Equal(0, buffer.Length); // buffer 应完全消费
    }

    [Fact]
    public void TryParse_EmptyMessage_ReturnsEmptyContent()
    {
        var buffer = CreateBuffer("\n");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("", result.Value.RawContent);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryParse_NoNewline_ReturnsNull()
    {
        var buffer = CreateBuffer("incomplete");
        var result = _protocol.TryParse(ref buffer);

        Assert.Null(result);
        // buffer 不应被消费
        Assert.Equal("incomplete".Length, buffer.Length);
    }

    [Fact]
    public void TryParse_MultipleMessages_ParsesOnlyFirst()
    {
        var buffer = CreateBuffer("first\nsecond\nthird\n");
        var results = new List<string>();

        while (true)
        {
            var result = _protocol.TryParse(ref buffer);
            if (result == null) break;
            results.Add(result.Value.RawContent);
        }

        Assert.Equal(3, results.Count);
        Assert.Equal("first", results[0]);
        Assert.Equal("second", results[1]);
        Assert.Equal("third", results[2]);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryParse_Utf8Chinese_ReturnsCorrectContent()
    {
        var buffer = CreateBuffer("你好世界\n");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("你好世界", result.Value.RawContent);
    }

    [Fact]
    public void TryParse_ProtocolCommand_ReturnsRawCommand()
    {
        var buffer = CreateBuffer("#->7Tao#->Hello!\n");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("#->7Tao#->Hello!", result.Value.RawContent);
    }

    [Fact]
    public void TryParse_LoginMessage_ReturnsRawLogin()
    {
        var buffer = CreateBuffer("#2Ui1n+-#Tao2817@1234>Room1\n");
        var result = _protocol.TryParse(ref buffer);

        Assert.NotNull(result);
        Assert.Equal("#2Ui1n+-#Tao2817@1234>Room1", result.Value.RawContent);
    }

    [Fact]
    public void TryParse_ExceedsMaxSize_ThrowsProtocolException()
    {
        var protocol = new TcpTextProtocol { MaxMessageSize = 10 };
        var longData = new string('A', 20); // 20 bytes, exceeds 10
        var buffer = CreateBuffer(longData);

        Assert.Throws<ProtocolException>(() => protocol.TryParse(ref buffer));
    }

    [Fact]
    public void Encode_SimpleMessage_AppendsNewline()
    {
        var msg = new ProtocolMessage("Hello", ConnectionId: 42);
        var encoded = _protocol.Encode(msg);

        var text = Encoding.UTF8.GetString(encoded.Span);
        Assert.Equal("Hello\n", text);
    }

    [Fact]
    public void Encode_EmptyMessage_OutputsOnlyNewline()
    {
        var msg = new ProtocolMessage("", ConnectionId: 0);
        var encoded = _protocol.Encode(msg);

        var text = Encoding.UTF8.GetString(encoded.Span);
        Assert.Equal("\n", text);
    }

    [Fact]
    public void Encode_ChineseMessage_PreservesUtf8()
    {
        var msg = new ProtocolMessage("你好", ConnectionId: 0);
        var encoded = _protocol.Encode(msg);

        var text = Encoding.UTF8.GetString(encoded.Span);
        Assert.Equal("你好\n", text);
    }

    [Fact]
    public void TryParse_Encode_RoundTrip_PreservesContent()
    {
        var original = new ProtocolMessage("#->5Tao#Bob#Alice", ConnectionId: 99);
        var encoded = _protocol.Encode(original);
        var buffer = new ReadOnlySequence<byte>(encoded);

        var parsed = _protocol.TryParse(ref buffer);

        Assert.NotNull(parsed);
        Assert.Equal(original.RawContent, parsed.Value.RawContent);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryParse_EmptyBuffer_ReturnsNull()
    {
        var buffer = ReadOnlySequence<byte>.Empty;
        var result = _protocol.TryParse(ref buffer);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_MultipleRounds_AccumulatesCorrectly()
    {
        // 模拟分两次接收一条消息：先收到一半，再收到另一半
        var part1 = Encoding.UTF8.GetBytes("Hello Wo");
        var part2 = Encoding.UTF8.GetBytes("rld\n");

        // 第一次：数据不完整
        var buffer = new ReadOnlySequence<byte>(part1);
        var result1 = _protocol.TryParse(ref buffer);
        Assert.Null(result1);

        // 第二次：追加后半段（模拟真实场景中合并 buffer）
        var combined = new byte[part1.Length + part2.Length];
        part1.CopyTo(combined, 0);
        part2.CopyTo(combined, part1.Length);
        buffer = new ReadOnlySequence<byte>(combined);

        var result2 = _protocol.TryParse(ref buffer);
        Assert.NotNull(result2);
        Assert.Equal("Hello World", result2.Value.RawContent);
    }

    // --- Helper ---

    private static ReadOnlySequence<byte> CreateBuffer(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new ReadOnlySequence<byte>(bytes);
    }
}
