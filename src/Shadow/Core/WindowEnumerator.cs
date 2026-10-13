namespace Shadow.Core;

/// <summary>窗口枚举与「可置顶性」判定。</summary>
internal static class WindowEnumerator
{
    /// <summary>系统外壳窗口类名：置顶它们没有意义，且会干扰开始菜单 / 任务栏。</summary>
    private static readonly HashSet<string> BlockedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "SysShadow",
        "ForegroundStaging",
        "TaskListThumbnailWnd",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "Windows.Internal.Shell.TabProxyWindow",
        "EdgeUiInputTopWndClass",
        "EdgeUiInputWndClass",
        "TopLevelWindowForOverflowXamlIsland",
    };

    /// <summary>系统外壳 / 桌面合成相关进程。</summary>
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "ShellExperienceHost",
        "TextInputHost",
        "LockApp",
        "dwm",
        "csrss",
        "winlogon",
        "LogonUI",
    };

    /// <summary>枚举全部顶层窗口句柄（不做过滤）。</summary>
    internal static IReadOnlyList<IntPtr> EnumerateTopLevel()
    {
        var handles = new List<IntPtr>(256);
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    /// <summary>枚举所有可以作为置顶目标的窗口。</summary>
    internal static IReadOnlyList<WindowInfo> EnumeratePinnable()
    {
        var result = new List<WindowInfo>();
        foreach (IntPtr hwnd in EnumerateTopLevel())
        {
            WindowInfo info = WindowInfo.Describe(hwnd);
            if (IsPinnable(info, out _)) result.Add(info);
        }
        return result;
    }

    /// <summary>判定一个窗口是否适合作为置顶目标。</summary>
    internal static bool IsPinnable(WindowInfo info, out string reason)
    {
        if (!info.IsValid) { reason = "无效窗口句柄"; return false; }
        if (info.IsOwnProcess) { reason = "该窗口属于 Shadow 自身"; return false; }
        if (!info.IsVisible) { reason = "窗口当前不可见"; return false; }
        if (info.IsCloaked) { reason = "窗口被 DWM 隐藏（UWP 幽灵窗口）"; return false; }
        if (info.Width <= 0 || info.Height <= 0) { reason = "窗口面积为 0"; return false; }
        if (BlockedClasses.Contains(info.ClassName)) { reason = $"系统外壳窗口（{info.ClassName}）"; return false; }
        if (BlockedProcesses.Contains(info.ProcessName)) { reason = $"系统外壳进程（{info.ProcessName}）"; return false; }

        // 用户的排除规则是硬约束：所有入口（拾取器、热键、主面板、CLI）都走这里，
        // 规则引擎自己扫描时也要问 —— 但 ApplyPinRules 是先求值再 TryPin，不会在这里被绕开。
        if (RuleEngine.IsExcluded(info))
        {
            reason = RuleEngine.DescribeExclusion(info);
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 取鼠标位置下窗口所属的顶层窗口。
    /// WindowFromPoint 返回的是最深层子窗口，必须用 GetAncestor(GA_ROOT) 上溯，
    /// 否则我们会去置顶一个子控件。
    /// </summary>
    internal static IntPtr GetRootWindowUnderCursor()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT point)) return IntPtr.Zero;

        IntPtr hwnd = NativeMethods.WindowFromPoint(point);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        return root != IntPtr.Zero ? root : hwnd;
    }

    /// <summary>
    /// 读取真实 Z 序：返回 hwnd 之上紧邻的那个窗口。
    /// 返回 IntPtr.Zero 表示它已经在最顶端。M2 的层级压栈会依赖这个函数。
    /// </summary>
    internal static IntPtr GetWindowAbove(IntPtr hwnd) =>
        NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV);

    /// <summary>
    /// 按「进程名 + 窗口类 + 标题」找回窗口。
    ///
    /// 用途一：崩溃后修复残留的 topmost 状态（上次置顶的窗口现在还钉在桌面上）。
    /// 用途二：HWND 不是长期有效的标识，窗口重启后句柄会变，只能靠这三元组重新对上。
    /// 用全量枚举而不是 EnumeratePinnable：待修复的窗口可能已经不满足「可置顶」判据了。
    /// </summary>
    internal static IntPtr FindBySignature(string processName, string className, string title)
    {
        if (className.Length == 0 && title.Length == 0) return IntPtr.Zero;

        foreach (IntPtr hwnd in EnumerateTopLevel())
        {
            WindowInfo info = WindowInfo.Describe(hwnd);
            if (!info.IsValid) continue;

            if (string.Equals(info.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(info.ClassName, className, StringComparison.Ordinal) &&
                string.Equals(info.Title, title, StringComparison.Ordinal))
            {
                return info.Handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 找「和 hwnd 属于同一进程」的其他顶层窗口，用于拾取器的同进程切换（Tab 键）。
    ///
    /// 为什么需要它：启动器 + 主窗口 + 报错框 + WebView 常常同属一个进程，
    /// 鼠标下抓到的那个往往不是用户想要的那个。
    ///
    /// 排序：hwnd 自己排第 0 位（保证「当前窗口」是切换序列的起点），
    /// 其余按面积从大到小 —— 大窗口才像主窗口，小的多是挂件或提示框。
    /// </summary>
    internal static List<WindowInfo> SiblingsOf(IntPtr hwnd)
    {
        var result = new List<WindowInfo>();
        if (hwnd == IntPtr.Zero) return result;

        WindowInfo self = WindowInfo.Describe(hwnd);
        if (!self.IsValid) return result;

        foreach (IntPtr candidate in EnumerateTopLevel())
        {
            if (candidate == hwnd) continue;

            WindowInfo info = WindowInfo.Describe(candidate);
            if (!info.IsValid) continue;
            if (info.ProcessId != self.ProcessId) continue;
            if (info.IsOwnProcess) continue;
            if (!info.IsVisible || info.IsCloaked) continue;
            if (info.Width <= 0 || info.Height <= 0) continue;
            if (info.IsToolWindow) continue;

            result.Add(info);
        }

        result.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));
        result.Insert(0, self);
        return result;
    }
}
