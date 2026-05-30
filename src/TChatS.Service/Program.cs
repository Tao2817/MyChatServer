using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TChatS.Service;

// ─── 加载配置 ───
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// ─── DI 容器 ───
var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddConfiguration(configuration.GetSection("Logging"));
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});

services.AddTChatServer(configuration);

var provider = services.BuildServiceProvider();
var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger("TChatServer");

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
}
