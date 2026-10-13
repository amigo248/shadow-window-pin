namespace Shadow.Core;

/// <summary>
/// 窗口位置/大小的最小操作集合。
///
/// 单独成类是为了守住一条约定：本项目只通过 SetWindowPos 改窗口，
/// 而且**永远带 SWP_NOACTIVATE**（改位置不能把焦点抢走）。
/// 用 WinForms 的 <c>Control.Bounds</c> 走不到这里 —— 那是给自家窗口用的。
/// </summary>
internal static class WindowGeometry
{
    /// <summary>移动并缩放窗口，全程不激活它。</summary>
    internal static bool MoveResize(IntPtr hwnd, int left, int top, int width, int height)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

        return NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, left, top, Math.Max(1, width), Math.Max(1, height),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>读窗口的屏幕矩形。</summary>
    internal static bool TryRead(IntPtr hwnd, out NativeMethods.RECT rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        return NativeMethods.GetWindowRect(hwnd, out rect);
    }

    /// <summary>窗口是否处于最小化（最小化窗口的 GetWindowRect 给出的是 -32000 这种假坐标）。</summary>
    internal static bool IsMinimized(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd) && NativeMethods.IsIconic(hwnd);
}
