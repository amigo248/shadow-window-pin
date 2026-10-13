using System.Runtime.InteropServices;

namespace Shadow.Core;

internal enum FullscreenVerdict
{
    /// <summary>取不到显示模式信息（多显卡驱动/远程桌面等场景），不下结论。</summary>
    Unknown,

    /// <summary>显示模式与系统设置一致，正常桌面。</summary>
    Windowed,

    /// <summary>显示模式被改过（宽高与系统设置不同），极可能是独占全屏程序在跑。</summary>
    ExclusiveFullscreen,
}

/// <summary>
/// 独占全屏（真全屏）判定。
///
/// 为什么用「显示模式是否被改过」这个判据：独占全屏（IDXGISwapChain::SetFullscreenState(TRUE)）
/// 会绕过 DWM 直接把显示模式切到游戏自己的模式上，此时桌面窗口的 topmost 对它无效 ——
/// 这是真正的物理限制，不是我们实现得不好。而这类程序往往连一个「铺满屏幕的窗口」都没有
/// （画面直接由 GPU 扫描输出），所以「窗口是否覆盖整个显示器」是没用的判据。
/// 显示模式与系统设置不一致则是可靠得多的信号。
///
/// 所有失败路径都必须写清楚原因：判定不出来时用户看到一句空白，比看到「读不到显示模式」
/// 更难排查 —— 而这类失败在多显卡/远程桌面/虚拟显示器上是真实存在的。
/// </summary>
internal static class FullscreenDetector
{
    /// <summary>针对某个窗口所在显示器判断是否处于独占全屏。</summary>
    internal static FullscreenVerdict DetectForWindow(IntPtr hwnd, out string detail)
    {
        detail = string.Empty;

        if (hwnd == IntPtr.Zero)
        {
            detail = "没有窗口句柄";
            return FullscreenVerdict.Unknown;
        }

        IntPtr monitor;
        try
        {
            monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }
        catch (Exception ex)
        {
            detail = $"调 MonitorFromWindow 抛异常：{ex.GetType().Name} {ex.Message}";
            Log.Warn(detail);
            return FullscreenVerdict.Unknown;
        }

        if (monitor == IntPtr.Zero)
        {
            detail = $"取不到窗口所在显示器（MonitorFromWindow 返回 0，err={Marshal.GetLastWin32Error()}）";
            return FullscreenVerdict.Unknown;
        }

        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            szDevice = string.Empty,
        };

        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            detail = $"取显示器信息失败（cbSize={info.cbSize}，err={Marshal.GetLastWin32Error()}）";
            return FullscreenVerdict.Unknown;
        }

        string device = info.szDevice ?? string.Empty;

        if (!TryReadMode(device, NativeMethods.ENUM_CURRENT_SETTINGS,
                out uint width, out uint height, out uint hz, out string currentReason))
        {
            detail = $"读不到当前显示模式（设备=\"{device}\"）：{currentReason}";
            return FullscreenVerdict.Unknown;
        }

        if (!TryReadMode(device, NativeMethods.ENUM_REGISTRY_SETTINGS,
                out uint regWidth, out uint regHeight, out uint regHz, out string registryReason))
        {
            detail = $"读不到系统设置的显示模式（设备=\"{device}\"）：{registryReason}";
            return FullscreenVerdict.Unknown;
        }

        detail = $"当前 {width}x{height}@{hz}Hz，系统设置 {regWidth}x{regHeight}@{regHz}Hz（{device}）";

        // 只比较宽高：部分驱动会把刷新率报告成 1 或 0，拿它判定会误伤
        return (width != regWidth || height != regHeight)
            ? FullscreenVerdict.ExclusiveFullscreen
            : FullscreenVerdict.Windowed;
    }

    /// <summary>
    /// 窗口是否铺满它所在显示器的整个区域（含边框）。用于「无边框全屏」这类伪全屏的提示：
    /// 它们能置顶，但视觉上与独占全屏很像，容易让用户以为置顶失败。
    /// </summary>
    internal static bool CoversWholeMonitor(IntPtr hwnd, out string detail)
    {
        detail = string.Empty;
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;
            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect)) return false;

            NativeMethods.RECT screen = info.rcMonitor;
            bool covers = rect.Left <= screen.Left && rect.Top <= screen.Top
                          && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;

            if (covers)
            {
                detail = $"窗口 {rect.Width}x{rect.Height} 覆盖显示器 {screen.Width}x{screen.Height}";
            }

            return covers;
        }
        catch (Exception ex)
        {
            Log.Warn($"判断窗口是否铺满显示器失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static bool TryReadMode(
        string device, uint mode,
        out uint width, out uint height, out uint hz, out string reason)
    {
        width = height = hz = 0;
        reason = string.Empty;

        try
        {
            var devMode = new NativeMethods.DEVMODE
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>(),
            };

            if (!NativeMethods.EnumDisplaySettingsW(device, mode, ref devMode))
            {
                reason = $"EnumDisplaySettingsW 返回失败（err={Marshal.GetLastWin32Error()}，" +
                         $"dmSize={devMode.dmSize}）";
                return false;
            }

            // 结构体布局一旦对不上，读到的就是垃圾值 —— 先用合理范围兜底，宁可返回 Unknown 也不误报
            if (devMode.dmPelsWidth is < 320 or > 32768)
            {
                reason = $"宽度不合理（{devMode.dmPelsWidth}），疑似结构体布局错位";
                return false;
            }

            if (devMode.dmPelsHeight is < 200 or > 32768)
            {
                reason = $"高度不合理（{devMode.dmPelsHeight}），疑似结构体布局错位";
                return false;
            }

            width = devMode.dmPelsWidth;
            height = devMode.dmPelsHeight;
            hz = devMode.dmDisplayFrequency is > 0 and < 1000 ? devMode.dmDisplayFrequency : 0;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name} {ex.Message}";
            Log.Warn($"读取显示模式失败（{mode}）：{reason}");
            return false;
        }
    }
}
