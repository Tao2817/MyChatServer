using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TChatS.Protocol;

/// <summary>
/// JSON 协议的 <see cref="IServiceProtocol"/> 实现。
/// 统一格式: <c>{"type":"...","args":{...}}</c>，紧凑单行。
/// </summary>
public sealed class JsonServiceProtocol : IServiceProtocol
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // ─── Parse：客户端 → 服务端 ───

    /// <inheritdoc />
    public ParsedClientMessage Parse(string rawContent)
    {
        ArgumentNullException.ThrowIfNull(rawContent);

        using var doc = JsonDocument.Parse(rawContent);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString()
            ?? throw new ProtocolException("JSON 消息缺少 type 字段。");

        var args = root.TryGetProperty("args", out var a)
            ? a
            : throw new ProtocolException("JSON 消息缺少 args 字段。");

        return type switch
        {
            "login"   => ToParsed(ClientMessageType.Login,       args.Deserialize<LoginJsonArgs>(JsonOpts)!.ToDomain()),
            "private" => ToParsed(ClientMessageType.PrivateChat, args.Deserialize<PrivateJsonArgs>(JsonOpts)!.ToDomain()),
            "normal"  => ToParsed(ClientMessageType.NormalChat,  args.Deserialize<NormalJsonArgs>(JsonOpts)!.ToDomain()),
            _ => throw new ProtocolException($"未知的客户端消息类型: {type}")
        };
    }

    private static ParsedClientMessage ToParsed(ClientMessageType type, ClientMessageArgs args)
        => new(type, args);

    // ─── Format：服务端 → 客户端 ───

    /// <inheritdoc />
    public string ReloginSuccess()    => Fmt("relogin",         new EmptyJsonArgs());
    /// <inheritdoc />
    public string WrongPassword()     => Fmt("wrongPassword",   new EmptyJsonArgs());
    /// <inheritdoc />
    public string NewUser()           => Fmt("newUser",         new EmptyJsonArgs());
    /// <inheritdoc />
    public string ServerShutdown()    => Fmt("serverShutdown",  new EmptyJsonArgs());

    /// <inheritdoc />
    public string UserList(IEnumerable<string> userNames)
        => Fmt("userList", new UserListJsonArgs(userNames.ToArray()));

    /// <inheritdoc />
    public string UserJoin(string userName)
        => Fmt("userJoin", new SingleUserJsonArgs(userName));

    /// <inheritdoc />
    public string UserLeave(string userName)
        => Fmt("userLeave", new SingleUserJsonArgs(userName));

    /// <inheritdoc />
    public string ServerMessage(string content)
        => Fmt("serverMessage", new ContentJsonArgs(content));

    /// <inheritdoc />
    public string ClientNormalMessage(string userName, string content)
        => Fmt("chat", new UserMessageJsonArgs(userName, content));

    /// <inheritdoc />
    public string ClientPrivateMessage(string senderName, string content)
        => Fmt("dispatchPrivate", new UserMessageJsonArgs(senderName, content));

    // ─── 序列化辅助 ───

    private static string Fmt(string type, object args)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("type", type);
        writer.WritePropertyName("args");
        JsonSerializer.Serialize(writer, args, args.GetType(), JsonOpts);
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
