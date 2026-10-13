using System.Diagnostics;

namespace Shadow.Core;

/// <summary>
/// 反作弊保护识别（纯信息提示，不做任何绕过尝试）。
///
/// 为什么只提示不拦截：纯 SetWindowPos + WINEVENT_OUTOFCONTEXT 不注入、不挂钩，
/// 被反作弊判定为异常的概率很低，但确实不是零。用户的游戏账号比一个置顶功能值钱得多，
/// 所以界面必须明确告诉他「这个窗口背后有反作弊，出了事别怪工具」，把选择权交回给用户。
/// </summary>
internal static class AntiCheatGuard
{
    /// <summary>已知的反作弊 / 保护组件进程名（不含 .exe，比较时忽略大小写）。</summary>
    private static readonly string[] KnownGuards =
    {
        // Easy Anti-Cheat
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Setup", "EasyAntiCheat_Service",
        // BattlEye
        "BEService", "BEService_x64", "BEDaisy", "BEClient",
        // Riot Vanguard
        "vgc", "vgtray", "vgk",
        // XIGNCODE3 / Wellbia
        "XignCode3", "xhunter1", "XignCode",
        // nProtect GameGuard
        "GameMon", "GameGuard", "npggNT", "nProtect", "npsvc",
        // 腾讯 / 网易 / 米哈游系
        "TenProtect", "TP3Helper", "ACE-Guard Client", "ACE-Tray", "AntiCheatExpert",
        "SGuard", "SGuardSvc", "SGuard64", "SGuardSvc64",
        "mhyprot", "mhyprot2", "mhyprot3",
        // 其他常见保护壳
        "GameShield", "unians", "Protect",
    };

    private static readonly object Gate = new();
    private static DateTime _cachedAt = DateTime.MinValue;
    private static List<string> _cached = new();

    /// <summary>该进程名本身是不是反作弊/保护组件。</summary>
    internal static bool IsTargetProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        string name = Path.GetFileNameWithoutExtension(processName);
        return KnownGuards.Any(guard => string.Equals(guard, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 当前系统里正在运行的反作弊组件（用于在拾取任意窗口时提醒「本机有反作弊在跑」）。
    /// 枚举进程开销不小，而拾取器每 30 毫秒问一次，所以缓存 5 秒。
    /// </summary>
    internal static List<string> RunningGuards()
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _cachedAt) < TimeSpan.FromSeconds(5)) return _cached;

            var found = new List<string>();
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try
                    {
                        if (IsTargetProcess(process.ProcessName)) found.Add(process.ProcessName);
                    }
                    catch
                    {
                        // 单个进程查不到名字（权限/已退出）就跳过
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"枚举反作弊进程失败：{ex.GetType().Name} {ex.Message}");
            }

            _cached = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    internal static bool AnyRunning() => RunningGuards().Count > 0;

    /// <summary>给界面用的一句话说明；没有发现时返回 null。</summary>
    internal static string? Describe()
    {
        List<string> guards = RunningGuards();
        return guards.Count == 0 ? null : string.Join("、", guards.Take(3));
    }
}
