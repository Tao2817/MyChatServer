using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TChatS.Core;
using TChatS.Protocol;
using TChatS.Storage;
using TChatS.Transport;

namespace TChatS.Service;

/// <summary>
/// DI 容器注册扩展方法。
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// 从 <see cref="IConfiguration"/> 的 "ChatServer" 节绑定配置并注册 TChatServer 所需的所有服务。
    /// </summary>
    public static IServiceCollection AddTChatServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new ChatServerOptions();
        configuration.GetSection("ChatServer").Bind(options);
        services.AddSingleton(options);

        services.AddTChatServerCore();
        return services;
    }

    /// <summary>
    /// 使用回调配置 ChatServerOptions 并注册所有服务。
    /// </summary>
    public static IServiceCollection AddTChatServer(
        this IServiceCollection services,
        Action<ChatServerOptions>? configureOptions = null)
    {
        var options = new ChatServerOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        services.AddTChatServerCore();
        return services;
    }

    /// <summary>
    /// 注册核心服务（不包含 ChatServerOptions，由调用方注入）。
    /// </summary>
    private static void AddTChatServerCore(this IServiceCollection services)
    {
        // 存储层
        services.AddSingleton<IUserRepository, InMemoryUserRepository>();

        // 业务层
        services.AddSingleton<AuthService>();
        services.AddSingleton<ChatRoomManager>();
        services.AddSingleton<MessageRouter>();

        // 传输层
        services.AddSingleton<ConnectionManager>();

        // 协议层 — 根据配置选择协议实现
        services.AddSingleton<IProtocolParser>(sp =>
        {
            var options = sp.GetRequiredService<ChatServerOptions>();
            return ResolveProtocol(options);
        });

        // 服务编排
        services.AddSingleton<ChatServerService>();
    }

    /// <summary>
    /// 根据 <see cref="ChatServerOptions.Protocol"/> 字段选择协议实现。
    /// </summary>
    private static IProtocolParser ResolveProtocol(ChatServerOptions options)
    {
        var protocol = options.Protocol?.Trim();

        if (string.Equals(protocol, "Modern", StringComparison.OrdinalIgnoreCase))
        {
            return new TcpTextProtocol();
        }

        // 默认 Legacy
        return new TcpTextProtocolLegacy();
    }
}
