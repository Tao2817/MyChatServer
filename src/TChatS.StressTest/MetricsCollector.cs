using System.Collections.Concurrent;
using System.Diagnostics;

namespace TChatS.StressTest;

/// <summary>
/// 线程安全的压力测试指标收集器。
/// </summary>
public sealed class MetricsCollector
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DateTime _startTime = DateTime.UtcNow;

    // 连接指标
    private long _connectionsAttempted;
    private long _connectionsSucceeded;
    private long _connectionsFailed;
    private long _connectionsActive;
    private long _connectionsPeak;

    // 消息指标
    private long _messagesSent;
    private long _messagesReceived;
    private long _messageErrors;

    // 延迟样本 (微秒)
    private readonly object _latencyLock = new();
    private readonly List<long> _latencySamples = [];

    // 吞吐量按秒分桶: key = 秒偏移, value = 消息数
    private readonly ConcurrentDictionary<long, long> _throughputSent = new();
    private readonly ConcurrentDictionary<long, long> _throughputRecv = new();

    // 定期快照
    private readonly CancellationTokenSource _snapshotCts = new();
    private Task? _snapshotTask;

    // ─── 连接指标 ───

    public void RecordConnectionAttempt()
    {
        Interlocked.Increment(ref _connectionsAttempted);
        var active = Interlocked.Increment(ref _connectionsActive);
        UpdatePeak(active);
    }

    public void RecordConnectionSuccess()
    {
        Interlocked.Increment(ref _connectionsSucceeded);
    }

    public void RecordConnectionFailed()
    {
        Interlocked.Increment(ref _connectionsFailed);
        Interlocked.Decrement(ref _connectionsActive);
    }

    public void RecordConnectionClosed()
    {
        Interlocked.Decrement(ref _connectionsActive);
    }

    private void UpdatePeak(long current)
    {
        long peak;
        do
        {
            peak = Interlocked.Read(ref _connectionsPeak);
            if (current <= peak) return;
        }
        while (Interlocked.CompareExchange(ref _connectionsPeak, current, peak) != peak);
    }

    // ─── 消息指标 ───

    public void RecordMessageSent()
    {
        Interlocked.Increment(ref _messagesSent);
        var second = _stopwatch.Elapsed.Ticks / TimeSpan.TicksPerSecond;
        _throughputSent.AddOrUpdate(second, 1, (_, v) => v + 1);
    }

    public void RecordMessageReceived()
    {
        Interlocked.Increment(ref _messagesReceived);
        var second = _stopwatch.Elapsed.Ticks / TimeSpan.TicksPerSecond;
        _throughputRecv.AddOrUpdate(second, 1, (_, v) => v + 1);
    }

    public void RecordMessageError()
    {
        Interlocked.Increment(ref _messageErrors);
    }

    /// <summary>记录延迟 (微秒)。</summary>
    public void RecordLatency(long microseconds)
    {
        lock (_latencyLock)
        {
            _latencySamples.Add(microseconds);
        }
    }

    // ─── 定期快照 ───

    public void StartSnapshotTimer(int intervalSeconds = 5)
    {
        _snapshotTask = Task.Run(async () =>
        {
            while (!_snapshotCts.Token.IsCancellationRequested)
            {
                await Task.Delay(intervalSeconds * 1000, _snapshotCts.Token);
                PrintSnapshot();
            }
        }, _snapshotCts.Token);
    }

    public void StopSnapshotTimer()
    {
        _snapshotCts.Cancel();
    }

    public void PrintSnapshot()
    {
        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        var sent = Interlocked.Read(ref _messagesSent);
        var recv = Interlocked.Read(ref _messagesReceived);
        var activeConnect = Interlocked.Read(ref _connectionsActive);
        var peakConnect = Interlocked.Read(ref _connectionsPeak);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"── [{elapsed,5:F0}s] 活跃连接: {activeConnect,4} | 峰值: {peakConnect,4} | " +
                          $"发送: {sent,8} | 接收: {recv,8} | 错误: {Interlocked.Read(ref _messageErrors),4}");
        Console.ResetColor();
    }

    // ─── 汇总报告 ───

    public void PrintReport(StressTestOptions opts)
    {
        var elapsed = _stopwatch.Elapsed;
        var sent = Interlocked.Read(ref _messagesSent);
        var recv = Interlocked.Read(ref _messagesReceived);
        var errors = Interlocked.Read(ref _messageErrors);
        var attempted = Interlocked.Read(ref _connectionsAttempted);
        var succeeded = Interlocked.Read(ref _connectionsSucceeded);
        var failed = Interlocked.Read(ref _connectionsFailed);
        var peak = Interlocked.Read(ref _connectionsPeak);

        // 延迟统计
        long[] sortedLatencies;
        lock (_latencyLock)
        {
            sortedLatencies = [.._latencySamples];
        }
        Array.Sort(sortedLatencies);

        var avgLatency = sortedLatencies.Length > 0
            ? sortedLatencies.Average() / 1000.0 : 0;
        var minLatency = sortedLatencies.Length > 0
            ? sortedLatencies[0] / 1000.0 : 0;
        var maxLatency = sortedLatencies.Length > 0
            ? sortedLatencies[^1] / 1000.0 : 0;
        var p50 = Percentile(sortedLatencies, 50) / 1000.0;
        var p90 = Percentile(sortedLatencies, 90) / 1000.0;
        var p99 = Percentile(sortedLatencies, 99) / 1000.0;

        // 吞吐量统计
        var sentValues = _throughputSent.Values.Where(v => v > 0).ToList();
        var avgThroughput = elapsed.TotalSeconds > 0 ? sent / elapsed.TotalSeconds : 0;
        var peakThroughput = sentValues.Count > 0 ? sentValues.Max() : 0;

        var loginSuccessRate = attempted > 0 ? (double)succeeded / attempted * 100 : 0;
        var msgSuccessRate = sent > 0 ? (double)(sent - errors) / sent * 100 : 100;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  TChatS 压力测试报告");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.ResetColor();

        Console.WriteLine($"场景: {opts.Scenario}");
        Console.WriteLine($"持续时间: {elapsed.TotalSeconds:F0}s (目标 {opts.DurationSeconds}s) | 爬坡: {opts.RampUpSeconds}s");
        Console.WriteLine($"目标连接数: {opts.Connections} | 峰值连接: {peak}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("── 连接 ─────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  尝试: {attempted} | 成功: {succeeded} | 失败: {failed} | 峰值活跃: {peak}");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("── 消息 ─────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  发送: {sent} | 接收: {recv} | 错误: {errors}");
        Console.WriteLine($"  吞吐: {avgThroughput:F0} msg/s (avg) | {peakThroughput} msg/s (peak)");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("── 延迟 ─────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  样本数: {sortedLatencies.Length}");
        Console.WriteLine($"  Min: {minLatency:F2}ms | Max: {maxLatency:F2}ms | Avg: {avgLatency:F2}ms");
        Console.WriteLine($"  P50: {p50:F2}ms | P90: {p90:F2}ms | P99: {p99:F2}ms");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("── 通过率 ───────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  登录成功率: {loginSuccessRate:F1}% | 消息成功率: {msgSuccessRate:F1}%");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("═══════════════════════════════════════════");
        Console.ResetColor();
    }

    private static double Percentile(long[] sorted, int percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Length) index = sorted.Length - 1;
        return sorted[index];
    }
}
