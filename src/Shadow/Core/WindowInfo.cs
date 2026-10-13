using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Shadow.Core;

/// <summary>
/// 一个顶层窗口的只读快照。
///
/// 注意：这是「某一时刻」的状态快照，HWND 会被系统回收复用，
/// 所以不要把 WindowInfo 长期当作身份凭据使用（M2 起改用 exe+类名+标题 的匹配规则）。
/// </summary>
internal sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ClassName,
    uint ProcessId,
    string ProcessName,
    bool IsTopmost,
    bool IsVisible,
    bool IsCloaked,
    bool IsOwnProcess,
    int Width,
    int Height)
{
    internal static readonly WindowInfo Invalid = new(
        IntPtr.Zero, string.Empty, string.Empty, 0u, string.Empty,
        false, false, false, false, 0, 0);

    internal bool IsValid => Handle != IntPtr.Zero;

    internal string DisplayName => string.IsNullOrWhiteSpace(Title) ? $"<{ClassName}>" : Title;

    internal string ShortDescription =>
        $"\"{DisplayName}\" [{ClassName}] {ProcessName}({ProcessId}) hwnd=0x{Handle.ToInt64():X}";

    /// <summary>
    /// 带 WS_EX_NOREDIRECTIONBITMAP 的窗口不走 DWM 重定向位图（UWP / DirectComposition / 部分翻转链）。
    /// 这类窗口「设置 topmost 成功但表现为没生效」的概率明显更高（MPO/独立翻转会把它盖在我们的层之上），
    /// 所以只作为风险提示，不作为「不可置顶」的硬判据。
    /// </summary>
    internal bool UsesDirectComposition =>
        (NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE).ToInt64()
         & NativeMethods.WS_EX_NOREDIRECTIONBITMAP) != 0;

    /// <summary>
    /// 工具窗口：不该出现在拾取候选里的那种（启动器的隐藏挂件、输入法候选、托盘辅助窗）。
    /// 它们通常是 WS_EX_TOOLWINDOW，不进 Alt-Tab，也不该被当成「同进程的主要窗口」。
    /// </summary>
    internal bool IsToolWindow =>
        (NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE).ToInt64()
         & NativeMethods.WS_EX_TOOLWINDOW) != 0;

    /// <summary>抓取窗口当前状态。</summary>
    internal static WindowInfo Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return Invalid;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect);

        return new WindowInfo(
            Handle: hwnd,
            Title: GetText(hwnd),
            ClassName: GetClass(hwnd),
            ProcessId: pid,
            ProcessName: GetProcessName(pid),
            IsTopmost: IsWindowTopmost(hwnd),
            IsVisible: NativeMethods.IsWindowVisible(hwnd),
            IsCloaked: IsWindowCloaked(hwnd),
            IsOwnProcess: pid != 0 && pid == (uint)Environment.ProcessId,
            Width: rect.Width,
            Height: rect.Height);
    }

    /// <summary>
    /// 用扩展样式位读真实的置顶状态。
    /// 这是唯一可信的判据 —— SetWindowPos 的返回值在 UIPI / 独占全屏场景下会说谎。
    /// </summary>
    internal static bool IsWindowTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
    }

    /// <summary>窗口的扩展样式（诊断与自检打印用）。</summary>
    internal static long ExStyleOf(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? 0 : NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

    /// <summary>DWM 隐藏（cloaked）的窗口：UWP 幽灵窗口、已关闭但句柄残留的窗口等。</summary>
    internal static bool IsWindowCloaked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int hr = NativeMethods.DwmGetWindowAttribute(
                hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    internal static string GetText(IntPtr hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static string GetClass(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static string GetProcessName(uint pid)
    {
        if (pid == 0) return string.Empty;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // 进程已退出
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
