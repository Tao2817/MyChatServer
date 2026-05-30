using TChatS.Core;
using TChatS.Storage;
using Xunit;

namespace TChatS.Core.Tests;

public class AuthServiceTests
{
    private readonly InMemoryUserRepository _repo = new();
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _auth = new AuthService(_repo);
    }

    // ─── ParseLogin ───

    [Theory]
    [InlineData("2Ui1n+-#Tao@1234>Room1", "Tao", "1234", "Room1")]
    [InlineData("2Ui1n+-#Alice@pass>Lobby", "Alice", "pass", "Lobby")]
    [InlineData("2Ui1n+-#Test@!@#$>Chat-X", "Test", "!@#$", "Chat-X")]
    public void ParseLogin_ValidFormat_ExtractsCorrectly(string raw, string user, string pwd, string chatId)
    {
        var info = AuthService.ParseLogin(raw);

        Assert.NotNull(info);
        Assert.Equal(user, info.UserName);
        Assert.Equal(pwd, info.Password);
        Assert.Equal(chatId, info.ChatId);
    }

    [Theory]
    [InlineData("")]                             // 空字符串
    [InlineData("Hello")]                        // 无魔术字
    [InlineData("2Ui1n+-#@>")]                   // 空字段
    [InlineData("2Ui1n+-#User>Chat")]            // 缺少 @Password
    [InlineData("2Ui1n+-#User@Pass")]            // 缺少 >ChatID
    [InlineData("2Ui1n+-#@Pass>Chat")]            // 缺少 UserName
    public void ParseLogin_InvalidFormat_ReturnsNull(string raw)
    {
        var info = AuthService.ParseLogin(raw);
        Assert.Null(info);
    }

    // ─── Authenticate (三态) ───

    [Fact]
    public void Authenticate_NewUser_ReturnsNewUserRegistered()
    {
        var result = _auth.Authenticate("NewUser", "password");
        Assert.Equal(LoginResult.NewUserRegistered, result);
        Assert.True(_repo.Exists("NewUser"));
    }

    [Fact]
    public void Authenticate_ExistingUser_CorrectPassword_ReturnsReloginSuccess()
    {
        _auth.Authenticate("Bob", "secret"); // 先注册

        var result = _auth.Authenticate("Bob", "secret"); // 再登录
        Assert.Equal(LoginResult.ReloginSuccess, result);
    }

    [Fact]
    public void Authenticate_ExistingUser_WrongPassword_ReturnsWrongPassword()
    {
        _auth.Authenticate("Bob", "secret"); // 先注册

        var result = _auth.Authenticate("Bob", "wrong"); // 错误密码
        Assert.Equal(LoginResult.WrongPassword, result);
    }

    [Fact]
    public void Authenticate_StoresPasswordHash_NotPlaintext()
    {
        _auth.Authenticate("User", "mypassword");

        var stored = _repo.FindByUserName("User");
        Assert.NotNull(stored);
        Assert.NotEqual("mypassword", stored!.PasswordHash); // 不应存明文
        Assert.Equal(64, stored.PasswordHash.Length);          // SHA256 hex = 64 chars
    }

    [Fact]
    public void Authenticate_UserNameCaseInsensitive()
    {
        _auth.Authenticate("Alice", "pwd");
        var result = _auth.Authenticate("alice", "pwd"); // 大小写不敏感
        Assert.Equal(LoginResult.ReloginSuccess, result);
    }

    // ─── ValidateUserName ───

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("validUser", true)]
    [InlineData("User123", true)]
    [InlineData("server", false)]   // 禁止
    [InlineData("Server", false)]   // 禁止 (忽略大小写)
    [InlineData("SERVER", false)]   // 禁止
    [InlineData("client", false)]   // 禁止
    [InlineData("admin", false)]    // 禁止
    [InlineData("system", false)]   // 禁止
    [InlineData("root", false)]     // 禁止
    public void ValidateUserName_EnforcesBannedNames(string name, bool shouldBeValid)
    {
        var error = AuthService.ValidateUserName(name);
        if (shouldBeValid)
            Assert.Null(error);
        else
            Assert.NotNull(error);
    }

    [Fact]
    public void ValidateUserName_RejectsTooLong()
    {
        var longName = new string('A', 21);
        var error = AuthService.ValidateUserName(longName);
        Assert.NotNull(error);
    }

    // ─── HashPassword ───

    [Fact]
    public void HashPassword_SameInput_ProducesSameHash()
    {
        var h1 = AuthService.HashPassword("test");
        var h2 = AuthService.HashPassword("test");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashPassword_DifferentInput_ProducesDifferentHash()
    {
        var h1 = AuthService.HashPassword("test1");
        var h2 = AuthService.HashPassword("test2");
        Assert.NotEqual(h1, h2);
    }
}
