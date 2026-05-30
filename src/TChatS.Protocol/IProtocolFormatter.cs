namespace TChatS.Protocol;

/// <summary>
/// 业务协议格式化接口。将业务语义（登录成功、用户加入等）转换为协议层字符串。
/// 不同实现对应不同的协议版本，方便后续切换。
/// </summary>
public interface IProtocolFormatter
{
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
