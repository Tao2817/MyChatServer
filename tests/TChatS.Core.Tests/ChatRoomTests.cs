using Xunit;

namespace TChatS.Core.Tests;

public class ChatRoomTests
{
    [Fact]
    public void Join_NewUser_Succeeds()
    {
        var room = new ChatRoom("Room1");
        var result = room.Join("Alice", connectionId: 1);
        Assert.True(result);
        Assert.Equal(1, room.UserCount);
    }

    [Fact]
    public void Join_DuplicateUserName_Fails()
    {
        var room = new ChatRoom("Room1");
        room.Join("Alice", connectionId: 1);
        var result = room.Join("Alice", connectionId: 2); // 同名用户
        Assert.False(result);
        Assert.Equal(1, room.UserCount);
    }

    [Fact]
    public void Join_SameUserNameDifferentCase_Fails()
    {
        var room = new ChatRoom("Room1");
        room.Join("Alice", connectionId: 1);
        var result = room.Join("alice", connectionId: 2); // 大小写不敏感
        Assert.False(result);
    }

    [Fact]
    public void Leave_ExistingUser_ReturnsUserName()
    {
        var room = new ChatRoom("Room1");
        room.Join("Alice", connectionId: 1);

        var userName = room.Leave(connectionId: 1);
        Assert.Equal("Alice", userName);
        Assert.Equal(0, room.UserCount);
        Assert.True(room.IsEmpty);
    }

    [Fact]
    public void Leave_NonExistent_ReturnsNull()
    {
        var room = new ChatRoom("Room1");
        var userName = room.Leave(connectionId: 999);
        Assert.Null(userName);
    }

    [Fact]
    public void FindConnectionId_ExistingUser_ReturnsId()
    {
        var room = new ChatRoom("Room1");
        room.Join("Bob", connectionId: 42);

        var id = room.FindConnectionId("Bob");
        Assert.Equal(42, id);
    }

    [Fact]
    public void FindConnectionId_NotExisting_ReturnsNull()
    {
        var room = new ChatRoom("Room1");
        Assert.Null(room.FindConnectionId("Nobody"));
    }

    [Fact]
    public void FindUserName_Existing_ReturnsName()
    {
        var room = new ChatRoom("Room1");
        room.Join("Charlie", connectionId: 7);

        var name = room.FindUserName(connectionId: 7);
        Assert.Equal("Charlie", name);
    }

    [Fact]
    public void Contains_ExistingConnection_ReturnsTrue()
    {
        var room = new ChatRoom("Room1");
        room.Join("Dave", connectionId: 3);
        Assert.True(room.Contains(3));
        Assert.False(room.Contains(99));
    }

    [Fact]
    public void GetOtherConnectionIds_ExcludesSpecified()
    {
        var room = new ChatRoom("Room1");
        room.Join("A", 1);
        room.Join("B", 2);
        room.Join("C", 3);

        var others = room.GetOtherConnectionIds(excludeConnectionId: 2);
        Assert.Equal(2, others.Count);
        Assert.Contains(1L, others);
        Assert.Contains(3L, others);
        Assert.DoesNotContain(2L, others);
    }

    [Fact]
    public void GetUserNames_ReturnsAllNames()
    {
        var room = new ChatRoom("Room1");
        room.Join("Alice", 1);
        room.Join("Bob", 2);

        var names = room.GetUserNames();
        Assert.Equal(2, names.Count);
        Assert.Contains("Alice", names);
        Assert.Contains("Bob", names);
    }
}

public class ChatRoomManagerTests
{
    [Fact]
    public void GetOrCreate_CreatesNewRoom()
    {
        var mgr = new ChatRoomManager();
        var room = mgr.GetOrCreate("NewRoom");

        Assert.NotNull(room);
        Assert.Equal("NewRoom", room.ChatId);
        Assert.Equal(1, mgr.RoomCount);
    }

    [Fact]
    public void GetOrCreate_ReturnsExistingRoom()
    {
        var mgr = new ChatRoomManager();
        var room1 = mgr.GetOrCreate("RoomX");
        var room2 = mgr.GetOrCreate("RoomX");

        Assert.Same(room1, room2);
        Assert.Equal(1, mgr.RoomCount);
    }

    [Fact]
    public void JoinRoom_AddsUserToRoom()
    {
        var mgr = new ChatRoomManager();
        var result = mgr.JoinRoom("Lobby", "Alice", connectionId: 1);

        Assert.True(result);
        var room = mgr.FindRoom("Lobby");
        Assert.NotNull(room);
        Assert.Equal("Alice", room!.FindUserName(1));
    }

    [Fact]
    public void FindRoomByConnection_ReturnsCorrectRoom()
    {
        var mgr = new ChatRoomManager();
        mgr.JoinRoom("RoomA", "User1", 1);
        mgr.JoinRoom("RoomB", "User2", 2);

        var roomA = mgr.FindRoomByConnection(1);
        var roomB = mgr.FindRoomByConnection(2);

        Assert.NotNull(roomA);
        Assert.NotNull(roomB);
        Assert.Equal("RoomA", roomA!.ChatId);
        Assert.Equal("RoomB", roomB!.ChatId);
    }

    [Fact]
    public void LeaveRoom_ReturnsUserNameAndChatId()
    {
        var mgr = new ChatRoomManager();
        mgr.JoinRoom("Lobby", "Alice", 1);

        var (name, chatId) = mgr.LeaveRoom(1);
        Assert.Equal("Alice", name);
        Assert.Equal("Lobby", chatId);
    }

    [Fact]
    public void LeaveRoom_LastUser_RemovesEmptyRoom()
    {
        var mgr = new ChatRoomManager();
        mgr.JoinRoom("Lobby", "Alice", 1);

        mgr.LeaveRoom(1);
        Assert.Null(mgr.FindRoom("Lobby"));
        Assert.Equal(0, mgr.RoomCount);
    }

    [Fact]
    public void LeaveRoom_UnknownConnection_ReturnsNulls()
    {
        var mgr = new ChatRoomManager();
        var (name, chatId) = mgr.LeaveRoom(999);
        Assert.Null(name);
        Assert.Null(chatId);
    }

    [Fact]
    public void GetAllConnections_ReturnsAcrossRooms()
    {
        var mgr = new ChatRoomManager();
        mgr.JoinRoom("RoomA", "A1", 1);
        mgr.JoinRoom("RoomA", "A2", 2);
        mgr.JoinRoom("RoomB", "B1", 3);

        var all = mgr.GetAllConnections();
        Assert.Equal(3, all.Count);
    }
}
