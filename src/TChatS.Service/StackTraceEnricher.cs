using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace TChatS.Service;

/// <summary>
/// 当日志级别 ≥ Warning 时，自动将调用堆栈附加到 <c>StackTrace</c> 属性。
/// 只保留项目自身 (TChatS.*) 的帧，跳过框架和日志库的内部帧。
/// </summary>
public sealed class StackTraceEnricher : ILogEventEnricher
{
    /// <summary>
    /// 触发堆栈捕获的最低级别，默认 <see cref="LogEventLevel.Warning"/>。
    /// </summary>
    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Error;

    /// <summary>
    /// 在堆栈中仅保留以此前缀开头的命名空间中的帧。
    /// </summary>
    public string NamespacePrefix { get; init; } = "TChatS";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Level < MinimumLevel)
            return;

        var trace = new StackTrace(fNeedFileInfo: true);
        var frames = trace.GetFrames();
        if (frames == null || frames.Length == 0)
            return;

        // 仅保留项目自身代码的帧
        var filtered = frames
            .Select(f => new { Frame = f, Method = f.GetMethod() })
            .Where(f => f.Method?.DeclaringType is Type declaringType)
            .Where(f =>
            {
                // var ns = f.Method!.DeclaringType!.Namespace ?? "";
                // // 跳过 Serilog / Microsoft / System 等框架帧
                // if (ns.StartsWith("Serilog", StringComparison.Ordinal)) return false;
                // if (ns.StartsWith("Microsoft", StringComparison.Ordinal)) return false;
                // if (ns.StartsWith("System", StringComparison.Ordinal)) return false;
                // // 跳过 enricher 自身
                // if (f.Method.DeclaringType == typeof(StackTraceEnricher)) return false;
                return true;
            })
            .Select(f => new StackTrace(f.Frame).ToString().Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (filtered.Count == 0)
            return;

        var stackTrace = string.Join(Environment.NewLine, filtered);
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("StackTrace", stackTrace));
    }
}
