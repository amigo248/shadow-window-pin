using Shadow.Core;
using Shadow.Persistence;

namespace Shadow.App;

/// <summary>
/// 主面板：置顶列表 + 层级拖拽 + 全局开关。
///
/// 设计取舍：
/// - 拖拽排序用 ListView 自带的插入标记（InsertionMark），不引第三方拖拽库。
/// - 「拾取窗口」先隐藏面板再取鼠标下的窗口 —— 面板自己挡在鼠标下面时取到的会是它自己。
/// - 关闭窗口只是隐藏，真正退出走托盘/按钮（常驻工具不能点个叉就没了）。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly TrayIcon _tray;

    private readonly ListView _list = new();
    private readonly FlowLayoutPanel _toolbar = new();
    private readonly TableLayoutPanel _bottom = new();

    private readonly Button _pickButton = new();
    private readonly Button _unpinButton = new();
    private readonly Button _upButton = new();
    private readonly Button _downButton = new();
    private readonly Button _topButton = new();
    private readonly Button _restoreButton = new();

    private readonly CheckBox _strictBox = new();
    private readonly CheckBox _autoStartBox = new();
    private readonly CheckBox _badgeBox = new();
    private readonly Button _unpinAllButton = new();
    private readonly Button _exitButton = new();
    private readonly Label _statusLabel = new();

    // M5：外观（透明度 / 点击穿透）、场景、规则、设置入口
    private readonly TrackBar _opacityBar = new();
    private readonly Label _opacityLabel = new();
    private readonly CheckBox _clickThroughBox = new();
    private readonly Button _appearanceResetButton = new();
    private readonly ComboBox _sceneBox = new();
    private readonly Button _sceneSaveButton = new();
    private readonly Button _sceneApplyButton = new();
    private readonly Button _sceneDeleteButton = new();
    private readonly Button _rulesButton = new();
    private readonly Button _moreButton = new();

    /// <summary>场景行：面板最小宽度按它的首选宽度算，否则最右边的「更多 ▾」会被裁掉。</summary>
    private readonly Control _sceneRow;

    /// <summary>拖动滑块会连续触发，用它合并成一次 SetWindowPos/SetLayeredWindowAttributes。</summary>
    private readonly System.Windows.Forms.Timer _appearanceTimer = new() { Interval = 250 };

    /// <summary>把控件状态同步成「选中窗口的现状」时用的重入保护。</summary>
    private bool _syncingAppearance;

    /// <summary>
    /// 上一次真正生效的点击穿透状态。
    /// 用途：点击穿透只在这条「关 → 开」的边沿上提示一次（它是唯一可能把用户关在门外的操作），
    /// 而不是拖动透明度滑块时每次都弹。
    /// </summary>
    private bool _lastClickThrough;

    /// <summary>
    /// 上一次已经弹过的外观失败原因。
    /// 拖动滑块每 250ms 就会试一次，失败时如果每次都弹气球会变成刷屏 ——
    /// 只在原因发生变化时提示一次。
    /// </summary>
    private string _lastAppearanceFailure = string.Empty;

    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };

    /// <summary>
    /// ShowPanel 临时置顶的兜底还原。
    /// Activate() 可能被系统拒绝（后台进程没有抢前台的权限），那样 Deactivate 永远不会来，
    /// 面板就会一直飘在别人上面 —— 用它兜底。
    /// </summary>
    private readonly System.Windows.Forms.Timer _topMostTimer = new() { Interval = 3000 };

    // M4：拾取器从「隐藏面板 + 1.5 秒定时器」升级成真正的准星覆盖层 ——
    // 定时器方案只能靠用户自己把鼠标挪好，没有实时反馈，也没法在同进程的多个窗口里挑。
    private readonly PickerOverlay _picker = new();

    private readonly string _toggleHotkeyText;
    private readonly string _unpinAllHotkeyText;
    private readonly ToolTip _tips = new();
    private string _hotkeySummary = string.Empty;

    private int _dropIndex = -1;
    private bool _suppressEvents;
    private bool _reallyExit;

    internal MainForm(TrayIcon tray, string toggleHotkeyText, string unpinAllHotkeyText)
    {
        _tray = tray;
        _toggleHotkeyText = toggleHotkeyText;
        _unpinAllHotkeyText = unpinAllHotkeyText;
        _hotkeySummary = $"{toggleHotkeyText} / {unpinAllHotkeyText}";

        Text = "Shadow · 窗口置顶";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(600, 440); // 宽度只是下限，真正的下限由 ApplyMinimumWidth 按场景行算
        ShowInTaskbar = true;

        BuildListView();
        BuildToolbar();
        _sceneRow = BuildSceneRow();
        BuildBottom();

        // 停靠顺序：Fill 先加、边缘后加，边缘控件先占位、Fill 吃剩下的空间
        Controls.Add(_list);
        Controls.Add(_toolbar);
        Controls.Add(_bottom);

        PinService.Changed += RefreshList;
        _refreshTimer.Tick += OnRefreshTick;
        _topMostTimer.Tick += OnTopMostTimeout;

        _picker.Picked += OnPicked;
        _picker.Cancelled += OnPickCancelled;

        Load += (_, _) =>
        {
            ApplyProfile();
            RefreshScenes();
        };
        FormClosing += OnFormClosing;
        Resize += (_, _) => LayoutColumns();
        Shown += (_, _) =>
        {
            ApplyMinimumWidth();
            RefreshList();
            RefreshScenes();
        };

        _refreshTimer.Start();
    }

    /// <summary>恢复上次列表：把配置里的窗口按原顺序重新钉住。</summary>
    internal event EventHandler? RestoreRequested;

    /// <summary>用户点了「退出」。</summary>
    internal event EventHandler? ExitRequested;

    /// <summary>用户切换了「显示层级徽标」。</summary>
    internal event Action<bool>? RankBadgeVisibilityChanged;

    /// <summary>徽标开关当前是否勾选（自检用）。</summary>
    internal bool RankBadgeChecked => _badgeBox.Checked;

    /// <summary>拾取器（自检要直接驱动它）。</summary>
    internal PickerOverlay Picker => _picker;

    /// <summary>让窗口可以真正关闭（退出流程中调用）。</summary>
    internal void AllowClose() => _reallyExit = true;

    /// <param name="temporaryTopMost">
    /// 是否允许用「临时置顶几秒」的办法把面板压到最前。
    /// 托盘双击 / CLI <c>panel</c> 唤出时需要它：屏幕上可能有用户自己置顶的窗口，
    /// 普通窗口永远压不过它们（实测 Activate/BringToFront 都唤不出来）。
    ///
    /// 启动时**不要**用它（见 ShadowApp.Start）：用户要的是「界面在屏幕最上层、一眼看得到」，
    /// 而不是「Shadow 的面板自己变成一个置顶窗口」。那时只做一次性前抬，不带 WS_EX_TOPMOST。
    ///
    /// 另外，即使传了 true，也只有面板**真的被别的进程的置顶窗口压住**时才动用置顶属性
    /// （用户 m04650 的防闪烁要求）：给面板加 WS_EX_TOPMOST 会让它上面的每个置顶窗口
    /// 画面闪一下，而「抬到最前 + 抢焦点」在没被压住时已经完全够用。
    /// </param>
    internal void ShowPanel(bool temporaryTopMost = true)
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        if (temporaryTopMost && CoveredByTopMostWindow())
        {
            TopMost = true;
            _topMostTimer.Stop();
            _topMostTimer.Start();
        }
        else
        {
            // 一次性抬到普通窗口层带的最前：先确保没有置顶属性，再 HWND_TOP + 后面那个 Activate()。
            // 这样面板在最上层是「这一刻被抬上来的」，而不是「一直浮在所有窗口之上」。
            DropTemporaryTopMost();
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOP,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        RefreshList();
        RefreshScenes();
        Activate();
        BringToFront();
    }

    /// <summary>
    /// 最小宽度按「场景行的首选宽度」算，而不是拍一个数字。
    /// 场景行是 WrapContents=false 的 FlowLayoutPanel：装不下时最右边的「更多 ▾」
    /// 会被直接裁掉（实测 660 宽时按钮少了一截），按钮文案或系统字体一变，
    /// 硬编码的数字又会失准。
    /// </summary>
    private void ApplyMinimumWidth()
    {
        // 直接量场景行当前的实际宽度，而不是它 PreferredSize —— 实测在 Shown 时
        // PreferredSize 偏小（算出 600 的下限），而布局后它的真实宽度已经 685，
        // 结果最右边的「更多 ▾」照样被裁。
        int chrome = Math.Max(0, Width - ClientSize.Width); // 边框
        int needed = _sceneRow.Right + _bottom.Padding.Horizontal + chrome + 8;
        MinimumSize = new Size(Math.Max(600, needed), MinimumSize.Height);

        // 改 MinimumSize 不保证立刻把当前尺寸拉上去，手动补一刀，
        // 否则从旧配置（PanelWidth 只有 560）起来时会一直露着被裁的一行。
        if (Width < MinimumSize.Width) Width = MinimumSize.Width;
    }

    /// <summary>场景行是否完整落在面板客户区里（自检用）。</summary>
    internal bool SceneRowFits => _sceneRow.Right + _bottom.Padding.Right <= _bottom.ClientSize.Width;

    /// <summary>场景行右边缘 / 客户区宽度（自检打印用）。</summary>
    internal int SceneRowRight => _sceneRow.Right;

    internal int ClientWidth => ClientSize.Width;

    private void OnTopMostTimeout(object? sender, EventArgs e)
    {
        _topMostTimer.Stop();

        // 已经拿到焦点说明用户正在用它，继续留着；被置顶窗口盖住则必须留着，
        // 否则面板又藏回那扇窗口后面（这正是用户抱怨的「唤不到最前」）。
        if (ContainsFocus) return;
        if (CoveredByTopMostWindow()) return;

        DropTemporaryTopMost();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        // 焦点跑到别的程序去了：用户已经不在用面板，别再飘在人家上面。
        if (!CoveredByTopMostWindow()) DropTemporaryTopMost();
    }

    private void DropTemporaryTopMost()
    {
        _topMostTimer.Stop();
        if (TopMost) TopMost = false;
    }

    /// <summary>面板矩形是否被别的进程的置顶窗口压住（我们自己的徽标/拾取框不算）。</summary>
    private bool CoveredByTopMostWindow()
    {
        if (!NativeMethods.GetWindowRect(Handle, out NativeMethods.RECT mine)) return false;

        uint self = (uint)Environment.ProcessId;

        foreach (IntPtr candidate in WindowEnumerator.EnumerateTopLevel())
        {
            if (candidate == Handle) continue;
            if (!WindowInfo.IsWindowTopmost(candidate)) continue;
            if (!NativeMethods.GetWindowRect(candidate, out NativeMethods.RECT other)) continue;

            bool overlaps = other.Left < mine.Right && other.Right > mine.Left &&
                            other.Top < mine.Bottom && other.Bottom > mine.Top;
            if (!overlaps) continue;

            NativeMethods.GetWindowThreadProcessId(candidate, out uint owner);
            if (owner == self) continue; // 层级徽标、拾取框都是我们自己的顶层窗口

            return true;
        }

        return false;
    }

    internal void UpdateHotkeyText(string toggle, string unpinAll)
    {
        _hotkeySummary = $"{toggle} / {unpinAll}";
        RefreshList();
    }

    // -------------------------------------------------- 自检用的只读开口
    // 只暴露「读出来的事实」，不把控件本身交给外部随便改。

    /// <summary>列表里当前显示的行数。</summary>
    internal int RowCount => _list.Items.Count;

    /// <summary>第 index 行对应的窗口句柄（越界或无效返回 Zero）。</summary>
    internal IntPtr HandleAt(int index) =>
        index >= 0 && index < _list.Items.Count && _list.Items[index].Tag is IntPtr handle
            ? handle
            : IntPtr.Zero;

    /// <summary>第 index 行「层级」列显示的文字。</summary>
    internal string RankTextAt(int index) =>
        index >= 0 && index < _list.Items.Count ? _list.Items[index].SubItems[0].Text : string.Empty;

    /// <summary>第 index 行「状态」列显示的文字。</summary>
    internal string StateTextAt(int index)
    {
        if (index < 0 || index >= _list.Items.Count) return string.Empty;
        ListViewItem.ListViewSubItemCollection sub = _list.Items[index].SubItems;
        return sub.Count > 3 ? sub[3].Text : string.Empty;
    }

    // ------------------------------------------------------------ 构建界面

    private void BuildListView()
    {
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.GridLines = false;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.AllowDrop = true;
        _list.Columns.Add("层级", 56, HorizontalAlignment.Center);
        _list.Columns.Add("窗口", 240, HorizontalAlignment.Left);
        _list.Columns.Add("进程", 150, HorizontalAlignment.Left);
        _list.Columns.Add("状态", 90, HorizontalAlignment.Left);

        _list.ItemDrag += OnItemDrag;
        _list.DragEnter += OnDragEnter;
        _list.DragOver += OnDragOver;
        _list.DragLeave += (_, _) => _list.InsertionMark.Index = -1;
        _list.DragDrop += OnDragDrop;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.KeyDown += OnListKeyDown;
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.AutoSize = true;
        _toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _toolbar.Padding = new Padding(6, 6, 6, 2);

        ConfigureButton(_pickButton, "拾取窗口", (_, _) => StartPicking());
        ConfigureButton(_unpinButton, "取消置顶", (_, _) => UnpinSelected());
        ConfigureButton(_upButton, "上移 ▲", (_, _) => MoveSelected(-1));
        ConfigureButton(_downButton, "下移 ▼", (_, _) => MoveSelected(1));
        ConfigureButton(_topButton, "置为第 1 层", (_, _) => MoveSelectedToTop());
        ConfigureButton(_restoreButton, "恢复上次列表", (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty));

        _tips.SetToolTip(_pickButton, "开始后移动鼠标选择窗口，左键选中，Tab 切换同进程窗口，右键或 Esc 取消");
        _tips.SetToolTip(_upButton, "热键 Ctrl+Alt+1…9 也能直接指定层级");

        _toolbar.Controls.AddRange(new Control[]
        {
            _pickButton, _unpinButton, _upButton, _downButton, _topButton, _restoreButton,
        });
    }

    private void BuildBottom()
    {
        _bottom.Dock = DockStyle.Bottom;
        _bottom.AutoSize = true;
        _bottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _bottom.ColumnCount = 1;
        _bottom.RowCount = 5;
        _bottom.Padding = new Padding(6, 2, 6, 4);

        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _strictBox.Text = "严格层级模式（推荐）";
        _strictBox.AutoSize = true;
        _strictBox.Margin = new Padding(0, 4, 16, 0);
        _strictBox.CheckedChanged += OnStrictChanged;
        _tips.SetToolTip(_strictBox, "勾选：谁抢到前面都会按层级压回去；取消：允许点击把已置顶窗口提到最前");

        _autoStartBox.Text = "开机自动启动（最高权限，静默）";
        _autoStartBox.AutoSize = true;
        _autoStartBox.Margin = new Padding(0, 4, 0, 0);
        _autoStartBox.CheckedChanged += OnAutoStartChanged;
        _tips.SetToolTip(_autoStartBox, "通过任务计划程序登录时静默提权启动，不弹 UAC");

        _badgeBox.Text = "显示层级徽标";
        _badgeBox.AutoSize = true;
        _badgeBox.Margin = new Padding(16, 4, 0, 0);
        _badgeBox.CheckedChanged += OnBadgeChanged;
        _tips.SetToolTip(_badgeBox, "在每个已置顶窗口左上角显示层号（点击穿透，不挡操作）");

        options.Controls.Add(_strictBox);
        options.Controls.Add(_autoStartBox);
        options.Controls.Add(_badgeBox);

        Control appearance = BuildAppearanceRow();
        Control scenes = _sceneRow; // 在构造函数里建好（ApplyMinimumWidth 要量它的首选宽度）

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        ConfigureButton(_unpinAllButton, "全部取消置顶", (_, _) => PinService.UnpinAll());
        ConfigureButton(_exitButton, "退出 Shadow", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        actions.Controls.Add(_unpinAllButton);
        actions.Controls.Add(_exitButton);

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Height = 34;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.ForeColor = SystemColors.GrayText;

        _bottom.Controls.Add(options, 0, 0);
        _bottom.Controls.Add(appearance, 0, 1);
        _bottom.Controls.Add(scenes, 0, 2);
        _bottom.Controls.Add(actions, 0, 3);
        _bottom.Controls.Add(_statusLabel, 0, 4);

        _appearanceTimer.Tick += (_, _) => ApplyAppearanceToSelection();
    }

    /// <summary>
    /// 外观行：透明度滑块 + 点击穿透。
    /// 作用对象是「列表里当前选中的那个窗口」而不是鼠标下的窗口 —— 面板本身要占着鼠标，
    /// 用滑块调鼠标底下那层窗口是调不到的。
    /// </summary>
    private Control BuildAppearanceRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };

        _opacityBar.Minimum = WindowAppearance.MinOpacity;
        _opacityBar.Maximum = WindowAppearance.MaxOpacity;
        _opacityBar.TickFrequency = 10;
        _opacityBar.SmallChange = 5;
        _opacityBar.LargeChange = 10;
        _opacityBar.Value = WindowAppearance.MaxOpacity;
        _opacityBar.Width = 170;
        _opacityBar.Height = 28;
        _opacityBar.Margin = new Padding(0, 2, 4, 0);
        _opacityBar.ValueChanged += (_, _) => OnAppearanceInputChanged();
        _tips.SetToolTip(_opacityBar, "调低会让窗口半透明（10%–100%），DirectComposition / UWP 窗口可能不支持");

        _opacityLabel.AutoSize = false;
        _opacityLabel.Width = 46;
        _opacityLabel.TextAlign = ContentAlignment.MiddleLeft;
        _opacityLabel.Margin = new Padding(0, 6, 12, 0);

        _clickThroughBox.Text = "点击穿透";
        _clickThroughBox.AutoSize = true;
        _clickThroughBox.Margin = new Padding(0, 6, 12, 0);
        _clickThroughBox.CheckedChanged += (_, _) => OnAppearanceInputChanged();
        _tips.SetToolTip(_clickThroughBox,
            "勾选后该窗口不再接收鼠标点击（事件会穿透到它下面的窗口）。" +
            "只能用主面板取消 —— 它自己已经点不动了");

        ConfigureButton(_appearanceResetButton, "重置外观", (_, _) => ResetAppearanceOfSelection());
        _tips.SetToolTip(_appearanceResetButton, "恢复到不透明、不穿透");

        row.Controls.Add(new Label
        {
            Text = "透明度",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0),
        });
        row.Controls.Add(_opacityBar);
        row.Controls.Add(_opacityLabel);
        row.Controls.Add(_clickThroughBox);
        row.Controls.Add(_appearanceResetButton);

        return row;
    }

    /// <summary>场景行：一键切换一整批窗口（含层级、透明度、位置）。</summary>
    private Control BuildSceneRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };

        _sceneBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _sceneBox.Width = 190;
        _sceneBox.Margin = new Padding(0, 3, 6, 0);
        _tips.SetToolTip(_sceneBox, "场景 = 一批窗口及其层级/外观/位置，点「应用」一键切换");

        ConfigureButton(_sceneSaveButton, "保存为场景…", (_, _) => SaveCurrentAsScene());
        ConfigureButton(_sceneApplyButton, "应用场景", (_, _) => ApplySelectedScene());
        ConfigureButton(_sceneDeleteButton, "删除场景", (_, _) => DeleteSelectedScene());
        ConfigureButton(_rulesButton, "自动规则…", (_, _) => OpenRuleEditor());
        _tips.SetToolTip(_rulesButton, "按进程名 / 标题的正则自动置顶或排除某些窗口");

        ConfigureButton(_moreButton, "更多 ▾", (_, _) => ShowMoreMenu());
        _tips.SetToolTip(_moreButton, "自定义热键、导入导出配置");

        row.Controls.Add(new Label { Text = "场景", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
        row.Controls.Add(_sceneBox);
        row.Controls.Add(_sceneSaveButton);
        row.Controls.Add(_sceneApplyButton);
        row.Controls.Add(_sceneDeleteButton);
        row.Controls.Add(_rulesButton);
        row.Controls.Add(_moreButton);

        return row;
    }

    private static void ConfigureButton(Button button, string text, EventHandler onClick)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Margin = new Padding(0, 0, 6, 0);
        button.Padding = new Padding(6, 2, 6, 2);
        button.Click += onClick;
    }

    // ------------------------------------------------------------ 列表刷新

    private void RefreshList()
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshList));
            return;
        }

        IntPtr selected = SelectedHandle();
        _list.BeginUpdate();

        try
        {
            _list.Items.Clear();

            foreach (PinnedWindow item in PinService.Items)
            {
                string state = !item.IsAlive
                    ? "窗口已关闭"
                    : WindowInfo.IsWindowTopmost(item.Handle) ? "置顶中" : "置顶丢失";

                var row = new ListViewItem(item.Rank.ToString()) { Tag = item.Handle };
                row.SubItems.Add(item.DisplayName);
                row.SubItems.Add($"{item.Snapshot.ProcessName} ({item.ProcessId})");
                row.SubItems.Add(state);

                if (item.Handle == selected) row.Selected = true;

                _list.Items.Add(row);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        LayoutColumns();

        string hotkeys = _hotkeySummary.Length > 0
            ? _hotkeySummary
            : $"{_toggleHotkeyText} / {_unpinAllHotkeyText}";
        _statusLabel.Text = PinService.PinnedCount == 0
            ? $"当前没有置顶窗口 · 点「拾取窗口」或按 {_toggleHotkeyText} 钉住鼠标下的窗口"
            : $"已置顶 {PinService.PinnedCount} 个窗口 · " +
              $"{(PinService.StrictMode ? "严格层级" : "宽松层级")} · 热键 {hotkeys} · " +
              $"数据目录 {ShadowPaths.DataDirectory}";

        UpdateButtons();
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        if (!Visible) return;

        // 标题/进程名会变，窗口会关，定期刷新快照
        PinService.RefreshSnapshots();
        RefreshList();
    }

    private void LayoutColumns()
    {
        if (_list.Columns.Count < 4) return;

        int fixedWidth = _list.Columns[0].Width + _list.Columns[2].Width + _list.Columns[3].Width;
        int available = _list.ClientSize.Width - fixedWidth - 4;
        _list.Columns[1].Width = Math.Max(160, available);
    }

    private void UpdateButtons()
    {
        bool hasSelection = SelectedHandle() != IntPtr.Zero;
        int rank = hasSelection ? PinService.RankOf(SelectedHandle()) : 0;

        _unpinButton.Enabled = hasSelection;
        _upButton.Enabled = hasSelection && rank > 1;
        _downButton.Enabled = hasSelection && rank > 0 && rank < PinService.PinnedCount;
        _topButton.Enabled = hasSelection && rank > 1;
        _unpinAllButton.Enabled = PinService.PinnedCount > 0;
        _restoreButton.Enabled = ProfileStore.Current.PinnedWindows.Count > 0;
        SyncAppearanceControls();
    }

    private IntPtr SelectedHandle()
    {
        if (_list.SelectedItems.Count == 0) return IntPtr.Zero;
        return _list.SelectedItems[0].Tag is IntPtr handle ? handle : IntPtr.Zero;
    }

    // ------------------------------------------------------------ 操作

    private void StartPicking()
    {
        if (_picker.IsRunning)
        {
            _picker.Stop();
            ShowPanel();
            return;
        }

        // 面板可能正压在鼠标下面，先藏起来再让拾取器接管
        Hide();
        _picker.Start();

        if (!_picker.IsRunning)
        {
            _tray.Notify("拾取器启动失败，请重试", ToolTipIcon.Warning);
            ShowPanel();
        }
    }

    /// <summary>拾取器选中了某个窗口：已置顶的取消，未置顶的钉住。</summary>
    private void OnPicked(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                _tray.Notify("拾取到的窗口已经不存在了", ToolTipIcon.Warning);
                return;
            }

            WindowInfo info = WindowInfo.Describe(hwnd);
            if (PinService.IsTracked(hwnd))
            {
                PinService.TryUnpin(hwnd, out string removedMessage);
                _tray.Notify($"已取消置顶：{info.DisplayName}（{removedMessage}）");
                return;
            }

            PinAdvice advice = PinAdvisor.Evaluate(info);
            if (!advice.CanPin)
            {
                _tray.Notify($"这个窗口不能置顶：{advice.Message}", ToolTipIcon.Warning);
                return;
            }

            bool ok = PinService.TryPin(hwnd, out string pinMessage);
            string text = ok && advice.HasWarning ? $"{pinMessage}　⚠ {advice.Warning}" : pinMessage;
            _tray.Notify(text, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("拾取窗口失败", ex);
        }
        finally
        {
            ShowPanel();
        }
    }

    private void OnPickCancelled()
    {
        Log.Info("用户取消了拾取");
        ShowPanel();
    }

    private void UnpinSelected()
    {
        IntPtr hwnd = SelectedHandle();
        if (hwnd == IntPtr.Zero) return;

        PinService.TryUnpin(hwnd, out _);
        RefreshList();
    }

    private void MoveSelected(int delta)
    {
        IntPtr hwnd = SelectedHandle();
        if (hwnd == IntPtr.Zero) return;

        int rank = PinService.RankOf(hwnd);
        if (rank == 0) return;

        PinService.MoveToRank(hwnd, rank + delta);
        RefreshList();
        SelectHandle(hwnd);
    }

    private void MoveSelectedToTop()
    {
        IntPtr hwnd = SelectedHandle();
        if (hwnd == IntPtr.Zero) return;

        PinService.MoveToRank(hwnd, 1);
        RefreshList();
        SelectHandle(hwnd);
    }

    private void SelectHandle(IntPtr handle)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is IntPtr value && value == handle)
            {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                return;
            }
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete)
        {
            UnpinSelected();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveSelected(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveSelected(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    // ------------------------------------------------------------ 拖拽排序

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is not ListViewItem item) return;
        if (_list.Items.Count < 2) return;

        _list.DoDragDrop(item, DragDropEffects.Move);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(typeof(ListViewItem)) == true
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(ListViewItem)) != true)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Move;

        Point client = _list.PointToClient(new Point(e.X, e.Y));
        int nearest = _list.InsertionMark.NearestIndex(client);

        if (nearest < 0)
        {
            // 落在空白处 → 放到最后一层
            _dropIndex = _list.Items.Count - 1;
            _list.InsertionMark.Index = -1;
            return;
        }

        bool after = _list.InsertionMark.AppearsAfterItem;
        _list.InsertionMark.Index = nearest;
        _dropIndex = Math.Clamp(after ? nearest + 1 : nearest, 0, _list.Items.Count - 1);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        _list.InsertionMark.Index = -1;

        if (e.Data?.GetData(typeof(ListViewItem)) is not ListViewItem dragged) return;
        if (dragged.Tag is not IntPtr handle) return;

        int target = _dropIndex;
        _dropIndex = -1;

        if (target < 0) return;

        int current = PinService.RankOf(handle) - 1;
        if (current == target) return;

        // MoveToRank 的语义是「移完之后的最终下标 + 1」，正好对上拖拽的落点
        PinService.MoveToRank(handle, target + 1);
        RefreshList();
        SelectHandle(handle);
    }

    // ------------------------------------------------------------ 外观：透明度与点击穿透

    /// <summary>滑块/复选框被改动：起一个 250ms 的合并计时器，拖动过程中不打 Win32 调用。</summary>
    private void OnAppearanceInputChanged()
    {
        if (_syncingAppearance) return;

        _opacityLabel.Text = $"{_opacityBar.Value}%";

        // 组件在初始化时会触发一次 ValueChanged，此时还没有句柄/没有选中项
        if (!IsHandleCreated) return;

        _appearanceTimer.Stop();
        _appearanceTimer.Start();
    }

    private void ApplyAppearanceToSelection()
    {
        _appearanceTimer.Stop();

        IntPtr hwnd = SelectedHandle();
        if (hwnd == IntPtr.Zero) return;

        bool clickThrough = _clickThroughBox.Checked;
        bool clickThroughJustEnabled = clickThrough && !_lastClickThrough;

        if (!PinService.SetAppearance(hwnd, _opacityBar.Value, clickThrough, out string message))
        {
            // 失败只在原因变化时提示一次（拖动时每 250ms 都会重试）
            if (message != _lastAppearanceFailure)
            {
                _lastAppearanceFailure = message;
                _tray.Notify(message, ToolTipIcon.Warning);
            }

            _lastClickThrough = clickThrough;
            SyncAppearanceControls();
            return;
        }

        _lastAppearanceFailure = string.Empty;
        _lastClickThrough = clickThrough;

        // 成功不弹气泡：透明度在面板上（数值标签 + 列表的「外观」列）都看得到，
        // 而拖动滑块时「每改一次弹一条系统提示」会非常吵。
        if (clickThroughJustEnabled)
        {
            // 这是唯一一个「用户可能把自己关在门外」的操作，必须把出口说清楚
            _tray.Notify($"「{WindowInfo.Describe(hwnd).DisplayName}」已设为点击穿透：" +
                         "它不再接收鼠标操作，需要在本面板选中它后取消勾选。", ToolTipIcon.Warning);
        }
    }

    private void ResetAppearanceOfSelection()
    {
        IntPtr hwnd = SelectedHandle();
        if (hwnd == IntPtr.Zero) return;

        _appearanceTimer.Stop();
        bool ok = PinService.SetAppearance(
            hwnd, WindowAppearance.MaxOpacity, clickThrough: false, out string message);

        _lastClickThrough = false;
        _lastAppearanceFailure = string.Empty;
        SyncAppearanceControls();

        // 成功不弹气泡：滑块会跳回 100%、勾选会消失，面板本身已经说清楚了
        if (!ok) _tray.Notify(message, ToolTipIcon.Warning);
    }

    /// <summary>
    /// 仅供自检：选中指定窗口并按真实路径应用一次外观（拖动滑块走的就是这同一条路），
    /// 用来回归「调整透明度不得弹系统提示」这条用户反馈（m02923）。
    /// 产品路径由 250ms 合并计时器驱动，这里直接应用是为了让自检结果稳定。
    /// </summary>
    internal void ApplyAppearanceForTest(IntPtr hwnd, int opacity, bool clickThrough)
    {
        SelectHandle(hwnd);

        _syncingAppearance = true;
        try
        {
            _opacityBar.Value = Math.Clamp(
                opacity, WindowAppearance.MinOpacity, WindowAppearance.MaxOpacity);
            _clickThroughBox.Checked = clickThrough;
        }
        finally
        {
            _syncingAppearance = false;
        }

        ApplyAppearanceToSelection();
    }

    /// <summary>把控件状态同步成「选中窗口的现状」（没有选中就禁用整行）。</summary>
    private void SyncAppearanceControls()
    {
        IntPtr hwnd = SelectedHandle();
        PinnedWindow? item = hwnd == IntPtr.Zero ? null : PinService.Find(hwnd);

        _syncingAppearance = true;
        try
        {
            bool enabled = item is not null;
            _opacityBar.Enabled = enabled;
            _clickThroughBox.Enabled = enabled;
            _appearanceResetButton.Enabled = enabled;

            if (item is null)
            {
                _opacityBar.Value = WindowAppearance.MaxOpacity;
                _clickThroughBox.Checked = false;
                _opacityLabel.Text = "--";
                _lastClickThrough = false;
                _lastAppearanceFailure = string.Empty;
                return;
            }

            _opacityBar.Value = Math.Clamp(
                item.Opacity, WindowAppearance.MinOpacity, WindowAppearance.MaxOpacity);
            _clickThroughBox.Checked = item.ClickThrough;
            _opacityLabel.Text = $"{item.Opacity}%";

            // 同步时不要留下「刚打开穿透」的假边沿，否则切一下选中项就会弹提示
            _lastClickThrough = item.ClickThrough;
            _lastAppearanceFailure = string.Empty;
        }
        finally
        {
            _syncingAppearance = false;
        }
    }

    // ------------------------------------------------------------ 场景

    private void RefreshScenes()
    {
        string? selected = _sceneBox.SelectedItem as string;
        IReadOnlyList<SceneDto> scenes = SceneManager.All;

        _sceneBox.BeginUpdate();
        try
        {
            _sceneBox.Items.Clear();
            foreach (SceneDto scene in scenes) _sceneBox.Items.Add(scene.Name);

            if (selected is not null)
            {
                int index = _sceneBox.Items.IndexOf(selected);
                if (index >= 0) _sceneBox.SelectedIndex = index;
            }

            if (_sceneBox.SelectedIndex < 0 && _sceneBox.Items.Count > 0) _sceneBox.SelectedIndex = 0;
        }
        finally
        {
            _sceneBox.EndUpdate();
        }

        _sceneApplyButton.Enabled = _sceneBox.Items.Count > 0;
        _sceneDeleteButton.Enabled = _sceneBox.Items.Count > 0;
    }

    private void SaveCurrentAsScene()
    {
        if (PinService.PinnedCount == 0)
        {
            _tray.Notify("当前没有置顶窗口，保存出来的会是空场景。", ToolTipIcon.Warning);
            return;
        }

        string? name = InputDialog.Ask(this, "保存场景",
            $"把当前 {PinService.PinnedCount} 个置顶窗口（含层级、透明度、位置）保存为场景：");

        if (name is null) return;

        SceneDto scene = SceneManager.Capture(name);
        RefreshScenes();
        _tray.Notify($"已保存场景「{scene.Name}」（{scene.Windows.Count} 个窗口）。");
    }

    private void ApplySelectedScene()
    {
        if (_sceneBox.SelectedItem is not string name) return;

        int restored = SceneManager.Apply(name, out List<string> missing);

        if (missing.Count > 0)
        {
            _tray.Notify($"场景「{name}」已切换：恢复 {restored} 个窗口，" +
                         $"{missing.Count} 个没找到（{string.Join("、", missing.Take(2))}…）。",
                ToolTipIcon.Warning);
        }
        else
        {
            _tray.Notify($"场景「{name}」已切换：恢复 {restored} 个窗口。");
        }
    }

    private void DeleteSelectedScene()
    {
        if (_sceneBox.SelectedItem is not string name) return;

        if (Dialogs.Message(this, $"删除场景「{name}」？", buttons: MessageBoxButtons.OKCancel,
                icon: MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        SceneManager.Delete(name);
        RefreshScenes();
        _tray.Notify($"已删除场景「{name}」。");
    }

    // ------------------------------------------------------------ 规则与设置入口

    private void OpenRuleEditor()
    {
        using var form = new RuleForm();
        Dialogs.Show(this, form);

        // 规则可能改了外观（透明度/穿透）或置顶了窗口，回来刷新一遍
        RefreshList();
    }

    private void ShowMoreMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("自定义全局热键…", null, (_, _) =>
        {
            using var form = new HotkeyForm();
            if (Dialogs.Show(this, form) == DialogResult.OK)
            {
                _tray.Notify("热键设置已保存，重启 Shadow 后生效。");
            }
        });

        menu.Items.Add(new ToolStripSeparator());

        // 命令行接口需要重启才生效：管道服务端在启动时创建，运行中销毁它会留下
        // 半连接状态（正在等回应的 CLI 会直接失败），不如诚实地告诉用户重启一次。
        var cliItem = new ToolStripMenuItem("启用命令行接口（命名管道）")
        {
            CheckOnClick = true,
            Checked = ProfileStore.Current.EnableCli,
        };

        cliItem.CheckedChanged += (_, _) =>
        {
            ProfileStore.Current.EnableCli = cliItem.Checked;
            ProfileStore.SaveCurrent();
            _tray.Notify(cliItem.Checked
                ? "命令行接口已启用，重启 Shadow 后生效。"
                : "命令行接口已关闭，重启 Shadow 后生效。");
        };

        menu.Items.Add(cliItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("导出配置…", null, (_, _) => ExportProfile());
        menu.Items.Add("导入配置…", null, (_, _) => ImportProfile());
        menu.Items.Add("打开数据目录", null, (_, _) => OpenDataDirectory());

        menu.Show(_moreButton, new Point(0, _moreButton.Height));
    }

    private void ExportProfile()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出配置",
            FileName = "shadow-config.json",
            Filter = "JSON 配置|*.json|所有文件|*.*",
            InitialDirectory = ShadowPaths.DataDirectory,
        };

        if (Dialogs.Show(this, dialog) != DialogResult.OK) return;

        try
        {
            // 先确保当前状态落盘，导出的才是「现在这份」配置
            ProfileStore.SaveCurrent();
            File.Copy(ProfileStore.FilePath, dialog.FileName, overwrite: true);
            Log.Info($"已导出配置到 {dialog.FileName}");
            _tray.Notify("配置已导出。");
        }
        catch (Exception ex)
        {
            Log.Error("导出配置失败", ex);
            Dialogs.Message(this, $"导出失败：{ex.Message}", icon: MessageBoxIcon.Warning);
        }
    }

    private void ImportProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入配置",
            Filter = "JSON 配置|*.json|所有文件|*.*",
            InitialDirectory = ShadowPaths.DataDirectory,
        };

        if (Dialogs.Show(this, dialog) != DialogResult.OK) return;

        if (Dialogs.Message(this,
                "导入会覆盖当前配置（窗口列表、场景、规则、热键），且需要重启 Shadow 才能生效。继续？",
                buttons: MessageBoxButtons.OKCancel, icon: MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        try
        {
            // 覆盖前留一份备份：导入的可能是别人机器上的配置，出问题还能退回来
            if (File.Exists(ProfileStore.FilePath))
            {
                File.Copy(ProfileStore.FilePath, ProfileStore.FilePath + ".bak", overwrite: true);
            }

            File.Copy(dialog.FileName, ProfileStore.FilePath, overwrite: true);
            ProfileStore.SetCurrent(ProfileStore.Load());
            Log.Info($"已从 {dialog.FileName} 导入配置（原配置备份为 config.json.bak）");

            Dialogs.Message(this, "配置已导入。重启 Shadow 后完全生效。");
        }
        catch (Exception ex)
        {
            Log.Error("导入配置失败", ex);
            Dialogs.Message(this, $"导入失败：{ex.Message}", icon: MessageBoxIcon.Warning);
        }
    }

    private void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(ShadowPaths.DataDirectory);

            // 用 explorer.exe 打开目录，而不是把「目录」本身当程序启动：
            // ShellExecute 走「目录 → 默认程序」这条关联路径时，本机实测直接抛
            // Win32Exception(1223)「操作已被用户取消」，用户看到的是「打开失败」。
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ShadowPaths.DataDirectory}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开数据目录失败", ex);
            Dialogs.Message(this,
                $"打开数据目录失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"目录：{ShadowPaths.DataDirectory}",
                icon: MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------ 开关与生命周期

    private void ApplyProfile()
    {
        Profile profile = ProfileStore.Current;

        _suppressEvents = true;
        try
        {
            _strictBox.Checked = PinService.StrictMode;
            _autoStartBox.Checked = profile.AutoStart;
            _badgeBox.Checked = profile.ShowRankBadges;
            Size = new Size(
                Math.Max(MinimumSize.Width, profile.PanelWidth),
                Math.Max(MinimumSize.Height, profile.PanelHeight));
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnStrictChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents) return;

        PinService.StrictMode = _strictBox.Checked;
        ProfileStore.Current.StrictMode = _strictBox.Checked;
        ProfileStore.SaveCurrent();

        Log.Info($"严格层级模式 = {_strictBox.Checked}");
        if (_strictBox.Checked) PinService.ReapplyAll("切换到严格模式");

        RefreshList();
    }

    private void OnBadgeChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents) return;

        ProfileStore.Current.ShowRankBadges = _badgeBox.Checked;
        ProfileStore.SaveCurrent();
        RankBadgeVisibilityChanged?.Invoke(_badgeBox.Checked);

        Log.Info($"层级徽标 = {_badgeBox.Checked}");
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents) return;

        bool wanted = _autoStartBox.Checked;
        UseWaitCursor = true;

        try
        {
            bool ok = wanted
                ? AutoStartManager.Enable(out string message)
                : AutoStartManager.Disable(out message);

            if (!ok)
            {
                // 设置失败就把勾选状态改回去，别让界面撒谎
                _suppressEvents = true;
                _autoStartBox.Checked = !wanted;
                _suppressEvents = false;
            }

            ProfileStore.Current.AutoStart = _autoStartBox.Checked;
            ProfileStore.SaveCurrent();
            _tray.Notify(message, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExit)
        {
            // 常驻工具：点关闭只是收起面板
            e.Cancel = true;
            DropTemporaryTopMost();
            Hide();
            return;
        }

        PinService.Changed -= RefreshList;
        _refreshTimer.Stop();
        _picker.Stop();
        _appearanceTimer.Stop();
        _topMostTimer.Stop();

        Profile profile = ProfileStore.Current;
        profile.PanelWidth = Width;
        profile.PanelHeight = Height;
        ProfileStore.Save(profile);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PinService.Changed -= RefreshList;
            _refreshTimer.Tick -= OnRefreshTick;
            _refreshTimer.Dispose();
            _topMostTimer.Tick -= OnTopMostTimeout;
            _topMostTimer.Dispose();
            _appearanceTimer.Dispose();
            _picker.Dispose();
        }

        base.Dispose(disposing);
    }
}
