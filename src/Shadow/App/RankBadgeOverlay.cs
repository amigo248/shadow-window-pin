using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Shadow.Core;

namespace Shadow.App;

/// <summary>
/// 层级徽标叠加层：给每个已置顶窗口在左上角贴一个小圆牌，写着它的层号（1 = 最上层）。
///
/// 「N 级自定义层级」这个功能最大的问题是不可见 —— 用户排完序看不出谁在上面。
/// 徽标把内部层级表直接画到目标窗口上，是最省事也最直观的表达。
///
/// 两个必须处理的坑：
///  1. 点击穿透：徽标不能被点到，否则它就挡住了目标窗口左上角的按钮。
///     做法是设 WS_EX_TRANSPARENT（命中测试直接跳过）+ WS_EX_LAYERED + WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW。
///  2. 被压到下面：本工具的压栈动作会把置顶窗口抬到最前，徽标自然被盖住。
///     所以徽标自己带一个 500ms 的定时器，周期性检查自己有没有被压下去。
///     **检查不等于动手**（用户 m04650 的防闪烁要求）：位置没变、自己也仍在层带最前时，
///     一拍 SetWindowPos 都不发 —— 否则每个徽标每秒改两次层带顺序，
///     目标窗口（尤其是游戏）的画面就会周期性闪一下。
/// </summary>
internal sealed class RankBadgeOverlay : IDisposable
{
    private const int BadgeSize = 30;
    private const int BadgeMargin = 8;

    /// <summary>
    /// 徽标小窗口的「键控色」（画成这个颜色的像素会被抠成完全透明）。
    ///
    /// 为什么不用 WinForms 文档里常见的 <c>Color.Fuchsia</c>：圆形是抗锯齿画的，
    /// 边缘那一圈像素是「描边色 + 键控色」的混合，它们不等于键控色，所以抠不掉 ——
    /// 用 Fuchsia(255,0,255) 时，这一圈混合出来就是**刺眼的品红描边**（README 截图里一眼就能看见）。
    /// 取一个接近黑的深色，混合结果只是淡淡一圈暗边，看着像阴影；同时它必须与徽标自身用色
    /// 都不相同（内部的深色底是 18,20,26），否则会被一起抠掉。
    /// </summary>
    private static readonly Color KeyColor = Color.FromArgb(1, 2, 3);

    private readonly Dictionary<IntPtr, BadgeForm> _badges = new();

    /// <summary>每个徽标最近一次真正摆到的位置（没变就不重复写 Z 序）。</summary>
    private readonly Dictionary<IntPtr, Rectangle> _placedAt = new();

    private readonly System.Windows.Forms.Timer _raiseTimer;
    private bool _visible;
    private bool _disposed;
    private int _zOrderWrites;

    internal RankBadgeOverlay()
    {
        _raiseTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _raiseTimer.Tick += (_, _) => RaiseAll();
    }

    /// <summary>
    /// 徽标窗口真的被写过几次 Z 序（自检用：稳定状态下连续 RaiseAll 不该让它增长）。
    /// </summary>
    internal int ZOrderWriteCount => _zOrderWrites;

    /// <summary>是否显示徽标（对应主面板上的开关）。</summary>
    internal bool IsVisible => _visible;

    /// <summary>当前贴出来的徽标数量（自检与状态栏用）。</summary>
    internal int BadgeCount => _badges.Count;

    /// <summary>某个目标窗口对应的徽标窗口（自检用；没贴出来时为 null）。</summary>
    internal Form? BadgeWindowFor(IntPtr target) =>
        _badges.TryGetValue(target, out BadgeForm? badge) ? badge : null;

    internal void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;

        _visible = visible;

        if (visible)
        {
            Log.Info("层级徽标：已开启");
        }
        else
        {
            ClearBadges();
            Log.Info("层级徽标：已关闭");
        }
    }

    /// <summary>按当前层级表刷新徽标：多的删、少的建、层号与位置重画。</summary>
    internal void Refresh(IReadOnlyList<PinnedWindow> items)
    {
        if (_disposed) return;

        if (!_visible)
        {
            ClearBadges();
            return;
        }

        var wanted = new HashSet<IntPtr>();
        foreach (PinnedWindow item in items)
        {
            // 松散模式的窗口（独占全屏 / 铺满整屏的游戏）不贴徽标：
            // 徽标是带 WS_EX_TOPMOST 的独立顶层窗口，画在游戏上面会跟着游戏的呈现方式一起闪，
            // 而且它本来就不是我们能真正盖住的窗口。
            if (item.IsAlive && !PinService.IsLoose(item.Handle)) wanted.Add(item.Handle);
        }

        foreach (IntPtr handle in _badges.Keys.ToArray())
        {
            if (!wanted.Contains(handle)) RemoveBadge(handle);
        }

        int order = 0;
        foreach (PinnedWindow item in items)
        {
            if (!item.IsAlive) continue;
            if (PinService.IsLoose(item.Handle)) continue;

            order++;
            if (!_badges.TryGetValue(item.Handle, out BadgeForm? badge))
            {
                badge = new BadgeForm();

                // 先读 Handle（会顺带创建 HWND），再把点击穿透的扩展样式补上
                MakeClickThrough(badge.Handle);
                _badges[item.Handle] = badge;
            }

            badge.SetRank(item.Rank);
            PositionBadge(badge, item.Handle);
        }

        // 顺序乱了或徽标被压到目标下面时，抬起一次
        RaiseAll();
    }

    /// <summary>立即把所有徽标抬到层带最前，并校正位置。</summary>
    internal void RaiseAll()
    {
        if (_disposed || !_visible) return;

        foreach ((IntPtr target, BadgeForm badge) in _badges)
        {
            PositionBadge(badge, target);
        }
    }

    private void PositionBadge(BadgeForm badge, IntPtr target)
    {
        if (!NativeMethods.IsWindow(target))
        {
            HideBadge(badge, target);
            return;
        }

        // 最小化或 DWM 隐藏时不该有徽标悬在屏幕角落
        if (NativeMethods.IsIconic(target) || !NativeMethods.IsWindowVisible(target) ||
            WindowInfo.IsWindowCloaked(target))
        {
            HideBadge(badge, target);
            return;
        }

        if (!NativeMethods.GetWindowRect(target, out NativeMethods.RECT rect))
        {
            HideBadge(badge, target);
            return;
        }

        Rectangle screen = Screen.FromRectangle(
            Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)).WorkingArea;

        int x = Math.Max(screen.Left + 2, Math.Min(rect.Left + BadgeMargin, screen.Right - BadgeSize - 2));
        int y = Math.Max(screen.Top + 2, Math.Min(rect.Top + BadgeMargin, screen.Bottom - BadgeSize - 2));

        if (!badge.Visible) badge.Visible = true;

        // 防闪烁核心：位置没变、自己也仍在层带最前 → 一次 SetWindowPos 都不发。
        // 以前这里是每 500ms 无条件写一次，等于每个徽标每秒改两次层带顺序（用户 m04650）。
        var placed = new Rectangle(x, y, BadgeSize, BadgeSize);
        bool moved = !_placedAt.TryGetValue(target, out Rectangle last) || last != placed;

        if (!moved && WindowInfo.IsWindowTopmost(badge.Handle)) return;

        NativeMethods.SetWindowPos(
            badge.Handle,
            NativeMethods.HWND_TOPMOST,
            x, y, BadgeSize, BadgeSize,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        _placedAt[target] = placed;
        _zOrderWrites++;
    }

    /// <summary>收起徽标，并清掉它的位置记账（下次要重新摆）。</summary>
    private void HideBadge(BadgeForm badge, IntPtr target)
    {
        badge.Visible = false;
        _placedAt.Remove(target);
    }

    private static void MakeClickThrough(IntPtr hwnd)
    {
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
                   NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void RemoveBadge(IntPtr target)
    {
        if (!_badges.TryGetValue(target, out BadgeForm? badge)) return;

        _badges.Remove(target);
        _placedAt.Remove(target);
        badge.Dispose();
    }

    private void ClearBadges()
    {
        foreach (BadgeForm badge in _badges.Values) badge.Dispose();
        _badges.Clear();
        _placedAt.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _raiseTimer.Stop();
        _raiseTimer.Dispose();
        ClearBadges();
    }

    /// <summary>只画一个圆牌的小窗口，背景键控色 → 圆形之外完全透明。</summary>
    private sealed class BadgeForm : Form
    {
        private readonly Font _font = new("Segoe UI", 12F, FontStyle.Bold);
        private int _rank = 1;

        internal BadgeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            DoubleBuffered = true;
            Size = new Size(BadgeSize, BadgeSize);
            MinimumSize = new Size(BadgeSize, BadgeSize);
            MaximumSize = new Size(BadgeSize, BadgeSize);
            Text = "Shadow 层级徽标";
        }

        internal void SetRank(int rank)
        {
            if (_rank == rank) return;

            _rank = rank;
            Invalidate();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= (int)(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 背景必须是键控色（透明），所以什么都不画
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.Clear(KeyColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var circle = new Rectangle(1, 1, Width - 3, Height - 3);

            // 第 1 层用高亮色：最上层的那个是用户最关心的
            Color accent = _rank == 1 ? Color.FromArgb(255, 90, 200, 255) : Color.FromArgb(255, 150, 160, 175);

            using var fill = new SolidBrush(Color.FromArgb(255, 18, 20, 26));
            using var pen = new Pen(accent, 2.5f);

            g.FillEllipse(fill, circle);
            g.DrawEllipse(pen, circle);

            string text = _rank.ToString();
            SizeF size = g.MeasureString(text, _font);
            g.DrawString(text, _font, Brushes.White,
                circle.X + (circle.Width - size.Width) / 2f,
                circle.Y + (circle.Height - size.Height) / 2f + 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }
}
