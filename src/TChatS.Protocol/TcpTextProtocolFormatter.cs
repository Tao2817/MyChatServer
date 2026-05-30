namespace TChatS.Protocol;

/// <summary>
/// 旧版 TCP 文本协议的 <see cref="IProtocolFormatter"/> 实现。
/// 所有指令格式: <c>#-&gt;N[args]</c>，与 TChatServer_old 完全兼容。
/// </summary>
public sealed class TcpTextProtocolFormatter : IProtocolFormatter
{
    private const string Prefix = "#->";

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
