using System.Collections.Concurrent;
using TChatS.Core.Models;

namespace TChatS.Core;

/// <summary>
/// 单个聊天室，维护 ChatId 下所有在线用户及其连接。
/// 线程安全。
/// </summary>
public class ChatRoom
{
    private readonly ConcurrentDictionary<string, ChatUser> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, string> _connectionIndex = new(); // ConnectionId → UserName

    public string ChatId { get; }

    public ChatRoom(string chatId)
    {
        ChatId = chatId ?? throw new ArgumentNullException(nameof(chatId));
    }

    /// <summary>当前在线用户数</summary>
    public int UserCount => _users.Count;

    /// <summary>聊天室是否为空</summary>
    public bool IsEmpty => _users.IsEmpty;

    /// <summary>
    /// 用户加入此聊天室。
    /// </summary>
    /// <returns>false 表示用户名在此聊天室中已存在</returns>
    public bool Join(string userName, long connectionId)
    {
        var user = new ChatUser(userName, connectionId);
        if (!_users.TryAdd(userName, user))
            return false;

        _connectionIndex[connectionId] = userName;
        return true;
    }

    /// <summary>
    /// 用户离开此聊天室 (按连接 ID)。
    /// </summary>
    /// <returns>离开的用户名，若未找到则返回 null</returns>
    public string? Leave(long connectionId)
    {
        if (_connectionIndex.TryRemove(connectionId, out var userName))
        {
            _users.TryRemove(userName, out _);
            return userName;
        }
        return null;
    }

    /// <summary>
    /// 按用户名查找连接 ID。
    /// </summary>
    public long? FindConnectionId(string userName)
    {
        return _users.TryGetValue(userName, out var user) ? user.ConnectionId : null;
    }

    /// <summary>
    /// 按连接 ID 查找用户名。
    /// </summary>
    public string? FindUserName(long connectionId)
    {
        return _connectionIndex.TryGetValue(connectionId, out var userName) ? userName : null;
    }

    /// <summary>
    /// 指定连接是否在此聊天室中。
    /// </summary>
    public bool Contains(long connectionId) => _connectionIndex.ContainsKey(connectionId);

    /// <summary>
    /// 获取聊天室中除指定用户外的所有连接 ID 列表。
    /// 用于广播消息。
    /// </summary>
    public IReadOnlyList<long> GetOtherConnectionIds(long excludeConnectionId)
    {
        return _connectionIndex
            .Where(kv => kv.Key != excludeConnectionId)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// 获取聊天室中所有用户名列表。
    /// 用于 #-&gt;5 用户列表下发。
    /// </summary>
    public IReadOnlyList<string> GetUserNames()
    {
        return _users.Keys.ToList();
    }

    /// <summary>
    /// 获取聊天室中所有用户。
    /// </summary>
    public IReadOnlyList<ChatUser> GetUsers()
    {
        return _users.Values.ToList();
    }
}
