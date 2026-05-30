using TChatS.Core.Models;
using TChatS.Protocol;
using TChatS.Storage;
using Xunit;

namespace TChatS.Core.Tests;

public class MessageRouterTests
{
    private readonly InMemoryUserRepository _repo;
    private readonly AuthService _auth;
    private readonly ChatRoomManager _chatRooms;
    private readonly MessageRouter _router;

    public MessageRouterTests()
    {
        _repo = new InMemoryUserRepository();
        _auth = new AuthService(_repo);
        _chatRooms = new ChatRoomManager();
        _router = new MessageRouter(_chatRooms, _auth, new TcpTextProtocolFormatter());
    }

    // ─── 登录流程 ───

    [Fact]
    public void Login_NewUser_SendsNewUserAndWelcome()
    {
        var result = _router.Route(connectionId: 1, "#2Ui1n+-#Tao@1234>Room1");

        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Send s && s.Content == "#->2");
        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Send s && s.Content.Contains("欢迎加入群聊"));
        Assert.Contains(result.Actions,
            a => a is OutgoingAction.BroadcastToChat b && b.Content == "#->6Tao");
    }

    [Fact]
    public void Login_ExistingUserCorrectPassword_SendsReloginSuccess()
    {
        // 先注册
        _router.Route(connectionId: 1, "#2Ui1n+-#Bob@pwd>Room1");
        // 断开
        _router.HandleDisconnect(1);

        // 重新登录（新连接 ID）
        var result = _router.Route(connectionId: 2, "#2Ui1n+-#Bob@pwd>Room1");

        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Send s && s.Content == "#->0");
        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Send s && s.Content.Contains("欢迎回来"));
    }

    [Fact]
    public void Login_WrongPassword_SendsErrorAndDisconnects()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Eve@correct>Room1");

        var result = _router.Route(connectionId: 2, "#2Ui1n+-#Eve@wrong>Room1");

        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Send s && s.Content == "#->1");
        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Disconnect);
    }

    [Fact]
    public void Login_InvalidFormat_SendsWarningAndDisconnects()
    {
        var result = _router.Route(connectionId: 1, "Just random text");

        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Send s && s.Content.Contains("Warning"));
        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Disconnect);
    }

    [Fact]
    public void Login_BannedUserName_SendsErrorAndDisconnects()
    {
        var result = _router.Route(connectionId: 1, "#2Ui1n+-#server@pwd>Room1");

        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Send s && s.Content.Contains("禁止"));
        Assert.Contains(result.Actions,
            a => a is OutgoingAction.Disconnect);
    }

    [Fact]
    public void Login_SendsUserListToNewJoiner()
    {
        // 先加入两个用户到同一聊天室
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Lobby");
        _router.Route(connectionId: 2, "#2Ui1n+-#Bob@1>Lobby");

        // 第三个用户加入
        var result = _router.Route(connectionId: 3, "#2Ui1n+-#Charlie@1>Lobby");

        // 应包含 #->5 用户列表（Alice 和 Bob）
        var userListAction = result.Actions
            .OfType<OutgoingAction.Send>()
            .FirstOrDefault(a => a.Content.StartsWith("#->5"));
        Assert.NotNull(userListAction);
        Assert.Contains("Alice", userListAction!.Content);
        Assert.Contains("Bob", userListAction.Content);
    }

    [Fact]
    public void Login_BroadcastsJoinToOthers()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");

        var result = _router.Route(connectionId: 2, "#2Ui1n+-#Bob@1>Room1");

        // #->6 广播应排除 Bob 自己，发给 Alice (connId=1)
        var broadcast = result.Actions
            .OfType<OutgoingAction.BroadcastToChat>()
            .FirstOrDefault(b => b.Content.StartsWith("#->6"));
        Assert.NotNull(broadcast);
        Assert.Contains("#->6Bob", broadcast!.Content);
        Assert.Equal(2, broadcast.ExcludeConnectionId); // 排除 Bob
    }

    // ─── 群聊消息 ───

    [Fact]
    public void ChatMessage_BroadcastsToOthers()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");
        _router.Route(connectionId: 2, "#2Ui1n+-#Bob@1>Room1");

        var result = _router.Route(connectionId: 1, "Hello everyone!");

        var broadcast = result.Actions
            .OfType<OutgoingAction.BroadcastToChat>()
            .FirstOrDefault();
        Assert.NotNull(broadcast);
        Assert.Equal("<Alice>: Hello everyone!", broadcast!.Content);
        Assert.Equal(1, broadcast.ExcludeConnectionId); // 排除发送者自己
    }

    [Fact]
    public void ChatMessage_NotLoggedIn_TriggersLogin()
    {
        // 对未登录连接的普通消息 → 尝试解析为登录（失败 → 断开）
        var result = _router.Route(connectionId: 1, "Not a login message");

        Assert.Contains(result.Actions, a => a is OutgoingAction.Disconnect);
    }

    // ─── 私聊消息 #->7 ───

    [Fact]
    public void PrivateMessage_TargetExists_RoutesDirectly()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");
        _router.Route(connectionId: 2, "#2Ui1n+-#Bob@1>Room1");

        // Alice 向 Bob 发私聊
        var result = _router.Route(connectionId: 1, "#->7Bob#->Hi Bob!");

        var send = result.Actions
            .OfType<OutgoingAction.Send>()
            .FirstOrDefault(s => s.ConnectionId == 2); // 发给 Bob
        Assert.NotNull(send);
        Assert.Contains("Private Message From<Alice>", send!.Content);
        Assert.Contains("Hi Bob!", send.Content);
    }

    [Fact]
    public void PrivateMessage_TargetNotFound_SendsUserLeave()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");

        // Alice 向不存在的用户发私聊
        var result = _router.Route(connectionId: 1, "#->7Nobody#->Hello?");

        var send = result.Actions
            .OfType<OutgoingAction.Send>()
            .FirstOrDefault(s => s.ConnectionId == 1); // 发给发送者自己
        Assert.NotNull(send);
        Assert.Equal("#->8Nobody", send!.Content); // 通知目标已离开
    }

    [Fact]
    public void PrivateMessage_FormattedCorrectly()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");
        _router.Route(connectionId: 2, "#2Ui1n+-#Tao2817@1>Room1");

        // 复刻旧版示例: #->7Tao2817#->Hello_World!
        var result = _router.Route(connectionId: 1, "#->7Tao2817#->Hello_World!");

        var send = result.Actions
            .OfType<OutgoingAction.Send>()
            .FirstOrDefault(s => s.ConnectionId == 2);
        Assert.NotNull(send);
        Assert.Contains("Private Message From<Alice>", send!.Content);
        Assert.Contains("Hello_World!", send.Content);
    }

    // ─── 用户离开 ───

    [Fact]
    public void HandleDisconnect_LoggedInUser_BroadcastsLeave()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");
        _router.Route(connectionId: 2, "#2Ui1n+-#Bob@1>Room1");

        // Bob 断开连接
        var result = _router.HandleDisconnect(connectionId: 2);

        var broadcast = result.Actions
            .OfType<OutgoingAction.BroadcastToChat>()
            .FirstOrDefault();
        Assert.NotNull(broadcast);
        Assert.Equal("#->8Bob", broadcast!.Content); // 广播离开
        Assert.Equal(2, broadcast.ExcludeConnectionId);
    }

    [Fact]
    public void HandleDisconnect_NotLoggedIn_ReturnsEmpty()
    {
        var result = _router.HandleDisconnect(connectionId: 999);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void HandleDisconnect_LastUser_CleansUpRoom()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");

        _router.HandleDisconnect(connectionId: 1);

        Assert.Null(_chatRooms.FindRoom("Room1"));
        Assert.Equal(0, _chatRooms.RoomCount);
    }

    // ─── 协议指令常量 ───

    [Fact]
    public void ProtocolFormat_Command_ProducesCorrectPrefix()
    {
        Assert.Equal("#->0", ProtocolFormat.Command(ProtocolCommand.ReloginSuccess));
        Assert.Equal("#->1", ProtocolFormat.Command(ProtocolCommand.WrongPassword));
        Assert.Equal("#->2", ProtocolFormat.Command(ProtocolCommand.NewUser));
        Assert.Equal("#->3", ProtocolFormat.Command(ProtocolCommand.ServerShutdown));
        Assert.Equal("#->4", ProtocolFormat.Command(ProtocolCommand.ServerStopListen));
        Assert.Equal("#->7", ProtocolFormat.Command(ProtocolCommand.PrivateMessage));
        Assert.Equal("#->8", ProtocolFormat.Command(ProtocolCommand.UserLeave));
    }

    [Fact]
    public void ProtocolFormat_Command_ProducesCorrectFormat()
    {
        Assert.Equal("#->6Tao", ProtocolFormat.Command(ProtocolCommand.UserJoin, "Tao"));
        Assert.Equal("#->8Alice", ProtocolFormat.Command(ProtocolCommand.UserLeave, "Alice"));
    }

    // ─── 消息路由不产生无意义动作 ───

    [Fact]
    public void Login_DoesNotSendUserListWhenAlone()
    {
        // 第一个用户加入空聊天室 — 不应发送 #->5（没有其他人）
        var result = _router.Route(connectionId: 1, "#2Ui1n+-#Solo@1>EmptyRoom");

        var userListAction = result.Actions
            .OfType<OutgoingAction.Send>()
            .FirstOrDefault(a => a.Content.StartsWith("#->5"));
        Assert.Null(userListAction); // 没有其他用户，不下发 #->5
    }

    [Fact]
    public void Login_AllCommandsUseProtocolConstants()
    {
        _router.Route(connectionId: 1, "#2Ui1n+-#Alice@1>Room1");
        var result = _router.Route(connectionId: 2, "#2Ui1n+-#Bob@1>Room1");

        // 所有命令均应使用 #-> 前缀
        foreach (var action in result.Actions.OfType<OutgoingAction.Send>())
        {
            if (action.Content.StartsWith("#->"))
            {
                Assert.Matches(@"^#->\d", action.Content); // 格式正确
            }
        }
    }
}
