using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TChatS.Protocol;
using TChatS.Service;
using Xunit;

namespace TChatS.Integration.Tests;

/// <summary>
/// 集成测试 — 使用 TcpJsonProtocol (长度前缀分帧 + JSON 业务协议)。
/// </summary>
public class TChatServerIntegrationTests_JsonProtocol : IAsyncDisposable
{
    private readonly int _port;
    private readonly ChatServerService _server;
    private readonly CancellationTokenSource _cts = new();

    public TChatServerIntegrationTests_JsonProtocol()
    {
        _port = GetRandomPort();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // JSON 协议栈
        services.AddSingleton<IProtocolParser>(new TcpJsonProtocol());
        services.AddSingleton<IServiceProtocol, JsonServiceProtocol>();

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

    /// <summary>发送一条 JSON 消息，带 4 字节大端长度前缀。</summary>
    private static async Task SendJsonAsync(TcpClient c, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        await c.GetStream().WriteAsync(frame);
    }

    /// <summary>快速构造并发送一条 JSON 消息。</summary>
    private static Task SendAsync(TcpClient c, string type, object args)
    {
        var json = JsonSerializer.Serialize(new { type, args });
        return SendJsonAsync(c, json);
    }

    /// <summary>接收多条长度前缀分帧的 JSON 消息。</summary>
    private static async Task<List<string>> ReceiveAllAsync(
        TcpClient c, int maxCount = 10, int idleTimeoutMs = 3000)
    {
        var msgs = new List<string>();
        var leftover = new List<byte>();
        var buf = new byte[4096];
        var stream = c.GetStream();

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
                break;
            }

            if (n == 0) break;

            leftover.AddRange(buf.AsSpan(0, n));

            // 尝试解析完整的长度前缀帧
            while (true)
            {
                if (leftover.Count < 4)
                    break;

                var payloadLen = BinaryPrimitives.ReadInt32BigEndian(
                    leftover.ToArray().AsSpan(0, 4));

                if (payloadLen < 0 || payloadLen > 65536)
                    break; // 非法长度，等更多数据

                var frameLen = 4 + payloadLen;
                if (leftover.Count < frameLen)
                    break;

                msgs.Add(Encoding.UTF8.GetString(
                    leftover.GetRange(4, payloadLen).ToArray()));
                leftover.RemoveRange(0, frameLen);

                if (msgs.Count >= maxCount) return msgs;
            }

            perReadTimeout = 200;
        }

        return msgs;
    }

    /// <summary>检查 JSON 消息是否匹配给定的 type 和 args 谓词。</summary>
    private static bool IsMsg(string json, string type, Func<JsonElement, bool>? argsPredicate = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != type)
                return false;
            if (argsPredicate != null && !argsPredicate(root.GetProperty("args")))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
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
        await SendAsync(c, "login", new { userName = "Alice", password = "1234", chatId = "Room1" });
        var msgs = await ReceiveAllAsync(c);

        Assert.Contains(msgs, m => IsMsg(m, "newUser"));
        Assert.Contains(msgs, m => IsMsg(m, "serverMessage",
            a => a.GetProperty("content").GetString()!.Contains("欢迎加入群聊")));
    }

    [Fact]
    public async Task NewUser_ReceivesUserList_WithOtherUsers()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var a = await ConnectAsync(_port);
        await SendAsync(a, "login", new { userName = "Alice", password = "1", chatId = "Room1" });
        await ReceiveAllAsync(a);

        using var b = await ConnectAsync(_port);
        await SendAsync(b, "login", new { userName = "Bob", password = "1", chatId = "Room1" });
        var bMsgs = await ReceiveAllAsync(b);

        Assert.Contains(bMsgs, m => IsMsg(m, "userList",
            a => a.GetProperty("users").EnumerateArray().Any(u => u.GetString() == "Alice")));
    }

    [Fact]
    public async Task NewUser_BroadcastsJoinToExistingUsers()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var a = await ConnectAsync(_port);
        await SendAsync(a, "login", new { userName = "Alice", password = "1", chatId = "X" });
        await ReceiveAllAsync(a);

        using var b = await ConnectAsync(_port);
        await SendAsync(b, "login", new { userName = "Bob", password = "1", chatId = "X" });

        var aMsgs = await ReceiveAllAsync(a);
        Assert.Contains(aMsgs, m => IsMsg(m, "userJoin",
            a => a.GetProperty("userName").GetString() == "Bob"));
    }

    [Fact]
    public async Task ExistingUser_Relogin_SendsRelogin()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var c1 = await ConnectAsync(_port);
        await SendAsync(c1, "login", new { userName = "Tester", password = "secret", chatId = "Room1" });
        await ReceiveAllAsync(c1);
        c1.Close();

        using var c2 = await ConnectAsync(_port);
        await SendAsync(c2, "login", new { userName = "Tester", password = "secret", chatId = "Room1" });
        var msgs = await ReceiveAllAsync(c2);

        Assert.Contains(msgs, m => IsMsg(m, "relogin"));
        Assert.Contains(msgs, m => IsMsg(m, "serverMessage",
            a => a.GetProperty("content").GetString()!.Contains("欢迎回来")));
    }

    [Fact]
    public async Task WrongPassword_RejectedWithWrongPassword()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var c1 = await ConnectAsync(_port);
        await SendAsync(c1, "login", new { userName = "Eve", password = "correct", chatId = "Room1" });
        await ReceiveAllAsync(c1);
        c1.Close();

        using var c2 = await ConnectAsync(_port);
        await SendAsync(c2, "login", new { userName = "Eve", password = "WRONG", chatId = "Room1" });
        var msgs = await ReceiveAllAsync(c2);

        Assert.Contains(msgs, m => IsMsg(m, "wrongPassword"));
    }

    [Fact]
    public async Task GroupChat_BroadcastToOthers()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        using var bob = await ConnectAsync(_port);

        await SendAsync(alice, "login", new { userName = "Alice", password = "1", chatId = "Lobby" });
        await ReceiveAllAsync(alice);
        await SendAsync(bob, "login", new { userName = "Bob", password = "1", chatId = "Lobby" });
        await ReceiveAllAsync(bob);

        await SendAsync(alice, "normal", new { content = "Hello everyone!" });
        var bobMsgs = await ReceiveAllAsync(bob);

        Assert.Contains(bobMsgs, m => IsMsg(m, "chat",
            a => a.GetProperty("userName").GetString() == "Alice"
              && a.GetProperty("content").GetString() == "Hello everyone!"));
    }

    [Fact]
    public async Task PrivateMessage_DeliversToTarget()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        using var bob = await ConnectAsync(_port);

        await SendAsync(alice, "login", new { userName = "Alice", password = "1", chatId = "Room1" });
        await ReceiveAllAsync(alice);
        await SendAsync(bob, "login", new { userName = "Bob", password = "1", chatId = "Room1" });
        await ReceiveAllAsync(bob);

        await SendAsync(alice, "private", new { target = "Bob", content = "Secret!" });
        var bobMsgs = await ReceiveAllAsync(bob);

        Assert.Contains(bobMsgs, m => IsMsg(m, "dispatchPrivate",
            a => a.GetProperty("userName").GetString() == "Alice"
              && a.GetProperty("content").GetString() == "Secret!"));
    }

    [Fact]
    public async Task PrivateMessage_TargetNotFound_SendsLeave()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        await SendAsync(alice, "login", new { userName = "Alice", password = "1", chatId = "Room1" });
        await ReceiveAllAsync(alice);

        await SendAsync(alice, "private", new { target = "Nobody", content = "Hello?" });
        var msgs = await ReceiveAllAsync(alice);

        Assert.Contains(msgs, m => IsMsg(m, "userLeave",
            a => a.GetProperty("userName").GetString() == "Nobody"));
    }

    [Fact]
    public async Task UserDisconnect_BroadcastsLeave()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var alice = await ConnectAsync(_port);
        using var bob = await ConnectAsync(_port);

        await SendAsync(alice, "login", new { userName = "Alice", password = "1", chatId = "Room1" });
        await ReceiveAllAsync(alice);
        await SendAsync(bob, "login", new { userName = "Bob", password = "1", chatId = "Room1" });
        await ReceiveAllAsync(bob);
        await ReceiveAllAsync(alice); // 消费 Alice 收到的 userJoin

        bob.Close();
        await Task.Delay(200);

        var aliceMsgs = await ReceiveAllAsync(alice);
        Assert.Contains(aliceMsgs, m => IsMsg(m, "userLeave",
            a => a.GetProperty("userName").GetString() == "Bob"));
    }

    [Fact]
    public async Task DifferentRooms_Isolated()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var a = await ConnectAsync(_port);
        using var b = await ConnectAsync(_port);

        await SendAsync(a, "login", new { userName = "A", password = "1", chatId = "RoomA" });
        await ReceiveAllAsync(a);
        await SendAsync(b, "login", new { userName = "B", password = "1", chatId = "RoomB" });
        await ReceiveAllAsync(b);

        await SendAsync(a, "normal", new { content = "Only RoomA" });
        var bMsgs = await ReceiveAllAsync(b, idleTimeoutMs: 1000);

        Assert.DoesNotContain(bMsgs, m => IsMsg(m, "chat",
            a => a.GetProperty("content").GetString()!.Contains("Only RoomA")));
    }

    [Fact]
    public async Task UserName_CaseInsensitive_Relogin()
    {
        await StartServerAsync();
        await Task.Delay(100);

        using var c1 = await ConnectAsync(_port);
        await SendAsync(c1, "login", new { userName = "Alice", password = "1", chatId = "Room1" });
        await ReceiveAllAsync(c1);
        c1.Close();

        using var c2 = await ConnectAsync(_port);
        await SendAsync(c2, "login", new { userName = "alice", password = "1", chatId = "Room1" });
        var msgs = await ReceiveAllAsync(c2);

        Assert.Contains(msgs, m => IsMsg(m, "relogin"));
    }
}
