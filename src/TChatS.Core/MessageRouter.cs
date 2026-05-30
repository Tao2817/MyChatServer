using TChatS.Core.Models;

namespace TChatS.Core;

/// <summary>
/// 消息路由器。完全复刻旧版 <c>ServerSocket::OnReceive</c> 的处理逻辑。
/// 接收原始消息文本，返回一组待执行的 <see cref="OutgoingAction"/>。
/// Core 层不直接操作网络 I/O，所有输出通过 Action 列表返回给 Service 层执行。
/// </summary>
public class MessageRouter
{
    private readonly ChatRoomManager _chatRooms;
    private readonly AuthService _auth;

    public MessageRouter(ChatRoomManager chatRooms, AuthService auth)
    {
        _chatRooms = chatRooms ?? throw new ArgumentNullException(nameof(chatRooms));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }

    /// <summary>
    /// 处理一条完整的协议消息。
    /// </summary>
    /// <param name="connectionId">来源连接 ID</param>
    /// <param name="rawContent">原始消息内容（已去除帧分隔符）</param>
    /// <returns>待执行的输出动作列表</returns>
    public RouteResult Route(long connectionId, string rawContent)
    {
        // 检查该连接是否已登录（即是否在任何聊天室中）
        var chatRoom = _chatRooms.FindRoomByConnection(connectionId);

        if (chatRoom == null)
        {
            // 未登录 → 必须为登录消息
            return ProcessLogin(connectionId, rawContent);
        }

        // 已登录 → 路由聊天消息
        var userName = chatRoom.FindUserName(connectionId)!;
        return ProcessMessage(connectionId, userName, chatRoom, rawContent);
    }

    /// <summary>
    /// 处理登录消息，与旧版 <c>GetInfo</c> + <c>SetCurrentChat</c> + <c>CheckIdentity</c> 逻辑一致。
    /// </summary>
    private RouteResult ProcessLogin(long connectionId, string rawContent)
    {
        var actions = new List<OutgoingAction>();

        // 解析登录信息: 2Ui1n+-#UserName@Password>ChatID
        var loginInfo = AuthService.ParseLogin(rawContent);
        if (loginInfo == null)
        {
            // 非法的首条消息
            actions.Add(new OutgoingAction.Send(connectionId,
                $"<Warning>: 非法登录，可能是网络问题，非法信息为\"{rawContent}\""));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        // 用户名校验
        var nameError = AuthService.ValidateUserName(loginInfo.UserName);
        if (nameError != null)
        {
            actions.Add(new OutgoingAction.Send(connectionId, $"<Server>: {nameError}"));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        // 三态认证
        var result = _auth.Authenticate(loginInfo.UserName, loginInfo.Password);

        if (result == LoginResult.WrongPassword)
        {
            // 密码错误 → #->1，断开
            actions.Add(new OutgoingAction.Send(connectionId,
                ProtocolFormat.Command(ProtocolCommand.WrongPassword)));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        // 获取或创建聊天室，并加入用户
        _chatRooms.JoinRoom(loginInfo.ChatId, loginInfo.UserName, connectionId);

        // 构建响应（与旧版 CheckIdentity 一致的发送顺序）
        if (result == LoginResult.NewUserRegistered)
        {
            // 新用户 → #->2 + 欢迎消息
            actions.Add(new OutgoingAction.Send(connectionId,
                ProtocolFormat.Command(ProtocolCommand.NewUser)));
            actions.Add(new OutgoingAction.Send(connectionId,
                $"<Server>: 欢迎加入群聊#{loginInfo.ChatId}#"));
        }
        else // ReloginSuccess
        {
            // 老用户回归 → #->0 + 欢迎回来
            actions.Add(new OutgoingAction.Send(connectionId,
                ProtocolFormat.Command(ProtocolCommand.ReloginSuccess)));
            actions.Add(new OutgoingAction.Send(connectionId,
                $"<Server>: <{loginInfo.UserName}>欢迎回来,您已进入#{loginInfo.ChatId}#群聊"));
        }

        // 广播 #->6 给聊天室内其他用户
        actions.Add(new OutgoingAction.BroadcastToChat(
            loginInfo.ChatId,
            ProtocolFormat.CommandWithArg(ProtocolCommand.UserJoin, loginInfo.UserName),
            ExcludeConnectionId: connectionId));

        // 下发 #->5 用户列表给刚登录的用户
        var chatRoom = _chatRooms.FindRoom(loginInfo.ChatId)!;
        var otherUsers = chatRoom.GetUserNames()
            .Where(u => !string.Equals(u, loginInfo.UserName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (otherUsers.Count > 0)
        {
            var userList = ProtocolFormat.Command(ProtocolCommand.UserList) + string.Join("#", otherUsers);
            actions.Add(new OutgoingAction.Send(connectionId, userList));
        }

        return new RouteResult(actions);
    }

    /// <summary>
    /// 处理已登录用户的聊天消息，与旧版 <c>OnReceive</c> 后半段逻辑一致。
    /// </summary>
    private static RouteResult ProcessMessage(long connectionId, string userName, ChatRoom chatRoom, string rawContent)
    {
        var actions = new List<OutgoingAction>();

        // 检查是否为私聊消息 #->7TargetUser#->MessageContent
        if (rawContent.StartsWith(ProtocolFormat.PrivatePrefix, StringComparison.Ordinal))
        {
            return ProcessPrivateMessage(connectionId, userName, chatRoom, rawContent);
        }

        // 普通群聊消息: 广播给聊天室内所有其他用户
        var content = $"<{userName}>: {rawContent}";
        actions.Add(new OutgoingAction.BroadcastToChat(
            chatRoom.ChatId, content, ExcludeConnectionId: connectionId));

        return new RouteResult(actions);
    }

    /// <summary>
    /// 处理私聊消息 #-&gt;7，与旧版逻辑一致。
    /// 格式: <c>#-&gt;7TargetUserName#-&gt;MessageContent</c>
    /// </summary>
    private static RouteResult ProcessPrivateMessage(
        long connectionId, string senderName, ChatRoom chatRoom, string rawContent)
    {
        var actions = new List<OutgoingAction>();

        // 解析: 去掉 #->7 前缀，找到第二个 #-> 的位置
        var payload = rawContent[ProtocolFormat.PrivatePrefix.Length..];
        var delimiterIndex = payload.IndexOf(ProtocolFormat.PrivateDelimiter, StringComparison.Ordinal);

        if (delimiterIndex <= 0)
        {
            // 私聊格式错误 — 按普通消息处理
            actions.Add(new OutgoingAction.BroadcastToChat(
                chatRoom.ChatId, $"<{senderName}>: {rawContent}", ExcludeConnectionId: connectionId));
            return new RouteResult(actions);
        }

        var targetUserName = payload[..delimiterIndex];
        var privateMessage = payload[(delimiterIndex + ProtocolFormat.PrivateDelimiter.Length)..];

        // 在同一个聊天室中查找目标用户
        var targetConnId = chatRoom.FindConnectionId(targetUserName);
        if (targetConnId.HasValue)
        {
            // 目标存在 → 转发私聊消息
            actions.Add(new OutgoingAction.Send(targetConnId.Value,
                $"Private Message From<{senderName}>: {privateMessage}"));
        }
        else
        {
            // 目标不存在 → 通知发送方 #->8
            actions.Add(new OutgoingAction.Send(connectionId,
                ProtocolFormat.CommandWithArg(ProtocolCommand.UserLeave, targetUserName)));
        }

        return new RouteResult(actions);
    }

    /// <summary>
    /// 处理用户主动离开（连接关闭），与旧版 <c>OnClose</c> 逻辑一致。
    /// 从聊天室移除用户，广播 #-&gt;8。
    /// </summary>
    public RouteResult HandleDisconnect(long connectionId)
    {
        var (userName, chatId) = _chatRooms.LeaveRoom(connectionId);

        if (userName == null || chatId == null)
        {
            // 未登录就离开（旧版中记录警告）
            return RouteResult.Empty;
        }

        var actions = new List<OutgoingAction>
        {
            new OutgoingAction.BroadcastToChat(
                chatId,
                ProtocolFormat.CommandWithArg(ProtocolCommand.UserLeave, userName),
                ExcludeConnectionId: connectionId)
        };

        return new RouteResult(actions);
    }
}
