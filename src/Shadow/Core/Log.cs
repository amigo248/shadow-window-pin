using System.Text;

namespace Shadow.Core;

/// <summary>极简日志。日志失败绝不能影响主流程，所以全部吞掉异常。</summary>
internal static class Log
{
    private static readonly object Gate = new();

    internal static string FilePath => ShadowPaths.LogFilePath;

    internal static void Info(string message) => Write("INFO ", message, null);

    internal static void Warn(string message) => Write("WARN ", message, null);

    internal static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                   .Append(" [").Append(level).Append("] ")
                   .Append(message);

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            lock (Gate)
            {
                File.AppendAllText(FilePath, builder.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
