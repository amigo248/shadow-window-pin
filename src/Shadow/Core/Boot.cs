namespace Shadow.Core;

/// <summary>
/// 启动期诊断信标。
///
/// 背景：WinExe 进程没有控制台，一旦在极早期（配置 WinForms、解析数据目录之前）
/// 出问题，就会「静默退出」，连日志文件都写不出来 —— 排查时完全无迹可循。
/// 所以 Boot 故意把信标写到 exe 同目录，这条路径在开发机上一定可写。
///
/// 所有模式都启用；正常启动成功后会调用 <see cref="Clear"/> 把它删掉，
/// 所以只有「启动途中死掉」才会在安装目录里留下 boot.log。
/// </summary>
internal static class Boot
{
    private static readonly object Gate = new();

    /// <summary>是否写诊断信标（由 Program 按启动模式设置）。</summary>
    internal static bool Enabled { get; set; } = true;

    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "boot.log");

    internal static void Trace(string message)
    {
        if (!Enabled) return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 信标本身绝不允许影响启动
        }
    }

    /// <summary>启动成功后抹掉信标，避免它常驻 exe 目录。</summary>
    internal static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
        }
        catch
        {
            // 删不掉就留着，无害
        }
    }
}
