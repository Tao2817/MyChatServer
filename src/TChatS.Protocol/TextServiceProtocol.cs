namespace TChatS.Protocol;

/// <summary>
/// 旧版 TCP 文本协议的 <see cref="IServiceProtocol"/> 实现。
/// 所有指令格式: <c>#-&gt;N[args]</c>，与 TChatServer_old 完全兼容。
/// </summary>
public sealed class TextServiceProtocol : IServiceProtocol
{
    // ─── 协议常量 ───

    private const string Prefix = "#->";
    private const string LoginMagic = "#2Ui1n+-#";
    private const string PrivatePrefix = "#->7";
    private const string PrivateDelimiter = "#->";

    // ─── Parse：客户端 → 服务端 ───

    /// <inheritdoc />
    public ParsedClientMessage Parse(string rawContent)
    {
        ArgumentNullException.ThrowIfNull(rawContent);

        // 登录消息: #2Ui1n+-#UserName@Password>ChatID
        if (rawContent.StartsWith(LoginMagic, StringComparison.Ordinal))
            return ParseLogin(rawContent);

        // 私聊消息: #->7TargetName#->Content
        if (rawContent.StartsWith(PrivatePrefix, StringComparison.Ordinal))
            return ParsePrivateChat(rawContent);

        // 默认: 普通群聊消息
        return new ParsedClientMessage(ClientMessageType.NormalChat, new NormalChatArgs(rawContent));
    }

    private static ParsedClientMessage ParseLogin(string rawContent)
    {
        var payload = rawContent[LoginMagic.Length..];

        var atIdx = payload.IndexOf('@');
        var gtIdx = payload.IndexOf('>');

        if (atIdx > 0 && gtIdx > atIdx + 1)
        {
            var userName = payload[..atIdx];
            var password = payload[(atIdx + 1)..gtIdx];
            var chatId = payload[(gtIdx + 1)..];

            if (!string.IsNullOrEmpty(userName)
                && !string.IsNullOrEmpty(password)
                && !string.IsNullOrEmpty(chatId))
            {
                return new ParsedClientMessage(
                    ClientMessageType.Login,
                    new LoginArgs(userName, password, chatId));
            }
        }

        // 格式无效 → 降级为普通消息
        return new ParsedClientMessage(ClientMessageType.NormalChat, new NormalChatArgs(rawContent));
    }

    private static ParsedClientMessage ParsePrivateChat(string rawContent)
    {
        var payload = rawContent[PrivatePrefix.Length..];
        var delimIdx = payload.IndexOf(PrivateDelimiter, StringComparison.Ordinal);

        if (delimIdx > 0)
        {
            var target = payload[..delimIdx];
            var content = payload[(delimIdx + PrivateDelimiter.Length)..];

            if (!string.IsNullOrEmpty(target))
                return new ParsedClientMessage(
                    ClientMessageType.PrivateChat,
                    new PrivateChatArgs(target, content));
        }

        // 格式无效 → 降级为普通消息
        return new ParsedClientMessage(ClientMessageType.NormalChat, new NormalChatArgs(rawContent));
    }

    // ─── Format：服务端 → 客户端 ───

    private static string Cmd(byte n) => $"{Prefix}{n}";
    private static string Cmd(byte n, string arg) => $"{Prefix}{n}{arg}";

    /// <inheritdoc />
    public string ReloginSuccess() => Cmd(0);

    /// <inheritdoc />
    public string WrongPassword() => Cmd(1);

    /// <inheritdoc />
    public string NewUser() => Cmd(2);

    /// <inheritdoc />
    public string ServerShutdown() => Cmd(3);

    /// <inheritdoc />
    /// <remarks>格式: <c>#-&gt;5Name1#Name2#Name3#</c></remarks>
    public string UserList(IEnumerable<string> userNames)
        => Cmd(5, string.Concat(userNames.Select(n => n + "#")));

    /// <inheritdoc />
    public string UserJoin(string userName) => Cmd(6, userName);

    /// <inheritdoc />
    public string UserLeave(string userName) => Cmd(8, userName);

    /// <inheritdoc />
    /// <remarks>格式: <c>&lt;Server&gt;: 欢迎加入群聊#Room1#</c></remarks>
    public string ServerMessage(string content) => $"<Server>: {content}";

    /// <inheritdoc />
    /// <remarks>格式: <c>&lt;UserName&gt;: hello</c></remarks>
    public string ClientNormalMessage(string userName, string content) => $"<{userName}>: {content}";

    /// <inheritdoc />
    /// <remarks>格式: <c>Private Message From&lt;UserName&gt;: hi</c></remarks>
    public string ClientPrivateMessage(string senderName, string content) =>
        $"Private Message From<{senderName}>: {content}";
}
