namespace TChatS.StressTest;

/// <summary>
/// 压力测试配置选项，通过命令行参数解析。
/// </summary>
public sealed class StressTestOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10035;
    public int Connections { get; set; } = 100;
    public int DurationSeconds { get; set; } = 60;
    public int RampUpSeconds { get; set; } = 10;
    public int MessagesPerSec { get; set; } = 1;
    public int MessageSize { get; set; } = 64;
    public string Scenario { get; set; } = "sustained";
    public int ChatRooms { get; set; } = 5;

    public static StressTestOptions Parse(string[] args)
    {
        var opts = new StressTestOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var next = i + 1 < args.Length ? args[i + 1] : null;

            switch (arg)
            {
                case "--host": opts.Host = next!; i++; break;
                case "--port": opts.Port = int.Parse(next!); i++; break;
                case "--connections": opts.Connections = int.Parse(next!); i++; break;
                case "--duration": opts.DurationSeconds = int.Parse(next!); i++; break;
                case "--ramp-up": opts.RampUpSeconds = int.Parse(next!); i++; break;
                case "--messages-per-sec": opts.MessagesPerSec = int.Parse(next!); i++; break;
                case "--message-size": opts.MessageSize = int.Parse(next!); i++; break;
                case "--scenario": opts.Scenario = next!; i++; break;
                case "--chat-rooms": opts.ChatRooms = int.Parse(next!); i++; break;
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return opts;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            TChatS 压力测试工具

            用法: dotnet run -- [选项]

            选项:
              --host <ip>              服务器地址 (默认: 127.0.0.1)
              --port <port>            服务器端口 (默认: 10035)
              --connections <n>        并发连接数 (默认: 100)
              --duration <sec>         测试持续时间秒数 (默认: 60)
              --ramp-up <sec>          连接爬坡时间秒数 (默认: 10)
              --messages-per-sec <n>   每连接每秒消息数 (默认: 1)
              --message-size <n>       每条消息字节数 (默认: 64)
              --scenario <name>        测试场景 (默认: sustained)
                                         connection-flood | login-storm
                                         chat-throughput | sustained
              --chat-rooms <n>         聊天室数量 (默认: 5)
              --help                   显示此帮助

            示例:
              dotnet run -- --scenario login-storm --connections 100 --duration 30
              dotnet run -- --scenario chat-throughput --connections 500 --duration 120
              dotnet run -- --scenario connection-flood --connections 1200
            """);
    }

    public void PrintSummary()
    {
        Console.WriteLine($"场景: {Scenario}");
        Console.WriteLine($"目标: {Host}:{Port}");
        Console.WriteLine($"连接数: {Connections} | 持续时间: {DurationSeconds}s | 爬坡: {RampUpSeconds}s");
        Console.WriteLine($"消息速率: {MessagesPerSec}/s/连接 | 消息大小: {MessageSize}B | 聊天室: {ChatRooms}");
        Console.WriteLine();
    }
}
