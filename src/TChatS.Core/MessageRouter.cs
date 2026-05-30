using TChatS.Core.Models;
using TChatS.Protocol;

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
    private readonly IProtocolFormatter _fmt;

    public MessageRouter(ChatRoomManager chatRooms, AuthService auth, IProtocolFormatter fmt)
    {
        _chatRooms = chatRooms ?? throw new ArgumentNullException(nameof(chatRooms));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _fmt = fmt ?? throw new ArgumentNullException(nameof(fmt));
    }

    /// <summary>
    /// 处理一条完整的协议消息。
    /// </summary>
    /// <param name="connectionId">来源连接 ID</param>
    /// <param name="rawContent">原始消息内容（已去除帧分隔符）</param>
    /// <returns>待执行的输出动作列表</returns>
    public RouteResult Route(long connectionId, string rawContent)
    {
        var chatRoom = _chatRooms.FindRoomByConnection(connectionId);

        if (chatRoom == null)
            return ProcessLogin(connectionId, rawContent);

        var userName = chatRoom.FindUserName(connectionId)!;
        return ProcessMessage(connectionId, userName, chatRoom, rawContent);
    }

    // ─── 登录 ───

    private RouteResult ProcessLogin(long connectionId, string rawContent)
    {
        var actions = new List<OutgoingAction>();

        // 解析登录信息: 2Ui1n+-#UserName@Password>ChatID
        var loginInfo = AuthService.ParseLogin(rawContent);
        if (loginInfo == null)
        {
            actions.Add(new OutgoingAction.Send(connectionId,
                _fmt.ServerMessage($"Warning: 非法登录，可能是网络问题，非法信息为\"{rawContent}\"")));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        var nameError = AuthService.ValidateUserName(loginInfo.UserName);
        if (nameError != null)
        {
            actions.Add(new OutgoingAction.Send(connectionId, _fmt.ServerMessage(nameError)));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        var result = _auth.Authenticate(loginInfo.UserName, loginInfo.Password);

        if (result == LoginResult.WrongPassword)
        {
            // 密码错误 → #->1，断开
            actions.Add(new OutgoingAction.Send(connectionId, _fmt.WrongPassword()));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        _chatRooms.JoinRoom(loginInfo.ChatId, loginInfo.UserName, connectionId);

        if (result == LoginResult.NewUserRegistered)
        {
            // 新用户 → #->2 + 欢迎消息
            actions.Add(new OutgoingAction.Send(connectionId, _fmt.NewUser()));
            actions.Add(new OutgoingAction.Send(connectionId,
                _fmt.ServerMessage($"欢迎加入群聊#{loginInfo.ChatId}#")));
        }
        else
        {
            // 老用户回归 → #->0 + 欢迎回来
            actions.Add(new OutgoingAction.Send(connectionId, _fmt.ReloginSuccess()));
            actions.Add(new OutgoingAction.Send(connectionId,
                _fmt.ServerMessage($"<{loginInfo.UserName}>欢迎回来,您已进入#{loginInfo.ChatId}#群聊")));
        }

        // 广播 #->6 给聊天室内其他用户
        actions.Add(new OutgoingAction.BroadcastToChat(
            loginInfo.ChatId, _fmt.UserJoin(loginInfo.UserName),
            ExcludeConnectionId: connectionId));

        // 下发 #->5 用户列表给刚登录的用户
        var chatRoom = _chatRooms.FindRoom(loginInfo.ChatId)!;
        var otherUsers = chatRoom.GetUserNames()
            .Where(u => !string.Equals(u, loginInfo.UserName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (otherUsers.Count > 0)
            actions.Add(new OutgoingAction.Send(connectionId, _fmt.UserList(otherUsers)));

        return new RouteResult(actions);
    }

    // ─── 消息 ───

    private RouteResult ProcessMessage(long connectionId, string userName,
        ChatRoom chatRoom, string rawContent)
    {
        if (rawContent.StartsWith(ProtocolFormat.PrivatePrefix, StringComparison.Ordinal))
            return ProcessPrivateMessage(connectionId, userName, chatRoom, rawContent);

        var actions = new List<OutgoingAction>
        {
            new OutgoingAction.BroadcastToChat(
                chatRoom.ChatId, _fmt.ClientNormalMessage(userName, rawContent),
                ExcludeConnectionId: connectionId)
        };
        return new RouteResult(actions);
    }

    // ─── 私聊 ───

    private RouteResult ProcessPrivateMessage(
        long connectionId, string senderName, ChatRoom chatRoom, string rawContent)
    {
        // #->7TargetUserName#->MessageContent
        var payload = rawContent[ProtocolFormat.PrivatePrefix.Length..];
        var delimiterIndex = payload.IndexOf(ProtocolFormat.PrivateDelimiter, StringComparison.Ordinal);

        if (delimiterIndex <= 0)
        {
            return new RouteResult(new List<OutgoingAction>
            {
                new OutgoingAction.BroadcastToChat(
                    chatRoom.ChatId, _fmt.ClientNormalMessage(senderName, rawContent),
                    ExcludeConnectionId: connectionId)
            });
        }

        var targetUserName = payload[..delimiterIndex];
        var privateMessage = payload[(delimiterIndex + ProtocolFormat.PrivateDelimiter.Length)..];

        var targetConnId = chatRoom.FindConnectionId(targetUserName);
        var actions = new List<OutgoingAction>();

        if (targetConnId.HasValue)
        {
            // 目标存在 → 转发私聊消息
            actions.Add(new OutgoingAction.Send(targetConnId.Value,
                _fmt.ClientPrivateMessage(senderName, privateMessage)));
        }
        else
        {
            // 目标不存在 → IProtocolFormatter 会生成 #->8{target}
            actions.Add(new OutgoingAction.Send(connectionId,
                _fmt.UserLeave(targetUserName)));
        }

        return new RouteResult(actions);
    }

    // ─── 离开 ───

    public RouteResult HandleDisconnect(long connectionId)
    {
        var (userName, chatId) = _chatRooms.LeaveRoom(connectionId);

        if (userName == null || chatId == null)
            return RouteResult.Empty;

        var actions = new List<OutgoingAction>
        {
            new OutgoingAction.BroadcastToChat(
                chatId, _fmt.UserLeave(userName), ExcludeConnectionId: connectionId)
        };
        return new RouteResult(actions);
    }
}
