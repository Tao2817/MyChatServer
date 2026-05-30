using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TChatS.Protocol;
using TChatS.Service;
using Xunit;

namespace TChatS.Integration.Tests;

/// <summary>
/// 集成测试 — 启动真实 TCP 服务端，用多个客户端模拟完整消息流程。
/// 使用 TcpTextProtocol (以 <c>\n</c> 为分帧分隔符) 简化测试收发。
/// </summary>
public class TChatServerIntegrationTests : IAsyncDisposable
{
    private readonly int _port;
    private readonly ChatServerService _server;
    private readonly CancellationTokenSource _cts = new();

    public TChatServerIntegrationTests()
    {
        _port = GetRandomPort();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // 注册现代协议 (以 \n 分帧) 替代默认的旧版协议
        services.AddSingleton<IProtocolParser>(new TcpTextProtocol());

        services.AddSingleton<TChatS.Storage.IUserRepository, TChatS.Storage.InMemoryUserRepository>();
        services.AddSingleton<TChatS.Core.AuthService>();
        services.AddSingleton<TChatS.Core.ChatRoomManager>();
        services.AddSingleton<TChatS.Core.MessageRouter>();
        services.AddSingleton<TChatS.Transport.ConnectionManager>();
        services.AddSingleton(new ChatServerOptions
        {
            BindAddress = "127.0.0.1",
            Port = _port,
            HeartbeatSeconds = 120
        });
        services.AddSingleton<ChatServerService>();

        var provider = services.BuildServiceProvider();
        _server = provider.GetRequiredService<ChatServerService>();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _server.DisposeAsync();
        _cts.Dispose();
    }

    // ─── 辅助 ───

    private Task StartServerAsync() => _server.StartAsync(_cts.Token);

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", port);
        return c;
    }

    /// <summary>以 UTF-8 发送一条消息，附加 \n 分隔符。</summary>
    private static async Task SendAsync(TcpClient c, string msg)
    {
        var data = Encoding.UTF8.GetBytes(msg + "\n");
        await c.GetStream().WriteAsync(data);
    }

    /// <summary>接收多条以 \n 分隔的消息（粘包安全）。</summary>
    private static async Task<List<string>> ReceiveAllAsync(
        TcpClient c, int maxCount = 10, int idleTimeoutMs = 3000)
    {
        var msgs = new List<string>();
        var leftover = new List<byte>(); // 跨次调用的遗留字节
        var buf = new byte[4096];
        var stream = c.GetStream();

        // 第一轮读取用较长超时
        int perReadTimeout = idleTimeoutMs;

        while (msgs.Count < maxCount)
        {
            using var tcs = new CancellationTokenSource(perReadTimeout);
            int n;
            try
            {
                n = await stream.ReadAsync(buf, tcs.Token);
            }
            catch (OperationCanceledException)
            {
                break; // 超时，没有更多数据
            }

            if (n == 0) break; // 远端关闭

            leftover.AddRange(buf.AsSpan(0, n));

            // 分割所有完整的行
            while (true)
            {
                var idx = leftover.IndexOf((byte)'\n');
                if (idx < 0) break;
                msgs.Add(Encoding.UTF8.GetString(leftover.Take(idx).ToArray()));
                leftover.RemoveRange(0, idx + 1);
                if (msgs.Count >= maxCount) return msgs;
            }

            // 后续读取用较短超时（数据已在路上）
            perReadTimeout = 200;
        }

        return msgs;
    }

    private static int GetRandomPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ═══════════════════════════════════════════════════════════
    // 测试
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task NewUser_Login_ReceivesWelcome()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var c = await ConnectAsync(_port);
        await SendAsync(c, "2Ui1n+-#Alice@1234>Room1");
        var msgs = await ReceiveAllAsync(c);

        Assert.Contains("#->2", msgs);
        Assert.Contains(msgs, m => m.Contains("欢迎加入群聊"));
    }

    [Fact]
    public async Task NewUser_ReceivesUserList_WithOtherUsers()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var a = await ConnectAsync(_port);
        await SendAsync(a, "2Ui1n+-#Alice@1>Room1");
        await ReceiveAllAsync(a); // 消费 Alice 的欢迎消息

        using var b = await ConnectAsync(_port);
        await SendAsync(b, "2Ui1n+-#Bob@1>Room1");
        var bMsgs = await ReceiveAllAsync(b);

        // Bob 应收到 #->5 包含 Alice
        Assert.Contains(bMsgs, m => m.StartsWith("#->5") && m.Contains("Alice"));
    }

    [Fact]
    public async Task NewUser_BroadcastsJoinToExistingUsers()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var a = await ConnectAsync(_port);
        await SendAsync(a, "2Ui1n+-#Alice@1>X");
        await ReceiveAllAsync(a);

        using var b = await ConnectAsync(_port);
        await SendAsync(b, "2Ui1n+-#Bob@1>X");

        // Alice 应收到 #->6Bob (Bob 加入)
        var aMsgs = await ReceiveAllAsync(a);
        Assert.Contains("#->6Bob", aMsgs);
    }

    [Fact]
    public async Task ExistingUser_Relogin_Sends0()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var c1 = await ConnectAsync(_port);
        await SendAsync(c1, "2Ui1n+-#Tester@secret>Room1");
        await ReceiveAllAsync(c1);
        c1.Close();

        using var c2 = await ConnectAsync(_port);
        await SendAsync(c2, "2Ui1n+-#Tester@secret>Room1");
        var msgs = await ReceiveAllAsync(c2);

        Assert.Contains("#->0", msgs);
        Assert.Contains(msgs, m => m.Contains("欢迎回来"));
    }

    [Fact]
    public async Task WrongPassword_RejectedWith1()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var c1 = await ConnectAsync(_port);
        await SendAsync(c1, "2Ui1n+-#Eve@correct>Room1");
        await ReceiveAllAsync(c1);
        c1.Close();

        using var c2 = await ConnectAsync(_port);
        await SendAsync(c2, "2Ui1n+-#Eve@WRONG>Room1");
        var msgs = await ReceiveAllAsync(c2);

        Assert.Contains("#->1", msgs);
    }

    [Fact]
    public async Task GroupChat_BroadcastToOthers()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        using var bob = await ConnectAsync(_port);

        await SendAsync(alice, "2Ui1n+-#Alice@1>Lobby");
        await ReceiveAllAsync(alice);
        await SendAsync(bob, "2Ui1n+-#Bob@1>Lobby");
        await ReceiveAllAsync(bob);

        await SendAsync(alice, "Hello everyone!");
        var bobMsgs = await ReceiveAllAsync(bob);

        Assert.Contains(bobMsgs, m => m == "<Alice>: Hello everyone!");
    }

    [Fact]
    public async Task PrivateMessage_DeliversToTarget()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        using var bob = await ConnectAsync(_port);

        await SendAsync(alice, "2Ui1n+-#Alice@1>Room1");
        await ReceiveAllAsync(alice);
        await SendAsync(bob, "2Ui1n+-#Bob@1>Room1");
        await ReceiveAllAsync(bob);

        await SendAsync(alice, "#->7Bob#->Secret!");
        var bobMsgs = await ReceiveAllAsync(bob);

        Assert.Contains(bobMsgs, m => m.Contains("Private Message From<Alice>"));
        Assert.Contains(bobMsgs, m => m.Contains("Secret!"));
    }

    [Fact]
    public async Task PrivateMessage_TargetNotFound_SendsLeave()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        await SendAsync(alice, "2Ui1n+-#Alice@1>Room1");
        await ReceiveAllAsync(alice);

        await SendAsync(alice, "#->7Nobody#->Hello?");
        var msgs = await ReceiveAllAsync(alice);

        Assert.Contains("#->8Nobody", msgs);
    }

    [Fact]
    public async Task UserDisconnect_BroadcastsLeave()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        using var bob = await ConnectAsync(_port);

        await SendAsync(alice, "2Ui1n+-#Alice@1>Room1");
        await ReceiveAllAsync(alice);
        await SendAsync(bob, "2Ui1n+-#Bob@1>Room1");
        await ReceiveAllAsync(bob);
        await ReceiveAllAsync(alice); // 消费 Alice 收到的 #->6Bob

        bob.Close();
        await Task.Delay(200);

        var aliceMsgs = await ReceiveAllAsync(alice);
        Assert.Contains("#->8Bob", aliceMsgs);
    }

    [Fact]
    public async Task DifferentRooms_Isolated()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var a = await ConnectAsync(_port);
        using var b = await ConnectAsync(_port);

        await SendAsync(a, "2Ui1n+-#A@1>RoomA");
        await ReceiveAllAsync(a);
        await SendAsync(b, "2Ui1n+-#B@1>RoomB");
        await ReceiveAllAsync(b);

        await SendAsync(a, "Only RoomA");
        var bMsgs = await ReceiveAllAsync(b, idleTimeoutMs: 1000);

        Assert.DoesNotContain(bMsgs, m => m.Contains("Only RoomA"));
    }

    [Fact]
    public async Task UserName_CaseInsensitive_Relogin()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var c1 = await ConnectAsync(_port);
        await SendAsync(c1, "2Ui1n+-#Alice@1>Room1");
        await ReceiveAllAsync(c1);
        c1.Close();

        using var c2 = await ConnectAsync(_port);
        await SendAsync(c2, "2Ui1n+-#alice@1>Room1");
        var msgs = await ReceiveAllAsync(c2);

        Assert.Contains("#->0", msgs); // 重新登录，不是新用户
    }
}
