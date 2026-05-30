namespace TChatS.Core.Models;

/// <summary>
/// 消息路由产生的输出动作。Core 层不直接执行 I/O，
/// 而是返回动作列表给 Service 层执行。
/// </summary>
public abstract record OutgoingAction
{
    /// <summary>向指定连接发送消息</summary>
    public sealed record Send(long ConnectionId, string Content) : OutgoingAction;

    /// <summary>向指定聊天室广播消息，排除某个连接</summary>
    public sealed record BroadcastToChat(string ChatId, string Content, long ExcludeConnectionId) : OutgoingAction;

    /// <summary>断开指定连接</summary>
    public sealed record Disconnect(long ConnectionId) : OutgoingAction;
}

/// <summary>
/// 路由结果，包含一组待执行的输出动作。
/// </summary>
/// <param name="Actions">输出动作列表，按顺序执行</param>
public record RouteResult(IReadOnlyList<OutgoingAction> Actions)
{
    public static readonly RouteResult Empty = new(Array.Empty<OutgoingAction>());
}
