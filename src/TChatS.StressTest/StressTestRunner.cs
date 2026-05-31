using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TChatS.Protocol;

namespace TChatS.StressTest;

/// <summary>
/// 压力测试编排器，管理虚拟用户生命周期并执行测试场景。
/// </summary>
public sealed class StressTestRunner
{
    private readonly StressTestOptions _opts;
    private readonly MetricsCollector _metrics;
    private readonly TcpJsonProtocol _protocol = new();
    private readonly CancellationTokenSource _testCts = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public StressTestRunner(StressTestOptions opts, MetricsCollector metrics)
    {
        _opts = opts;
        _metrics = metrics;
    }

    /// <summary>触发优雅停止。</summary>
    public void Stop()
    {
        _testCts.Cancel();
    }

    public async Task RunAsync()
    {
        _opts.PrintSummary();
        _metrics.StartSnapshotTimer(5);

        Console.WriteLine("正在启动压力测试...");
        Console.WriteLine();

        try
        {
            var tasks = new List<Task>();
            var rampUpIntervalMs = _opts.RampUpSeconds > 0
                ? (_opts.RampUpSeconds * 1000.0) / _opts.Connections
                : 0;

            for (int i = 0; i < _opts.Connections; i++)
            {
                var userIndex = i;
                var roomIndex = i % _opts.ChatRooms;
                var task = RunUserAsync(userIndex, roomIndex);
                tasks.Add(task);

                // 爬坡: 逐步建立连接
                if (rampUpIntervalMs > 0 && i < _opts.Connections - 1)
                    await Task.Delay((int)rampUpIntervalMs);
            }

            // 等待测试持续时间到期
            var delayTask = Task.Delay(_opts.DurationSeconds * 1000, _testCts.Token);
            try
            {
                await delayTask;
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C 中断
            }

            Console.WriteLine();
            Console.WriteLine("测试时间到，正在收集结果...");

            // 取消所有用户任务
            _testCts.Cancel();

            // 等待所有用户结束
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"测试异常: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            _metrics.StopSnapshotTimer();
        }

        _metrics.PrintReport(_opts);
    }

    /// <summary>
    /// 单个虚拟用户的生命周期。
    /// </summary>
    private async Task RunUserAsync(int userIndex, int roomIndex)
    {
        var roomName = $"Room_{roomIndex}";
        var userName = $"StressUser_{userIndex}";

        _metrics.RecordConnectionAttempt();

        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync(_opts.Host, _opts.Port, _testCts.Token);
            var stream = client.GetStream();

            // 启动后台接收循环
            var receiveTask = ReceiveLoopAsync(client, stream, userIndex);

            // connection-flood: 只连接不发登录
            if (_opts.Scenario == "connection-flood")
            {
                _metrics.RecordConnectionSuccess();
                // 保持连接直到测试结束
                await WaitUntilEndAsync();
                receiveTask.Forget();
                return;
            }

            // 发送登录消息
            var loginJson = JsonSerializer.Serialize(new
            {
                type = "login",
                args = new { userName, password = "stress", chatId = roomName }
            });
            await SendFrameAsync(stream, loginJson);

            // 等待登录响应 (带超时)
            using var loginCts = new CancellationTokenSource(5000);
            try
            {
                // 简单等待一小段时间等登录响应到达
                await Task.Delay(500, loginCts.Token);
                _metrics.RecordConnectionSuccess();
            }
            catch (OperationCanceledException)
            {
                _metrics.RecordConnectionFailed();
                receiveTask.Forget();
                return;
            }

            // login-storm: 登录后等一会儿就结束
            if (_opts.Scenario == "login-storm")
            {
                await WaitUntilEndAsync();
                receiveTask.Forget();
                return;
            }

            // chat-throughput / sustained: 持续发送消息
            var intervalMs = 1000 / Math.Max(1, _opts.MessagesPerSec);
            var messageContentBase = GenerateMessageContent(_opts.MessageSize);

            while (!_testCts.Token.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, _testCts.Token);

                var sendTimestamp = _stopwatch.Elapsed.Ticks;
                var content = $"msg_{userIndex}_{sendTimestamp}_{messageContentBase}";

                string json;
                if (_opts.Scenario == "sustained" && userIndex % 5 == 0)
                {
                    // 20% 的用户发送私聊给下一个用户
                    var targetUser = $"StressUser_{(userIndex + 1) % _opts.Connections}";
                    json = JsonSerializer.Serialize(new
                    {
                        type = "private",
                        args = new { target = targetUser, content }
                    });
                }
                else
                {
                    json = JsonSerializer.Serialize(new
                    {
                        type = "normal",
                        args = new { content }
                    });
                }

                try
                {
                    await SendFrameAsync(stream, json, _testCts.Token);
                    _metrics.RecordMessageSent();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    _metrics.RecordMessageError();
                }
            }

            receiveTask.Forget();
        }
        catch (OperationCanceledException)
        {
            // 正常结束
        }
        catch (SocketException)
        {
            _metrics.RecordConnectionFailed();
        }
        catch (Exception ex)
        {
            _metrics.RecordConnectionFailed();
            if (_testCts.Token.IsCancellationRequested)
                return; // 测试结束时的正常异常，静默
            Debug.WriteLine($"User {userIndex} error: {ex.Message}");
        }
        finally
        {
            _metrics.RecordConnectionClosed();
            try { client?.Close(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 后台接收循环：持续读取并解析服务器发来的帧。
    /// </summary>
    private async Task ReceiveLoopAsync(TcpClient client, NetworkStream stream, int userIndex)
    {
        var buffer = new byte[8192];
        var accumulated = new List<byte>();

        try
        {
            while (!_testCts.Token.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer, _testCts.Token);
                }
                catch (OperationCanceledException) { break; }

                if (bytesRead == 0)
                    break; // 服务器关闭连接

                accumulated.AddRange(buffer.AsSpan(0, bytesRead));

                // 解析所有完整帧
                while (TryParseFrame(accumulated, out var json))
                {
                    _metrics.RecordMessageReceived();

                    // 尝试提取延迟信息
                    ExtractLatency(json);

                    if (_testCts.Token.IsCancellationRequested)
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* 正常 */ }
        catch (ObjectDisposedException) { /* 连接已关闭 */ }
        catch (IOException) { /* 连接重置 */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"Receive {userIndex} error: {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试从累积缓冲区中解析一个完整的长度前缀帧。
    /// 返回 true 表示成功解析了一个帧 (json 已提取)。
    /// </summary>
    private static bool TryParseFrame(List<byte> accumulated, out string json)
    {
        json = "";

        if (accumulated.Count < 4)
            return false;

        var lenArray = accumulated.Take(4).ToArray();
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(lenArray);

        if (payloadLength < 0 || payloadLength > 65536)
        {
            // 非法长度，丢弃这4字节
            accumulated.RemoveRange(0, 4);
            return false;
        }

        var frameLen = 4 + payloadLength;
        if (accumulated.Count < frameLen)
            return false;

        json = Encoding.UTF8.GetString(
            accumulated.GetRange(4, payloadLength).ToArray());
        accumulated.RemoveRange(0, frameLen);
        return true;
    }

    /// <summary>
    /// 从收到的 JSON 消息中提取时间戳并计算延迟。
    /// </summary>
    private void ExtractLatency(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            // 只对 chat 和 dispatchPrivate 计算延迟
            if (type != "chat" && type != "dispatchPrivate")
                return;

            var content = root.GetProperty("args").GetProperty("content").GetString();
            if (string.IsNullOrEmpty(content)) return;

            // 格式: msg_{senderIndex}_{ticks}_{padding}
            var parts = content!.Split('_');
            if (parts.Length < 3) return;

            if (long.TryParse(parts[2], out var sendTicks))
            {
                var nowTicks = _stopwatch.Elapsed.Ticks;
                var latencyUs = (nowTicks - sendTicks) * 1_000_000 / Stopwatch.Frequency;
                if (latencyUs > 0)
                    _metrics.RecordLatency(latencyUs);
            }
        }
        catch
        {
            // 忽略解析错误（可能是服务器系统消息）
        }
    }

    /// <summary>
    /// 发送一个长度前缀帧。
    /// </summary>
    private static async Task SendFrameAsync(
        NetworkStream stream, string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, ct);
    }

    private async Task WaitUntilEndAsync()
    {
        try
        {
            await Task.Delay(_opts.DurationSeconds * 1000, _testCts.Token);
        }
        catch (OperationCanceledException) { }
    }

    private static string GenerateMessageContent(int minSize)
    {
        // 生成填充字符，减去前缀 msg_X_TICKS_ 的长度（约 45 字符）
        var padding = Math.Max(0, minSize - 45);
        return new string('x', padding);
    }
}

/// <summary>
/// Task 扩展：fire-and-forget 并捕获异常。
/// </summary>
internal static class TaskExtensions
{
    public static void Forget(this Task task)
    {
        task.ContinueWith(t =>
        {
            if (t.Exception != null)
                Debug.WriteLine($"Background task error: {t.Exception.InnerException?.Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
