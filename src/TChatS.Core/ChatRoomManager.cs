using System.Collections.Concurrent;

namespace TChatS.Core;

/// <summary>
/// 聊天室管理器，维护所有 ChatId → ChatRoom 的映射。
/// 线程安全。
/// </summary>
public class ChatRoomManager
{
    private readonly ConcurrentDictionary<string, ChatRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取或创建聊天室。
    /// </summary>
    public ChatRoom GetOrCreate(string chatId)
    {
        return _rooms.GetOrAdd(chatId, id => new ChatRoom(id));
    }

    /// <summary>
    /// 按 ChatId 查找聊天室。
    /// </summary>
    public ChatRoom? FindRoom(string chatId)
    {
        return _rooms.TryGetValue(chatId, out var room) ? room : null;
    }

    /// <summary>
    /// 按连接 ID 查找其所在的聊天室。
    /// </summary>
    public ChatRoom? FindRoomByConnection(long connectionId)
    {
        return _rooms.Values.FirstOrDefault(r => r.Contains(connectionId));
    }

    /// <summary>
    /// 用户加入聊天室。若聊天室不存在则自动创建。
    /// </summary>
    /// <returns>false 表示用户在该聊天室中已存在</returns>
    public bool JoinRoom(string chatId, string userName, long connectionId)
    {
        var room = GetOrCreate(chatId);
        return room.Join(userName, connectionId);
    }

    /// <summary>
    /// 用户离开其所在的聊天室 (按连接 ID 离开)。
    /// </summary>
    /// <returns>(离开的用户名, 所在 ChatId)，未找到返回 (null, null)</returns>
    public (string? userName, string? chatId) LeaveRoom(long connectionId)
    {
        var room = FindRoomByConnection(connectionId);
        if (room == null) return (null, null);

        var userName = room.Leave(connectionId);

        // 清理空聊天室
        if (room.IsEmpty)
            _rooms.TryRemove(room.ChatId, out _);

        return (userName, room.ChatId);
    }

    /// <summary>
    /// 当前聊天室数量。
    /// </summary>
    public int RoomCount => _rooms.Count;

    /// <summary>
    /// 获取所有聊天室 ID。
    /// </summary>
    public IReadOnlyList<string> GetRoomIds()
    {
        return _rooms.Keys.ToList();
    }

    /// <summary>
    /// 广播消息到所有聊天室的所有用户。
    /// 用于 #-&gt;3 / #-&gt;4 等全局通知。
    /// </summary>
    public IReadOnlyList<(long connectionId, string chatId)> GetAllConnections()
    {
        return _rooms.Values
            .SelectMany(r => r.GetUsers().Select(u => (u.ConnectionId, r.ChatId)))
            .ToList();
    }
}
