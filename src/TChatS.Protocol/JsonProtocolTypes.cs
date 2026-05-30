using System.Text.Json.Serialization;

namespace TChatS.Protocol;

// ═══════════════════════════════════════════════════
// 客户端 → 服务端（Parse 用）
// ═══════════════════════════════════════════════════

/// <summary>登录消息 JSON args。<c>{"userName":"Tao","password":"1234","chatId":"Room1"}</c></summary>
public sealed record LoginJsonArgs(
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("chatId")]   string ChatId
)
{
    public LoginArgs ToDomain() => new(UserName, Password, ChatId);
}

/// <summary>私聊消息 JSON args。<c>{"target":"Bob","content":"Hi"}</c></summary>
public sealed record PrivateJsonArgs(
    [property: JsonPropertyName("target")]  string Target,
    [property: JsonPropertyName("content")] string Content
)
{
    public PrivateChatArgs ToDomain() => new(Target, Content);
}

/// <summary>群聊消息 JSON args。<c>{"content":"Hello!"}</c></summary>
public sealed record NormalJsonArgs(
    [property: JsonPropertyName("content")] string Content
)
{
    public NormalChatArgs ToDomain() => new(Content);
}

// ═══════════════════════════════════════════════════
// 服务端 → 客户端（Format 用）
// ═══════════════════════════════════════════════════

/// <summary>空参数。<c>{}</c> — relogin / wrongPassword / newUser / serverShutdown</summary>
public sealed record EmptyJsonArgs;

/// <summary>用户列表。<c>{"users":["Alice","Bob"]}</c></summary>
public sealed record UserListJsonArgs(
    [property: JsonPropertyName("users")] string[] Users
);

/// <summary>单用户名。<c>{"userName":"Bob"}</c> — userJoin / userLeave</summary>
public sealed record SingleUserJsonArgs(
    [property: JsonPropertyName("userName")] string UserName
);

/// <summary>纯文本内容。<c>{"content":"欢迎"}</c> — serverMessage</summary>
public sealed record ContentJsonArgs(
    [property: JsonPropertyName("content")] string Content
);

/// <summary>用户名 + 内容。<c>{"userName":"Alice","content":"Hi"}</c> — chat / dispatchPrivate</summary>
public sealed record UserMessageJsonArgs(
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("content")]  string Content
);
