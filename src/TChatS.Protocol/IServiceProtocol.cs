namespace TChatS.Protocol;

// ──────────────────────────────────────────────
// 客户端 → 服务端 消息解析
// ──────────────────────────────────────────────

/// <summary>
/// 客户端发送的消息类型。
/// </summary>
public enum ClientMessageType
{
    /// <summary>登录消息，格式: <c>#2Ui1n+-#UserName@Password&gt;ChatID</c></summary>
    Login,

    /// <summary>私聊消息，格式: <c>#-&gt;7TargetName#-&gt;Content</c></summary>
    PrivateChat,

    /// <summary>普通群聊消息</summary>
    NormalChat,
}

// ──────────────────────────────────────────────
// 客户端消息参数类型
// ──────────────────────────────────────────────

/// <summary>客户端消息参数基类。</summary>
public abstract record ClientMessageArgs;

/// <summary>登录消息参数。</summary>
/// <param name="UserName">用户名</param>
/// <param name="Password">明文密码</param>
/// <param name="ChatId">目标聊天室 ID</param>
public sealed record LoginArgs(string UserName, string Password, string ChatId) : ClientMessageArgs;

/// <summary>私聊消息参数。</summary>
/// <param name="TargetUserName">目标用户名</param>
/// <param name="Content">私聊消息内容</param>
public sealed record PrivateChatArgs(string TargetUserName, string Content) : ClientMessageArgs;

/// <summary>普通群聊消息参数。</summary>
/// <param name="Content">消息内容</param>
public sealed record NormalChatArgs(string Content) : ClientMessageArgs;

/// <summary>
/// 客户端消息解析结果。
/// </summary>
/// <param name="Type">消息类型</param>
/// <param name="Args">类型化参数，按 <see cref="Type"/> 对应具体子类</param>
public readonly record struct ParsedClientMessage(
    ClientMessageType Type,
    ClientMessageArgs Args
);

// ──────────────────────────────────────────────
// 业务协议接口
// ──────────────────────────────────────────────

/// <summary>
/// 业务协议接口。负责：
/// <list type="number">
///   <item><b>Parse</b> — 将客户端发来的原始消息解析为 <see cref="ParsedClientMessage"/>（消息类型 + 参数）</item>
///   <item><b>Format</b> — 将业务语义（登录成功、用户加入等）转换为协议层字符串</item>
/// </list>
/// 不同实现对应不同的协议版本，方便后续切换。
/// </summary>
public interface IServiceProtocol
{
    // ─── Parse：客户端 → 服务端 ───

    /// <summary>
    /// 解析客户端发来的原始消息，返回消息类型和参数。
    /// </summary>
    /// <param name="rawContent">原始消息内容（已去除帧分隔符）</param>
    /// <returns>解析结果，包含消息类型和对应参数</returns>
    ParsedClientMessage Parse(string rawContent);

    // ─── Format：服务端 → 客户端 ───

    /// <summary>重新登录成功 #-&gt;0</summary>
    string ReloginSuccess();

    /// <summary>密码错误 #-&gt;1</summary>
    string WrongPassword();

    /// <summary>新用户注册 #-&gt;2</summary>
    string NewUser();

    /// <summary>服务端关闭 #-&gt;3</summary>
    string ServerShutdown();

    /// <summary>用户列表 #-&gt;5</summary>
    string UserList(IEnumerable<string> userNames);

    /// <summary>用户加入 #-&gt;6</summary>
    string UserJoin(string userName);

    /// <summary>用户离开 #-&gt;8</summary>
    string UserLeave(string userName);

    /// <summary>服务器纯文本消息，如欢迎语、提示等</summary>
    string ServerMessage(string content);

    /// <summary>客户端普通群聊消息，如 <c>&lt;Alice&gt;: hello</c></summary>
    string ClientNormalMessage(string userName, string content);

    /// <summary>客户端私聊消息，如 <c>Private Message From&lt;Alice&gt;: hi</c></summary>
    string ClientPrivateMessage(string senderName, string content);
}
