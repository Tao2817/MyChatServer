using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TChatS.Service;

// ─── 加载配置 ───
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// ─── 日志持久化 ───
var logDir = configuration.GetValue("LogDirectory", "logs");
var logPath = Path.Combine(logDir, "tchats-.log");
var clearLogs = configuration.GetValue("ClearLogsOnStartup", false);

if (clearLogs && Directory.Exists(logDir))
{
    foreach (var f in Directory.GetFiles(logDir, "tchats-*.log"))
        File.Delete(f);
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.With<StackTraceEnricher>()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}{StackTrace}")
    .CreateLogger();

// ─── DI 容器 ───
var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.ClearProviders();
    builder.AddSerilog(dispose: true);
});

services.AddTChatServer(configuration);

var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILogger<ChatServerService>>();
var serverOptions = provider.GetRequiredService<ChatServerOptions>();

// ─── 优雅关闭 ───
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("收到关闭信号，正在停止服务...");
    cts.Cancel();
};

// ─── 启动 ───
var server = provider.GetRequiredService<ChatServerService>();

try
{
    await server.StartAsync(cts.Token);
    logger.LogInformation("TChatServer 已启动在 {Address}:{Port}",
        serverOptions.BindAddress, serverOptions.Port);
    logger.LogInformation("日志目录: {LogDir}", Path.GetFullPath(logDir));
    logger.LogInformation("按 Ctrl+C 停止服务");

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // 正常关闭流程
    }

    await server.StopAsync();
}
finally
{
    await server.DisposeAsync();
    logger.LogInformation("服务已完全关闭");
    await Log.CloseAndFlushAsync();
}
