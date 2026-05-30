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
    private readonly IServiceProtocol _protocol;

    public MessageRouter(ChatRoomManager chatRooms, AuthService auth, IServiceProtocol protocol)
    {
        _chatRooms = chatRooms ?? throw new ArgumentNullException(nameof(chatRooms));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
    }

    /// <summary>
    /// 处理一条完整的协议消息。
    /// </summary>
    /// <param name="connectionId">来源连接 ID</param>
    /// <param name="rawContent">原始消息内容（已去除帧分隔符）</param>
    /// <returns>待执行的输出动作列表</returns>
    public RouteResult Route(long connectionId, string rawContent)
    {
        var parsed = _protocol.Parse(rawContent);
        var chatRoom = _chatRooms.FindRoomByConnection(connectionId);

        // 未登录用户：只接受登录消息
        if (chatRoom == null)
        {
            return parsed is { Type: ClientMessageType.Login, Args: LoginArgs login }
                ? HandleLogin(connectionId, login)
                : InvalidLogin(connectionId, rawContent);
        }

        // 已登录用户：处理消息
        var userName = chatRoom.FindUserName(connectionId)!;
        return parsed switch
        {
            { Type: ClientMessageType.PrivateChat, Args: PrivateChatArgs pc }
                => HandlePrivateChat(connectionId, userName, chatRoom, pc),

            { Type: ClientMessageType.NormalChat, Args: NormalChatArgs nc }
                => HandleNormalChat(connectionId, userName, chatRoom, nc.Content),

            _ => throw new ProtocolException(
                $"已登录连接 {connectionId} 收到意外的消息类型 {parsed.Type}。")
        };
    }

    // ─── 登录 ───

    private RouteResult HandleLogin(long connectionId, LoginArgs login)
    {
        var actions = new List<OutgoingAction>();

        var nameError = AuthService.ValidateUserName(login.UserName);
        if (nameError != null)
        {
            actions.Add(new OutgoingAction.Send(connectionId, _protocol.ServerMessage(nameError)));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        var result = _auth.Authenticate(login.UserName, login.Password);

        if (result == LoginResult.WrongPassword)
        {
            actions.Add(new OutgoingAction.Send(connectionId, _protocol.WrongPassword()));
            actions.Add(new OutgoingAction.Disconnect(connectionId));
            return new RouteResult(actions);
        }

        _chatRooms.JoinRoom(login.ChatId, login.UserName, connectionId);

        if (result == LoginResult.NewUserRegistered)
        {
            actions.Add(new OutgoingAction.Send(connectionId, _protocol.NewUser()));
            actions.Add(new OutgoingAction.Send(connectionId,
                _protocol.ServerMessage($"欢迎加入群聊#{login.ChatId}#")));
        }
        else
        {
            actions.Add(new OutgoingAction.Send(connectionId, _protocol.ReloginSuccess()));
            actions.Add(new OutgoingAction.Send(connectionId,
                _protocol.ServerMessage($"<{login.UserName}>欢迎回来,您已进入#{login.ChatId}#群聊")));
        }

        // 广播 #->6 给聊天室内其他用户
        actions.Add(new OutgoingAction.BroadcastToChat(
            login.ChatId, _protocol.UserJoin(login.UserName),
            ExcludeConnectionId: connectionId));

        // 下发 #->5 用户列表给刚登录的用户
        var chatRoom = _chatRooms.FindRoom(login.ChatId)!;
        var otherUsers = chatRoom.GetUserNames()
            .Where(u => !string.Equals(u, login.UserName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (otherUsers.Count > 0)
            actions.Add(new OutgoingAction.Send(connectionId, _protocol.UserList(otherUsers)));

        return new RouteResult(actions);
    }

    private RouteResult InvalidLogin(long connectionId, string rawContent)
    {
        var actions = new List<OutgoingAction>
        {
            new OutgoingAction.Send(connectionId,
                _protocol.ServerMessage($"Warning: 非法登录，可能是网络问题，非法信息为\"{rawContent}\"")),
            new OutgoingAction.Disconnect(connectionId)
        };
        return new RouteResult(actions);
    }

    // ─── 普通群聊 ───

    private static RouteResult HandleNormalChat(
        long connectionId, string userName, ChatRoom chatRoom, string content)
    {
        var actions = new List<OutgoingAction>
        {
            new OutgoingAction.BroadcastToChat(
                chatRoom.ChatId, $"<{userName}>: {content}",
                ExcludeConnectionId: connectionId)
        };
        return new RouteResult(actions);
    }

    // ─── 私聊 ───

    private RouteResult HandlePrivateChat(
        long connectionId, string senderName, ChatRoom chatRoom, PrivateChatArgs pc)
    {
        var targetConnId = chatRoom.FindConnectionId(pc.TargetUserName);
        var actions = new List<OutgoingAction>();

        if (targetConnId.HasValue)
        {
            // 目标存在 → 转发私聊消息
            actions.Add(new OutgoingAction.Send(targetConnId.Value,
                _protocol.ClientPrivateMessage(senderName, pc.Content)));
        }
        else
        {
            // 目标不存在 → IServiceProtocol 会生成 #->8{target}
            actions.Add(new OutgoingAction.Send(connectionId,
                _protocol.UserLeave(pc.TargetUserName)));
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
                chatId, _protocol.UserLeave(userName), ExcludeConnectionId: connectionId)
        };
        return new RouteResult(actions);
    }
}
