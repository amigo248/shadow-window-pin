using System.Drawing;
using System.Windows.Forms;
using Shadow.Core;

namespace Shadow.App;

/// <summary>
/// 准星拾取器：一个只有边框和说明条的透明覆盖层，实时框住鼠标下的窗口。
///
/// 交互：左键 = 选中当前框住的窗口；Tab = 在「同进程的多个窗口」之间切换；
/// 右键 / Esc = 取消。同进程切换是必需的 —— 启动器、报错框、WebView 常常和主窗口同属一个进程，
/// 只看鼠标位置经常选错那一个。
///
/// 为什么用「轮询按键 + 透明边框窗口」而不是铺满屏幕的半透明遮罩：
///  1. 遮罩要给每个像素做混合、跨显示器还要处理 DPI，视觉瑕疵多且代码量翻倍；
///  2. 轮询 GetAsyncKeyState 不注入、不安装全局键盘钩子，与本工具「不注入、不挂钩」的安全取向一致；
///     轮询必须同时看 0x8000（当前按下）和 0x0001（上次调用之后被按过）两个位，否则快一点的点击会被漏掉；
///  3. 边框窗口只占一小块区域，鼠标事件照旧落到目标窗口上，命中测试不需要额外处理。
/// </summary>
internal sealed class PickerOverlay : IDisposable
{
    private const int BorderPad = 5;
    private const int LabelHeight = 58;
    private const int PollMs = 30;

    /// <summary>开始拾取后的静默期：用户点「拾取窗口」的那一下不能被当成「选中」。</summary>
    private const int ClickGuardMs = 250;

    private readonly PickerFrame _frame;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Font _titleFont = new("Segoe UI", 9.5F, FontStyle.Bold);
    private readonly Font _detailFont = new("Segoe UI", 8.5F);
    private readonly Font _hintFont = new("Segoe UI", 8F);

    private bool _running;
    private bool _leftDown;
    private bool _tabDown;
    private int _tickCount;
    private DateTime _startedAt;

    // 左键判定的计数（只计数、绝不写日志）。自检报「没有收到选中事件」时靠它区分三种情况：
    // 注入的按下根本没被读到 / 被 250ms 静默期挡下 / 上一轮读到的「还按着」挡住了边沿。
    // 不写日志是刻意的：加日志会改变时序，之前就出现过「加上日志就再也不复现」的假象。
    private int _leftSeenTicks;
    private int _leftBlockedByGuard;
    private int _leftBlockedByAlreadyDown;
    private short _lastLeftRaw;

    private IntPtr _current = IntPtr.Zero;
    private WindowInfo _currentInfo = WindowInfo.Invalid;
    private List<WindowInfo> _group = new();
    private int _groupIndex;
    private PinAdvice? _advice;
    private bool _disposed;

    internal PickerOverlay()
    {
        _frame = new PickerFrame(this);
        _timer = new System.Windows.Forms.Timer { Interval = PollMs };
        _timer.Tick += (_, _) => OnTick();
    }

    /// <summary>左键选中了某个窗口。</summary>
    internal event Action<IntPtr>? Picked;

    /// <summary>右键 / Esc 取消。</summary>
    internal event Action? Cancelled;

    internal bool IsRunning => _running;

    /// <summary>已轮询次数（自检用）。</summary>
    internal int TickCount => _tickCount;

    /// <summary>左键判定的诊断串（自检在「没收到选中事件」时打印，平时没人看）。</summary>
    internal string LeftDiagnostic =>
        $"轮询总数={_tickCount} 读到左键按下的轮询={_leftSeenTicks}"
        + $"（其中被 250ms 静默期挡下 {_leftBlockedByGuard}、被「上一轮就已经按着」挡下 {_leftBlockedByAlreadyDown}）"
        + $" 最后一次原始值=0x{_lastLeftRaw & 0xFFFF:X4}";

    /// <summary>
    /// 立刻轮询一次（自检专用）。
    /// <para>
    /// 产品路径由 30ms 的 System.Windows.Forms.Timer 驱动；但自检模式没有 Application.Run，
    /// 只用 Application.DoEvents() 抽空泵消息，而 WM_TIMER 是队列里最低优先级、队列不空就不生成的消息
    /// （实测有时几秒都不来一次）。让自检直接驱动轮询，才能验证拾取逻辑本身，
    /// 而不是把「定时器没被调度」误判成「点击没生效」。
    /// </para>
    /// </summary>
    internal void PollNow() => OnTick();

    /// <summary>当前框住的窗口（自检与界面回显用）。</summary>
    internal IntPtr CurrentHandle => _current;

    internal WindowInfo CurrentInfo => _currentInfo;

    internal PinAdvice? CurrentAdvice => _advice;

    /// <summary>边框窗口（自检要验证它的位置与可见性）。</summary>
    internal Form FrameWindow => _frame;

    internal void Start()
    {
        if (_disposed || _running) return;

        _running = true;
        _leftDown = false;
        _tabDown = false;
        _tickCount = 0;
        _leftSeenTicks = 0;
        _leftBlockedByGuard = 0;
        _leftBlockedByAlreadyDown = 0;
        _lastLeftRaw = 0;
        _startedAt = DateTime.UtcNow;

        // 先「消费」一次按键状态。GetAsyncKeyState 的 0x0001 位记录的是「上次调用之后被按过」，
        // 而拾取器通常正是被一次左键点击（点「拾取窗口」按钮或托盘菜单）拉起来的：
        // 那个位的残留会让首次轮询误判成「用户点了目标窗口」。这里空调用几次把它清掉。
        NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
        NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON);
        NativeMethods.GetAsyncKeyState(NativeMethods.VK_TAB);
        NativeMethods.GetAsyncKeyState(NativeMethods.VK_ESCAPE);
        _current = IntPtr.Zero;
        _group = new List<WindowInfo>();
        _groupIndex = 0;

        UpdateUnderCursor();

        _frame.Show();
        _frame.TopMost = true;
        _timer.Start();
        Log.Info("拾取器已启动");
    }

    internal void Stop()
    {
        if (!_running) return;

        _timer.Stop();
        _running = false;
        _frame.Hide();
        Log.Info("拾取器已停止");
    }

    private void OnTick()
    {
        if (!_running) return;

        _tickCount++;

        // 四个键各读一次：一次调用同时取回「当前按下」（0x8000）与「上次调用之后被按过」（0x0001）。
        // 只看 0x8000 是不够的 —— 轮询周期 30ms，比它更短的点击（真人快速点一下也是几十毫秒）
        // 会在两次轮询之间按完又松开，直接漏掉；0x0001 位就是为了这种轮询场景设计的。
        bool escape = ReadKey(NativeMethods.VK_ESCAPE);
        bool right = ReadKey(NativeMethods.VK_RBUTTON);
        if (escape || right)
        {
            Stop();
            Cancelled?.Invoke();
            return;
        }

        bool tab = ReadKey(NativeMethods.VK_TAB);
        if (tab && !_tabDown) CycleGroup();
        _tabDown = tab;

        // 左键这次单独读原始值：ReadKey 只看真假，诊断需要留下原始状态才能定位
        // （见 LeftDiagnostic 的注释）。
        short leftRaw = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
        bool left = (leftRaw & 0x8000) != 0 || (leftRaw & 0x0001) != 0;
        _lastLeftRaw = leftRaw;
        if (left)
        {
            _leftSeenTicks++;
            if (_leftDown) _leftBlockedByAlreadyDown++;
        }

        bool guardOpen = (DateTime.UtcNow - _startedAt).TotalMilliseconds > ClickGuardMs;
        if (left && !_leftDown && guardOpen)
        {
            IntPtr picked = _current;
            Stop();
            if (picked != IntPtr.Zero) Picked?.Invoke(picked);
            return;
        }

        if (left && !_leftDown && !guardOpen) _leftBlockedByGuard++;

        _leftDown = left;

        UpdateUnderCursor();
    }

    /// <summary>
    /// 该键此刻是否需要响应：当前按下，或者自上次调用以来被按过一次。
    /// <para>
    /// 必须一个键一次调用 —— GetAsyncKeyState 每次调用都会清掉 0x0001 位，
    /// 分成两次读会把「刚刚按过」这个事实吃掉，表现就是点击偶尔没反应。
    /// </para>
    /// </summary>
    private static bool ReadKey(int virtualKey)
    {
        short state = NativeMethods.GetAsyncKeyState(virtualKey);
        bool down = (state & 0x8000) != 0;
        bool pressed = (state & 0x0001) != 0;
        return down || pressed;
    }

    private void UpdateUnderCursor()
    {
        IntPtr hwnd = WindowEnumerator.GetRootWindowUnderCursor();

        if (hwnd != _current)
        {
            _current = hwnd;
            _currentInfo = WindowInfo.Describe(hwnd);
            _advice = _currentInfo.IsValid ? PinAdvisor.Evaluate(_currentInfo) : null;

            // 同进程候选：拾取器自己盖在目标上时 WindowFromPoint 取到的仍是目标，
            // 所以这里拿到的是「鼠标下那个」，用它所在的进程去凑候选列表
            _group = WindowEnumerator.SiblingsOf(hwnd);
            _groupIndex = 0;
            for (int i = 0; i < _group.Count; i++)
            {
                if (_group[i].Handle == hwnd) { _groupIndex = i; break; }
            }
        }

        Reposition();
        _frame.Invalidate();
    }

    private void CycleGroup()
    {
        if (_group.Count <= 1) return;

        _groupIndex = (_groupIndex + 1) % _group.Count;
        _current = _group[_groupIndex].Handle;
        _currentInfo = _group[_groupIndex];
        _advice = PinAdvisor.Evaluate(_currentInfo);
        Reposition();
        _frame.Invalidate();
    }

    /// <summary>把边框窗口摆到目标窗口外面（上方优先，上方放不下就放进窗口内侧）。</summary>
    private void Reposition()
    {
        if (!_currentInfo.IsValid || _currentInfo.Width <= 0 || _currentInfo.Height <= 0)
        {
            _frame.Visible = false;
            return;
        }

        if (!NativeMethods.GetWindowRect(_current, out NativeMethods.RECT rect)) return;

        Rectangle screen = Screen.FromRectangle(ToRectangle(rect)).Bounds;

        int labelTop = rect.Top - LabelHeight;

        // 判据用「框的上边缘」（labelTop - BorderPad）而不是 labelTop：
        // 差这一个 BorderPad 会让贴顶窗口的说明条被屏幕边缘切掉最上面一行（实测 y=-3 时就是如此）。
        // 放不下就改画在窗口内侧 —— 说明条位置难看一点，也比文字被切掉好。
        bool labelInside = labelTop - BorderPad < screen.Top;

        Rectangle bounds = labelInside
            ? new Rectangle(rect.Left - BorderPad, rect.Top - BorderPad,
                rect.Width + BorderPad * 2, rect.Height + BorderPad * 2)
            : new Rectangle(rect.Left - BorderPad, labelTop - BorderPad,
                rect.Width + BorderPad * 2, rect.Height + LabelHeight + BorderPad * 2);

        _labelInside = labelInside;

        if (!_frame.Visible) _frame.Visible = true;

        // SetWindowPos 而不是 Bounds：避免激活目标窗口抢焦点
        NativeMethods.SetWindowPos(
            _frame.Handle,
            NativeMethods.HWND_TOPMOST,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private bool _labelInside;

    private static Rectangle ToRectangle(NativeMethods.RECT rect) =>
        Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    /// <summary>由边框窗口回调，负责把内容画出来（键控色区域自动变成透明）。</summary>
    internal void PaintFrame(Graphics g, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        Color accent = _advice switch
        {
            { CanPin: false } => Color.FromArgb(255, 90, 90),
            { HasWarning: true } => Color.FromArgb(255, 200, 120),
            _ => Color.FromArgb(90, 200, 255),
        };

        int labelH = _labelInside ? LabelHeight : LabelHeight;
        Rectangle labelRect = new(BorderPad, BorderPad, Math.Max(0, width - BorderPad * 2), labelH - BorderPad);
        Rectangle borderRect = _labelInside
            ? new Rectangle(labelRect.Left, labelRect.Bottom, labelRect.Width, Math.Max(0, height - labelRect.Bottom - BorderPad))
            : new Rectangle(BorderPad, labelRect.Bottom + 2, Math.Max(0, width - BorderPad * 2), Math.Max(0, height - labelRect.Bottom - BorderPad - 2));

        using var accentPen = new Pen(accent, 3f);
        using var backBrush = new SolidBrush(Color.FromArgb(240, 18, 20, 26));

        g.FillRectangle(backBrush, labelRect);
        g.DrawRectangle(accentPen, labelRect);

        if (borderRect.Width > 2 && borderRect.Height > 2)
        {
            using var dash = new Pen(accent, 3f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Solid };
            g.DrawRectangle(dash, borderRect);
        }

        string title = _currentInfo.IsValid
            ? Truncate(_currentInfo.DisplayName, 96)
            : "（鼠标下没有可拾取的窗口）";

        string detail = _currentInfo.IsValid
            ? $"[{_currentInfo.ClassName}] {_currentInfo.ProcessName}({_currentInfo.ProcessId}) " +
              $"{_currentInfo.Width}x{_currentInfo.Height}" + GroupSuffix()
            : "把鼠标移到要置顶的窗口上";

        string status = _advice switch
        {
            null => string.Empty,
            { CanPin: false } advice => "✖ " + advice.Message,
            { HasWarning: true } advice => "⚠ " + advice.Warning,
            _ => "✔ 可以置顶",
        };

        g.DrawString(title, _titleFont, Brushes.White, labelRect.X + 8, labelRect.Y + 4);
        g.DrawString(detail, _detailFont, Brushes.Gainsboro, labelRect.X + 8, labelRect.Y + 22);
        g.DrawString(Truncate(status, 78), _detailFont, new SolidBrush(accent), labelRect.X + 8, labelRect.Y + 37);
        g.DrawString("左键选中 · Tab 切换同进程窗口 · 右键或 Esc 取消", _hintFont, Brushes.Gray,
            labelRect.Right - 268, labelRect.Bottom - 16);
    }

    private string GroupSuffix() =>
        _group.Count > 1 ? $"　同进程第 {_groupIndex + 1}/{_group.Count} 个（Tab 切换）" : string.Empty;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Dispose();
        _frame.Dispose();
        _titleFont.Dispose();
        _detailFont.Dispose();
        _hintFont.Dispose();
    }

    /// <summary>
    /// 只有边框和说明条的窗口：
    /// 背景色 = 键控色（Fuchsia）→ 未绘制的区域完全透明且不参与命中测试；
    /// WS_EX_NOACTIVATE + ShowWithoutActivation → 绝不抢焦点（否则拾取一下就改变了前台窗口）。
    /// </summary>
    private sealed class PickerFrame : Form
    {
        private readonly PickerOverlay _owner;

        internal PickerFrame(PickerOverlay owner)
        {
            _owner = owner;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia;
            DoubleBuffered = true;
            MinimizeBox = false;
            MaximizeBox = false;
            Text = "Shadow 拾取器";
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // 键控色 = 透明，所以每帧先把整块刷成键控色，再画真正要显示的部分
            e.Graphics.Clear(Color.Fuchsia);
            _owner.PaintFrame(e.Graphics, ClientSize.Width, ClientSize.Height);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 什么都不做：背景必须是键控色，交给 OnPaint 的 Clear
        }
    }
}
