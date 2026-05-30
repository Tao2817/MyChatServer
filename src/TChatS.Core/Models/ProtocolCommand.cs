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

    /// <summary>
    /// 登录魔术字，共 9 字符。旧版客户端格式: <c>#2Ui1n+-#User@Pass&gt;ChatID</c>
    /// 出处: TChatClientDlg.cpp:258 <c>INFO.Format(L"#2Ui1n+-#%s@%s&gt;%s", ...)</c>
    /// 服务端通过 <c>message.Delete(0, 9)</c> 剥除此前缀。
    /// </summary>
    public const string LoginMagic = "#2Ui1n+-#";

    /// <summary>私聊指令前缀</summary>
    public const string PrivatePrefix = "#->7";

    /// <summary>私聊分隔符 (消息内容前的第二个 #->)</summary>
    public const string PrivateDelimiter = "#->";

    /// <summary>登录信息分隔符: UserName @ Password > ChatID</summary>
    public const char LoginUserSeparator = '@';
    public const char LoginChatSeparator = '>';

    /// <summary>
    /// 构建协议指令字符串。
    /// 无参数: <c>"#-&gt;0"</c>；带参数: <c>"#-&gt;6Tao"</c>；多参数: <c>"#-&gt;5Alice#Bob#"</c>
    /// </summary>
    public static string Command(ProtocolCommand cmd, params string[] args)
        => $"{Prefix}{(byte)cmd}{string.Concat(args)}";

    /// <summary>
    /// 序列化 #-&gt;5 的参数部分。
    /// 旧版格式: <c>Name1#Name2#Name3#</c> — 每用户名以 # 结尾 (含最后一个)。
    /// 调用方需自行拼接命令头: <c>Command(UserList, SerializeUserList(names))</c>
    /// 出处: ServerSocket.cpp:152-158
    /// </summary>
    public static string SerializeUserList(IEnumerable<string> userNames)
        => string.Concat(userNames.Select(n => n + "#"));
}
