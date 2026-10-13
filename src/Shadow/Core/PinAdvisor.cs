namespace Shadow.Core;

/// <summary>一个窗口「能不能置顶」以及「置顶之后要注意什么」的结论。</summary>
internal sealed record PinAdvice(bool CanPin, string Message, string? Warning)
{
    internal bool HasWarning => !string.IsNullOrEmpty(Warning);
}

/// <summary>
/// 把「可置顶性 + 反作弊 + 无重定向位图 + 独占全屏」这些零散判据收口成一处，
/// 让拾取器、主面板、托盘提示说的是同一套话，不会出现三处口径不一致。
/// </summary>
internal static class PinAdvisor
{
    /// <summary>在动手之前评估（不做任何窗口状态修改）。</summary>
    internal static PinAdvice Evaluate(WindowInfo info)
    {
        if (!info.IsValid) return new PinAdvice(false, "窗口已不存在", null);

        if (!WindowEnumerator.IsPinnable(info, out string reason))
        {
            return new PinAdvice(false, reason, null);
        }

        var warnings = new List<string>();

        if (AntiCheatGuard.IsTargetProcess(info.ProcessName))
        {
            warnings.Add($"「{info.ProcessName}」是反作弊/保护组件，操作它没有任何意义，还可能被记录");
        }
        else if (AntiCheatGuard.Describe() is string running)
        {
            warnings.Add($"本机正在运行反作弊组件（{running}）：本工具不注入、不安插全局钩子，风险很低但非零");
        }

        if (info.UsesDirectComposition)
        {
            warnings.Add("该窗口不走 DWM 重定向位图（UWP / DirectComposition），置顶可能被系统忽略");
        }

        FullscreenVerdict verdict = FullscreenDetector.DetectForWindow(info.Handle, out string modeDetail);
        if (verdict == FullscreenVerdict.ExclusiveFullscreen)
        {
            warnings.Add($"检测到独占全屏（{modeDetail}），topmost 层带对它无效，请先切到窗口化/无边框");
        }
        else if (verdict == FullscreenVerdict.Windowed && FullscreenDetector.CoversWholeMonitor(info.Handle, out _))
        {
            warnings.Add("该窗口铺满整个显示器（无边框全屏）：能置顶，但上层窗口盖上去时它可能掉帧或暂停渲染");
        }

        return new PinAdvice(true, "可以置顶", warnings.Count == 0 ? null : string.Join("；", warnings));
    }

    /// <summary>
    /// 已经调过 SetWindowPos、但回读发现 topmost 位没生效时，
    /// 给出可能的具体原因 —— 「失败了」这三个字对排查毫无帮助。
    /// </summary>
    internal static string ExplainFailedReadback(WindowInfo info)
    {
        var reasons = new List<string>();

        if (FullscreenDetector.DetectForWindow(info.Handle, out string modeDetail)
            == FullscreenVerdict.ExclusiveFullscreen)
        {
            reasons.Add($"显示器处于独占全屏（{modeDetail}），Windows 的 topmost 对独占全屏程序无效");
        }

        if (NativeMethods.GetForegroundWindow() != info.Handle)
        {
            reasons.Add(
                "目标窗口不在前台，而 Windows 只允许「所属进程拥有 SetForegroundWindow 权限」的窗口被置顶" +
                "（官方文档 SetWindowPos 明写这条前提）：本工具已尝试把它切到前台仍未成功，" +
                "请先点一下该窗口让它成为前台窗口再重试");
        }

        if (info.UsesDirectComposition)
        {
            reasons.Add("该窗口使用 DirectComposition/翻转链，不接受 topmost 层带");
        }

        if (AntiCheatGuard.IsTargetProcess(info.ProcessName) || AntiCheatGuard.AnyRunning())
        {
            reasons.Add("本机有反作弊/保护组件在运行，可能拦截了对该窗口的操作");
        }

        if (reasons.Count == 0)
        {
            // 实测结论（2026-10-06，Windows 11 26200）：系统自带的「记事本」
            // （C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_*）会静默忽略置顶请求 ——
            // SetWindowPos(HWND_TOPMOST) 返回 True，但 WS_EX_TOPMOST 位始终不置位，
            // 即使完全不经过本工具、由别的进程直接调用也一样。它的 ExStyle 没有任何可疑位
            // （既非 DirectComposition，也非受保护进程），所以无法在动手之前预判，只能事后如实说明。
            reasons.Add("该应用可能主动忽略置顶请求（实测 Windows 11 新版记事本就是这样：系统返回成功，但置顶位不生效）");
            reasons.Add("另外也可能属于受保护进程（SYSTEM/PPL，如部分反作弊、Defender），或由比本工具更高权限的进程创建");
        }

        return string.Join("；", reasons);
    }
}
