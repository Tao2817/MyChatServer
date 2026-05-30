namespace TChatS.Core.Models;

/// <summary>
/// 协议指令枚举，与旧版 <c>#-&gt;N</c> 协议一一对应。
/// </summary>
public enum ProtocolCommand : byte
{
    /// <summary>#-&gt;0 重新登录成功（用户名+密码正确）</summary>
    ReloginSuccess = 0,

    /// <summary>#-&gt;1 密码错误 / 登录被拒</summary>
    WrongPassword = 1,

    /// <summary>#-&gt;2 新用户注册成功</summary>
    NewUser = 2,

    /// <summary>#-&gt;3 服务器主动断开所有连接</summary>
    ServerShutdown = 3,

    /// <summary>#-&gt;4 服务器停止监听</summary>
    ServerStopListen = 4,

    /// <summary>#-&gt;5 下发当前群聊用户列表</summary>
    UserList = 5,

    /// <summary>#-&gt;6 广播用户加入群聊</summary>
    UserJoin = 6,

    /// <summary>#-&gt;7 私聊消息</summary>
    PrivateMessage = 7,

    /// <summary>#-&gt;8 广播用户离开群聊</summary>
    UserLeave = 8,
}

/// <summary>
/// 协议指令格式常量。
/// </summary>
public static class ProtocolFormat
{
    /// <summary>协议指令前缀</summary>
    public const string Prefix = "#->";

    /// <summary>登录魔术字</summary>
    public const string LoginMagic = "2Ui1n+-#";

    /// <summary>私聊指令前缀</summary>
    public const string PrivatePrefix = "#->7";

    /// <summary>私聊分隔符 (消息内容前的第二个 #->)</summary>
    public const string PrivateDelimiter = "#->";

    /// <summary>登录信息分隔符: UserName @ Password > ChatID</summary>
    public const char LoginUserSeparator = '@';
    public const char LoginChatSeparator = '>';

    /// <summary>构建协议指令字符串</summary>
    public static string Command(ProtocolCommand cmd) => $"{Prefix}{(byte)cmd}";

    /// <summary>构建带参数的用户加入/离开指令</summary>
    public static string CommandWithArg(ProtocolCommand cmd, string arg) => $"{Prefix}{(byte)cmd}{arg}";
}
