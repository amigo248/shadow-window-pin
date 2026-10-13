using System.Runtime.InteropServices;

namespace Shadow.Core;

/// <summary>
/// 窗口外观：透明度与点击穿透。
///
/// 靠的是 Win32 的分层窗口：<c>WS_EX_LAYERED</c> + <c>SetLayeredWindowAttributes(LWA_ALPHA)</c>。
/// 这条路不注入、不挂钩、不改进程内存，代价是它有明确的适用边界：
///  - 走 DWM 重定向（普通窗口、无边框窗口化的游戏）→ 有效；
///  - 走 DirectComposition / 独立翻转（<c>WS_EX_NOREDIRECTIONBITMAP</c>，很多 UWP、部分新式应用）
///    → 透明度可能被直接忽略；真独占全屏更不可能有效。
/// 这类降级无法用 API 读出来（窗口不会告诉你它是不是自己在合成），所以只能给出提示、
/// 让用户看到「设置成功了但没变化」时知道原因，而不是以为工具坏了。
/// </summary>
internal static class WindowAppearance
{
    internal const int MinOpacity = 10;
    internal const int MaxOpacity = 100;
    internal const int DefaultOpacity = 100;

    /// <summary>
    /// 真的调用过多少次 <c>SetLayeredWindowAttributes</c>（自检用：把透明度设成同一个值时不该增长）。
    /// </summary>
    internal static int LayeredWriteCount { get; private set; }

    internal static bool IsLayered(IntPtr hwnd) =>
        (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64()
         & NativeMethods.WS_EX_LAYERED) != 0;

    internal static bool IsClickThrough(IntPtr hwnd) =>
        (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64()
         & NativeMethods.WS_EX_TRANSPARENT) != 0;

    /// <summary>读回当前透明度；拿不到时返回 false（此时不能断言「已经设成 80%」）。</summary>
    internal static bool TryGetAlpha(IntPtr hwnd, out byte alpha)
    {
        alpha = 255;
        if (!NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out byte value, out uint flags)) return false;

        // 没带 LWA_ALPHA 的窗口（只用 colorkey 挖洞）其 alpha 无意义，视为不透明
        if ((flags & NativeMethods.LWA_ALPHA) == 0) return false;

        alpha = value;
        return true;
    }

    /// <summary>把窗口样式记为「我们改过之前的样子」，用于取消置顶时还原。</summary>
    internal static WindowStyleSnapshot Capture(IntPtr hwnd)
    {
        var snapshot = new WindowStyleSnapshot
        {
            Captured = true,
            ExStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64(),
            Alpha = 255,
            AlphaKnown = false,
        };

        if (TryGetAlpha(hwnd, out byte alpha))
        {
            snapshot.Alpha = alpha;
            snapshot.AlphaKnown = true;
        }

        return snapshot;
    }

    /// <summary>
    /// 应用透明度与点击穿透。
    /// opacity = 100 且 clickThrough = false 时**不强行加分层样式**，而是还原到 <paramref name="original"/> ——
    /// 否则「拉回 100%」反而会给窗口永久加上 WS_EX_LAYERED，可能改变它的重绘行为。
    /// </summary>
    internal static bool Apply(
        IntPtr hwnd, int opacity, bool clickThrough, WindowStyleSnapshot original, out string detail)
    {
        detail = string.Empty;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            detail = "窗口已失效";
            return false;
        }

        opacity = Math.Clamp(opacity, MinOpacity, MaxOpacity);

        if (opacity >= MaxOpacity && !clickThrough)
        {
            return Restore(hwnd, original, out detail);
        }

        bool originallyLayered = original.Captured
            ? (original.ExStyle & NativeMethods.WS_EX_LAYERED) != 0
            : IsLayered(hwnd);
        bool originallyTransparent = original.Captured
            ? (original.ExStyle & NativeMethods.WS_EX_TRANSPARENT) != 0
            : IsClickThrough(hwnd);

        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long wanted = ex | NativeMethods.WS_EX_LAYERED;

        if (clickThrough) wanted |= NativeMethods.WS_EX_TRANSPARENT;
        else if (!originallyTransparent) wanted &= ~NativeMethods.WS_EX_TRANSPARENT;

        if (wanted != ex)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(wanted));
            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);
        }

        byte alpha = opacity >= MaxOpacity ? (byte)255 : (byte)Math.Round(opacity * 255.0 / 100.0);

        // 防闪烁（用户 m04650）：透明度已经就是目标值时就不要再写一次 ——
        // SetLayeredWindowAttributes 每次调用都会让窗口重绘一遍（调同一个数值也照闪）。
        // 拖滑块时这是好事（值确实变了），但定时刷新/重复设同一个值时必须静默。
        bool alphaAlreadyRight = TryGetAlpha(hwnd, out byte current) && current == alpha;

        bool callOk = true;
        if (!alphaAlreadyRight)
        {
            callOk = NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
            LayeredWriteCount++;
        }

        // 回读复核：SetLayeredWindowAttributes 同样会说谎（受保护窗口、DirectComposition 窗口）
        bool layered = IsLayered(hwnd);
        bool through = IsClickThrough(hwnd);
        bool alphaOk = TryGetAlpha(hwnd, out byte actual) && actual == alpha;

        detail = $"透明度 {opacity}%（实际 {actual}{(alphaOk ? "" : "，未确认")}）" +
                 (clickThrough ? "，已开启点击穿透" : string.Empty);

        if (!callOk && !layered)
        {
            detail = $"该窗口拒绝分层样式（Win32Error={Marshal.GetLastWin32Error()}），透明度与点击穿透都不会生效";
            Log.Warn($"设置透明度失败 hwnd=0x{hwnd.ToInt64():X}：{detail}");
            return false;
        }

        if (!layered || (clickThrough != through))
        {
            detail += "；窗口样式未按预期生效（可能是 DirectComposition / 受保护窗口）";
            Log.Warn($"外观未完全生效 hwnd=0x{hwnd.ToInt64():X}：{detail}");
        }
        else if (!alphaOk && opacity < MaxOpacity)
        {
            detail += "；透明度未被确认（部分应用自己合成画面，会忽略分层透明度）";
        }

        return layered;
    }

    /// <summary>还原到我们接手之前的样子（取消置顶、把透明度拉回 100% 时调用）。</summary>
    internal static bool Restore(IntPtr hwnd, WindowStyleSnapshot original, out string detail)
    {
        detail = string.Empty;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            detail = "窗口已失效";
            return false;
        }

        if (!original.Captured)
        {
            detail = "没有记录过原始样式";
            return false;
        }

        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long wanted = original.ExStyle & (NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);

        // 只把自己动过的那两个位按原样还原，其它位保持当前值（期间可能是用户或别的程序改的）
        wanted |= ex & ~(NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);

        if (wanted != ex)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(wanted));
            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);
        }

        if ((original.ExStyle & NativeMethods.WS_EX_LAYERED) != 0)
        {
            // 本来就是分层窗口：把原来的 alpha 还回去
            byte alpha = original.AlphaKnown ? original.Alpha : (byte)255;
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
            detail = $"已还原为分层窗口（透明度 {Math.Round(alpha * 100.0 / 255) }%）";
        }
        else
        {
            detail = "已还原窗口外观";
        }

        return true;
    }
}

/// <summary>接手某个窗口之前的外观快照。</summary>
internal struct WindowStyleSnapshot
{
    internal bool Captured;
    internal long ExStyle;
    internal byte Alpha;
    internal bool AlphaKnown;

    internal static readonly WindowStyleSnapshot None = default;
}
