namespace Shadow.Core;

/// <summary>Shadow 的数据目录。M3 起配置文件也会落在这里。</summary>
internal static class ShadowPaths
{
    internal const string ProductName = "Shadow";

    private static string? _dataDirectory;

    /// <summary>
    /// 实际可用的数据目录：优先 %APPDATA%\Shadow\，不可写时退到 exe 同目录（便携模式）。
    ///
    /// 为什么是「实测」而不是「直接假设 %APPDATA% 能写」：
    /// 置顶工具经常运行在受限账户、只读安装目录、被安全软件托管的目录下，
    /// 而「日志/配置写不进去」属于最难排查的一类故障 —— 必须当场探测并留痕。
    /// </summary>
    internal static string DataDirectory
    {
        get
        {
            _dataDirectory ??= ResolveDataDirectory();
            return _dataDirectory;
        }
    }

    internal static string LogFilePath => Path.Combine(DataDirectory, "shadow.log");

    internal static string SelfTestLogPath => Path.Combine(DataDirectory, "selftest.log");

    /// <summary>数据目录是否落在 exe 同目录（便携模式）。</summary>
    internal static bool IsPortable => _dataDirectory is not null &&
                                       string.Equals(
                                           _dataDirectory,
                                           Path.Combine(AppContext.BaseDirectory, ProductName),
                                           StringComparison.OrdinalIgnoreCase);

    private static string ResolveDataDirectory()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDataCandidate = string.IsNullOrWhiteSpace(roaming)
            ? string.Empty
            : Path.Combine(roaming, ProductName);
        string exeCandidate = Path.Combine(AppContext.BaseDirectory, ProductName);

        foreach (string candidate in new[] { appDataCandidate, exeCandidate })
        {
            if (candidate.Length == 0) continue;

            if (TryPrepareWritable(candidate, out string reason))
            {
                Boot.Trace($"数据目录选定：{candidate}");
                return candidate;
            }

            Boot.Trace($"数据目录不可用：{candidate} → {reason}");
        }

        // 两个候选都写不了：退回 exe 目录尽力而为，至少不因为日志而崩溃
        Boot.Trace("没有可写的数据目录，退回 exe 同目录尽力而为");
        return exeCandidate;
    }

    /// <summary>真的写一个探针文件再删掉，确保「能建目录」不等于「能写文件」。</summary>
    private static bool TryPrepareWritable(string directory, out string reason)
    {
        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, "writable.probe");
            File.WriteAllText(probe, "shadow");
            File.Delete(probe);

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }
}
