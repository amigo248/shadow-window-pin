namespace Shadow.Core;

/// <summary>新钉住的窗口插到层级栈的哪一端。</summary>
internal enum PinPosition
{
    /// <summary>
    /// 插到最上层。这是手动置顶的默认行为：SetWindowPos(HWND_TOPMOST) 本身就把窗口
    /// 放到层带最前，用户的直觉也是「我刚钉的窗口应该在最上面」。
    /// </summary>
    Top,

    /// <summary>
    /// 插到最下层。规则引擎自动接管某类窗口时用（比如常驻的备忘录/时钟），
    /// 不应该打乱用户手工排好的层级。
    /// </summary>
    Bottom,
}

/// <summary>
/// 层级栈中的一项：一个被 Shadow 置顶的窗口。
///
/// 这里保存的是「钉住那一刻的快照」，而不是长期相信 HWND ——
/// 因为窗口销毁后 HWND 会被系统回收复用，拿旧句柄当身份凭据会操作到别的窗口上。
/// 需要重新识别时用 <see cref="Matches"/> 按 exe + 窗口类 + 标题匹配。
/// </summary>
internal sealed class PinnedWindow
{
    internal PinnedWindow(IntPtr handle, int rank, WindowInfo snapshot)
    {
        Handle = handle;
        Rank = rank;
        Snapshot = snapshot;
        PinnedAt = DateTime.Now;
    }

    internal IntPtr Handle { get; }

    /// <summary>1 = 最上层。数字越小越靠上。</summary>
    internal int Rank { get; set; }

    internal WindowInfo Snapshot { get; private set; }

    internal DateTime PinnedAt { get; }

    internal string DisplayName => Snapshot.DisplayName;

    internal uint ProcessId => Snapshot.ProcessId;

    internal bool IsAlive => NativeMethods.IsWindow(Handle);

    /// <summary>透明度百分比（10–100）。100 = 不透明。</summary>
    internal int Opacity { get; set; } = WindowAppearance.DefaultOpacity;

    /// <summary>点击穿透：鼠标事件直接穿过这个窗口。</summary>
    internal bool ClickThrough { get; set; }

    /// <summary>我们动手改外观之前的样子，取消置顶时按它还原。</summary>
    internal WindowStyleSnapshot OriginalStyle { get; set; } = WindowStyleSnapshot.None;

    /// <summary>用户是否给它设过外观（没设过就不去碰它的窗口样式）。</summary>
    internal bool HasCustomAppearance =>
        Opacity < WindowAppearance.MaxOpacity || ClickThrough || OriginalStyle.Captured;

    /// <summary>HWND 回收安全判据。</summary>
    internal bool Matches(WindowInfo other) =>
        other.ProcessName == Snapshot.ProcessName &&
        other.ClassName == Snapshot.ClassName &&
        other.Title == Snapshot.Title;

    /// <summary>刷新标题等信息（窗口改名后 UI 与匹配规则都靠它）。</summary>
    internal void Refresh()
    {
        if (IsAlive && Snapshot.Handle == Handle)
        {
            WindowInfo current = WindowInfo.Describe(Handle);
            if (current.IsValid) Snapshot = current;
        }
    }

    public override string ToString() => $"#{Rank} {Snapshot.ShortDescription}";
}
