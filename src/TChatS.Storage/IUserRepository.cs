namespace TChatS.Storage;

/// <summary>
/// 用户持久化存储接口。
/// 默认使用内存实现 (<see cref="InMemoryUserRepository"/>)，
/// 可扩展为 JSON 文件、SQLite 等持久化方案。
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// 按用户名查找用户。
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <returns>找到的 <see cref="UserInfo"/>，未找到返回 null</returns>
    UserInfo? FindByUserName(string userName);

    /// <summary>
    /// 添加新用户。
    /// </summary>
    /// <param name="user">用户信息</param>
    /// <exception cref="InvalidOperationException">用户名已存在</exception>
    void Add(UserInfo user);

    /// <summary>
    /// 验证用户名和密码哈希是否匹配。
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <param name="passwordHash">密码哈希</param>
    /// <returns>true 表示匹配</returns>
    bool ValidatePassword(string userName, string passwordHash);

    /// <summary>
    /// 检查用户名是否已存在。
    /// </summary>
    bool Exists(string userName);
}

/// <summary>
/// 用户信息记录。
/// </summary>
/// <param name="UserName">用户名</param>
/// <param name="PasswordHash">SHA256 密码哈希（非明文）</param>
public record UserInfo(string UserName, string PasswordHash);
