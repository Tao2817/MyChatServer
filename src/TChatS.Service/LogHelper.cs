using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace TChatS.Service;

/// <summary>
/// 日志辅助方法，自动附加 [CallerFilePath]:[CallerLineNumber] 信息。
/// 调用方用 $"" 字符串插值传递参数，file/line 由编译器自动填充。
/// </summary>
internal static class LogHelper
{
    public static void Info(ILogger logger, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        logger.LogInformation("[{File}:{Line}] {Message}", Path.GetFileName(file), line, message);
    }

    public static void Warn(ILogger logger, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        logger.LogWarning("[{File}:{Line}] {Message}", Path.GetFileName(file), line, message);
    }

    public static void Warn(ILogger logger, Exception ex, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        logger.LogWarning(ex, "[{File}:{Line}] {Message}", Path.GetFileName(file), line, message);
    }

    public static void Error(ILogger logger, Exception ex, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        logger.LogError(ex, "[{File}:{Line}] {Message}", Path.GetFileName(file), line, message);
    }

    public static void Error(ILogger logger, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        logger.LogError("[{File}:{Line}] {Message}", Path.GetFileName(file), line, message);
    }

    public static void Debug(ILogger logger, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        logger.LogDebug("[{File}:{Line}] {Message}", Path.GetFileName(file), line, message);
    }
}
