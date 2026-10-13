using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Shadow.Core;

/// <summary>
/// 开机自启管理。
///
/// 为什么不用注册表 Run 键：本程序以 requireAdministrator 启动，
/// Run 键每次登录都会弹一次 UAC 确认框（用户会疯）。
/// 任务计划程序可以用「以最高权限运行」+ ONLOGON 触发器实现静默提权启动。
/// </summary>
internal static class AutoStartManager
{
    private const string TaskName = "Shadow.WindowPin";

    internal static string TaskDisplayName => TaskName;

    /// <summary>自启任务当前是否存在。</summary>
    internal static bool IsEnabled()
    {
        (int code, _, _) = Run("/Query", "/TN", TaskName);
        return code == 0;
    }

    /// <summary>创建/覆盖自启任务（需要管理员权限，本程序启动即提权故一定可行）。</summary>
    internal static bool Enable(out string message)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            message = "取不到自身可执行文件路径，无法设置自启";
            return false;
        }

        // 单文件发布时 ProcessPath 就是 exe 本身；dotnet Shadow.dll 方式运行会指向 dotnet.exe，
        // 此时用进程主模块路径兜底。
        if (!string.Equals(Path.GetFileNameWithoutExtension(exe), "Shadow", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string module = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (module.Length > 0) exe = module;
            }
            catch
            {
                // 忽略，用原值
            }
        }

        // --autostart：告诉程序「这是登录自启，别把主面板弹出来」。
        // 用户自己双击 exe（不带参数）时会弹面板，见 AppModeParser.IsAutostart。
        (int code, string stdout, string stderr) = Run(
            "/Create", "/TN", TaskName,
            "/TR", exe + " " + Shadow.App.AppModeParser.AutostartSwitch,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F");

        if (code == 0)
        {
            message = $"已设置开机自启（任务计划“{TaskName}”，以最高权限静默启动）";
            Log.Info($"自启任务已创建：{exe}");
            return true;
        }

        message = $"设置自启失败（schtasks 退出码 {code}）：{FirstLine(stdout + stderr)}";
        Log.Warn(message);
        return false;
    }

    internal static bool Disable(out string message)
    {
        (int code, string stdout, string stderr) = Run("/Delete", "/TN", TaskName, "/F");

        if (code == 0)
        {
            message = "已关闭开机自启";
            Log.Info("自启任务已删除");
            return true;
        }

        // 任务本来就不存在也算成功；但「拒绝访问」绝不能当成成功，
        // 否则界面会谎报「已关闭」而任务其实还在（用户重启后会发现它又起来了）。
        string output = FirstLine(stdout + stderr);
        if (LooksLikeAccessDenied(stdout + stderr))
        {
            message = $"关闭自启失败（拒绝访问）：{output}";
            Log.Warn(message);
            return false;
        }

        message = code == 1 ? "已关闭开机自启（任务原本不存在）" : $"关闭自启失败（退出码 {code}）：{output}";
        Log.Info(message);
        return code == 1;
    }

    /// <summary>
    /// 判断 schtasks 的失败是否属于「权限不足」。中文/英文系统都用文本匹配，
    /// 因为 schtasks 成功与「任务不存在」都用退出码 1，只能靠文本区分真正的原因。
    /// </summary>
    internal static bool LooksLikeAccessDenied(string text)
    {
        return text.Contains("拒绝访问", StringComparison.Ordinal)
            || text.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0
            || text.Contains("ERROR_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>任务计划实际的触发方式说明，用于「关于」里解释为什么不用注册表。</summary>
    internal static string Describe()
    {
        return IsEnabled()
            ? $"已开启（计划任务 {TaskName}，登录时以最高权限静默启动）"
            : "未开启";
    }

    /// <summary>
    /// schtasks 输出所用的控制台编码（中文系统 GBK/936）。
    /// .NET Core 默认不包含代码页 936，必须先注册 CodePagesEncodingProvider，
    /// 否则 Encoding.GetEncoding(936) 会直接抛异常 —— 取不到就退回 UTF-8，不能让诊断信息把功能带崩。
    /// </summary>
    private static Encoding ConsoleEncoding
    {
        get
        {
            if (_consoleEncoding is not null) return _consoleEncoding;

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                int codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                if (codePage <= 0) codePage = 936;
                _consoleEncoding = Encoding.GetEncoding(codePage);
            }
            catch
            {
                _consoleEncoding = Encoding.UTF8;
            }

            return _consoleEncoding;
        }
    }

    private static Encoding? _consoleEncoding;

    private static (int Code, string StdOut, string StdErr) Run(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // schtasks 是控制台程序，按系统 ANSI/OEM 代码页输出（中文系统是 GBK/936）。
                // 不指定编码时 .NET 按 UTF-8 解码，中文错误信息会变成乱码（“错误: 拒绝访问。” → ����: �ܾ����ʡ�），
                // 用户看到的就是一串问号，等于没有报错信息。
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding,
            };

            // ArgumentList 会自动处理带空格的路径与引号，避免手写命令行拼错
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            using Process? process = Process.Start(info);
            if (process is null) return (-1, string.Empty, "无法启动 schtasks.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            return (process.HasExited ? process.ExitCode : -2, stdout, stderr);
        }
        catch (Exception ex)
        {
            Log.Warn($"调用 schtasks 失败：{ex.GetType().Name} {ex.Message}");
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0].Trim() : "无输出";
    }
}
