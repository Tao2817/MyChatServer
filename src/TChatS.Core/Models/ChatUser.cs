namespace TChatS.Core.Models;

/// <summary>
/// 聊天室中的用户，绑定用户名与连接 ID。
/// </summary>
public class ChatUser
{
    public string UserName { get; }
    public long ConnectionId { get; }
    public DateTime JoinedAt { get; }

    public ChatUser(string userName, long connectionId)
    {
        UserName = userName;
        ConnectionId = connectionId;
        JoinedAt = DateTime.UtcNow;
    }
}
