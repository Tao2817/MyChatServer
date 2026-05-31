using TChatS.StressTest;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 解析参数
if (args.Length == 0 || args.Contains("--help"))
{
    StressTestOptions.PrintHelp();
    return;
}

var opts = StressTestOptions.Parse(args);

// 验证参数
if (opts.Connections <= 0 || opts.DurationSeconds <= 0)
{
    Console.WriteLine("错误: --connections 和 --duration 必须大于 0");
    return;
}

var validScenarios = new[] { "connection-flood", "login-storm", "chat-throughput", "sustained" };
if (!validScenarios.Contains(opts.Scenario))
{
    Console.WriteLine($"错误: 无效的场景 '{opts.Scenario}'，有效值: {string.Join(", ", validScenarios)}");
    return;
}

// 创建指标收集器和测试编排器
var metrics = new MetricsCollector();
var runner = new StressTestRunner(opts, metrics);

// 处理 Ctrl+C 优雅停止
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("收到中断信号，正在停止测试...");
    runner.Stop();
};

// 运行测试
try
{
    await runner.RunAsync();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"致命错误: {ex}");
    Console.ResetColor();
}
