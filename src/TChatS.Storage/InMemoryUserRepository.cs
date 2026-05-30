using System.Collections.Concurrent;

namespace TChatS.Storage;

/// <summary>
/// 内存用户存储实现。
/// 数据仅存在于进程生命周期内，进程重启后丢失。
/// </summary>
public class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, string> _users = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public UserInfo? FindByUserName(string userName)
    {
        if (_users.TryGetValue(userName, out var passwordHash))
            return new UserInfo(userName, passwordHash);
        return null;
    }

    /// <inheritdoc />
    public void Add(UserInfo user)
    {
        if (!_users.TryAdd(user.UserName, user.PasswordHash))
            throw new InvalidOperationException($"用户 '{user.UserName}' 已存在。");
    }

    /// <inheritdoc />
    public bool ValidatePassword(string userName, string passwordHash)
    {
        if (_users.TryGetValue(userName, out var stored))
            return string.Equals(stored, passwordHash, StringComparison.Ordinal);
        return false;
    }

    /// <inheritdoc />
    public bool Exists(string userName)
    {
        return _users.ContainsKey(userName);
    }
}
