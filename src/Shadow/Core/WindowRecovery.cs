using System.Drawing;
using System.Windows.Forms;
using Shadow.Persistence;

namespace Shadow.Core;

/// <summary>
/// 「把窗口重新认出来」的公共逻辑。
///
/// 崩溃后清理残留置顶、重启后恢复上次的列表，都依赖同一件事：
/// 用「进程名 + 窗口类 + 标题」重新找到 HWND —— HWND 本身绝不能持久化，
/// 因为它会被系统回收复用，存下来的数字过一会儿可能指向别人家的窗口。
/// </summary>
internal static class WindowRecovery
{
    /// <summary>取消上次遗留在 topmost 层带里的置顶状态，返回真正修好的数量。</summary>
    internal static int ClearLeakedTopmost(IEnumerable<PinnedEntryDto> entries)
    {
        int repaired = 0;

        foreach (PinnedEntryDto entry in entries)
        {
            IntPtr hwnd = WindowEnumerator.FindBySignature(
                entry.ProcessName, entry.ClassName, entry.Title);

            if (hwnd == IntPtr.Zero || !WindowInfo.IsWindowTopmost(hwnd)) continue;

            ZOrderEnforcer.ClearTopmost(hwnd);

            // 回读复核：SetWindowPos 可能「成功返回但没生效」
            if (!WindowInfo.IsWindowTopmost(hwnd)) repaired++;
        }

        Log.Info($"残留置顶清理：修复 {repaired} 个窗口（记录 {entries.Count()} 条）");
        return repaired;
    }

    /// <summary>按保存顺序重新置顶，返回成功数量；找不到的窗口标题写进 missing。</summary>
    internal static int RePin(IEnumerable<PinnedEntryDto> entries, out List<string> missing)
    {
        missing = new List<string>();
        int restored = 0;

        foreach (PinnedEntryDto entry in entries)
        {
            string label = Describe(entry);

            IntPtr hwnd = WindowEnumerator.FindBySignature(
                entry.ProcessName, entry.ClassName, entry.Title);

            if (hwnd == IntPtr.Zero)
            {
                missing.Add(label);
                continue;
            }

            // 逐个追加到末尾 —— 全部追加完，层级顺序就等于保存时的顺序，
            // 也不会把用户当前手工调好的层级打乱（恢复是显式动作）
            if (!PinService.TryPin(hwnd, out _, PinPosition.Bottom))
            {
                missing.Add(label);
                continue;
            }

            restored++;

            // 位置与外观也跟着回去：恢复「上次的工作区」如果只恢复层级、窗口位置还是乱的，意义不大
            if (entry.Geometry is { IsUsable: true } geometry)
            {
                TryRestoreGeometry(hwnd, geometry, out _);
            }

            if (entry.Opacity < WindowAppearance.MaxOpacity || entry.ClickThrough)
            {
                PinService.SetAppearance(hwnd, entry.Opacity, entry.ClickThrough, out _);
            }
        }

        return restored;
    }

    /// <summary>把窗口摆回记录的位置与大小。</summary>
    internal static bool TryRestoreGeometry(IntPtr hwnd, GeometryDto geometry, out string detail)
    {
        detail = string.Empty;

        if (!geometry.IsUsable)
        {
            detail = "记录的位置或尺寸不可用（可能是最小化的窗口）";
            return false;
        }

        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            detail = "窗口已失效";
            return false;
        }

        // 先确认目标位置还落在某个显示器上：显示器拔掉后把窗口摆到不存在的屏幕上是经典事故
        bool onScreen = false;
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle bounds = screen.WorkingArea;
            var target = new Rectangle(geometry.Left, geometry.Top, geometry.Width, geometry.Height);
            if (bounds.IntersectsWith(target)) { onScreen = true; break; }
        }

        if (!onScreen)
        {
            detail = $"记录的位置 {geometry.Left},{geometry.Top} 不在任何显示器范围内，已跳过";
            Log.Warn($"恢复窗口位置被跳过：{detail}");
            return false;
        }

        WindowGeometry.MoveResize(hwnd, geometry.Left, geometry.Top, geometry.Width, geometry.Height);        detail = $"已恢复到 {geometry.Left},{geometry.Top} ({geometry.Width}x{geometry.Height})";
        return true;
    }

    /// <summary>记录窗口当前的位置与大小（用户点「记住位置」或退出时调用）。</summary>
    internal static GeometryDto? CaptureGeometry(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect)) return null;
        if (rect.Width < 80 || rect.Height < 60) return null;

        return new GeometryDto
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
        };
    }

    /// <summary>给用户看的名字：标题为空就退回进程名（很多工具窗口没有标题）。</summary>
    internal static string Describe(PinnedEntryDto entry) =>
        string.IsNullOrWhiteSpace(entry.Title) ? entry.ProcessName : entry.Title;
}
