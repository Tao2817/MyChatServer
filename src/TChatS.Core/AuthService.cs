using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TChatS.Core.Models;
using TChatS.Storage;

namespace TChatS.Core;

/// <summary>
/// 登录认证结果，与旧版三态逻辑一致。
/// </summary>
public enum LoginResult
{
    /// <summary>重新登录成功（用户名+密码正确）→ #-&gt;0</summary>
    ReloginSuccess,
    /// <summary>密码错误 → #-&gt;1</summary>
    WrongPassword,
    /// <summary>新用户注册成功 → #-&gt;2</summary>
    NewUserRegistered,
}

/// <summary>
/// 登录信息，从 "2Ui1n+-#UserName@Password&gt;ChatID" 解析得到。
/// </summary>
public record LoginInfo(string UserName, string Password, string ChatId);

/// <summary>
/// 认证服务。负责登录消息解析、用户名校验、三态认证和密码哈希。
/// </summary>
public class AuthService
{
    private readonly IUserRepository _users;

    // 禁止使用的用户名模式（与旧版一致）
    private static readonly Regex BannedNamePattern = new(
        @"^(server|client|admin|system|root)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AuthService(IUserRepository users)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
    }

    /// <summary>
    /// 从原始消息中解析登录信息。
    /// 格式: <c>2Ui1n+-#UserName@Password&gt;ChatID</c>
    /// </summary>
    /// <param name="rawMessage">原始消息字符串</param>
    /// <returns>解析出的 <see cref="LoginInfo"/>，格式不正确返回 null</returns>
    public static LoginInfo? ParseLogin(string rawMessage)
    {
        if (string.IsNullOrEmpty(rawMessage))
            return null;

        // 检查登录魔术字
        if (!rawMessage.StartsWith(ProtocolFormat.LoginMagic, StringComparison.Ordinal))
            return null;

        // 去掉魔术字前缀
        var payload = rawMessage[ProtocolFormat.LoginMagic.Length..];

        // 分离 UserName @ Password > ChatID
        var atIndex = payload.IndexOf(ProtocolFormat.LoginUserSeparator);
        var gtIndex = payload.IndexOf(ProtocolFormat.LoginChatSeparator);

        if (atIndex <= 0 || gtIndex <= atIndex + 1)
            return null; // 格式无效

        var userName = payload[..atIndex];
        var password = payload[(atIndex + 1)..gtIndex];
        var chatId = payload[(gtIndex + 1)..];

        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(chatId))
            return null;

        return new LoginInfo(userName, password, chatId);
    }

    /// <summary>
    /// 执行三态认证，与旧版 CheckIdentity 逻辑一致。
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <param name="password">明文密码</param>
    /// <returns>认证结果</returns>
    public LoginResult Authenticate(string userName, string password)
    {
        var passwordHash = HashPassword(password);

        if (_users.Exists(userName))
        {
            // 用户已存在 → 校验密码
            return _users.ValidatePassword(userName, passwordHash)
                ? LoginResult.ReloginSuccess   // 密码正确 → #->0
                : LoginResult.WrongPassword;   // 密码错误 → #->1
        }

        // 新用户 → 自动注册
        _users.Add(new UserInfo(userName, passwordHash));
        return LoginResult.NewUserRegistered; // → #->2
    }

    /// <summary>
    /// 校验用户名是否合法。
    /// </summary>
    /// <returns>null 表示合法，否则返回错误消息</returns>
    public static string? ValidateUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return "用户名不能为空。";

        if (userName.Length > 20)
            return "用户名不能超过 20 个字符。";

        if (BannedNamePattern.IsMatch(userName))
            return $"用户名 '{userName}' 被禁止使用。";

        return null;
    }

    /// <summary>
    /// SHA256 哈希密码（替代旧版明文存储）。
    /// </summary>
    public static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexStringLower(bytes);
    }
}
