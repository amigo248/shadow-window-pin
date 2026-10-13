using Shadow.Core;

namespace Shadow.App;

/// <summary>托盘图标 + 右键菜单。</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string Title = "Shadow — 窗口置顶工具";

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openPanelItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _unpinAllItem;
    private readonly ToolStripMenuItem _aboutItem;
    private readonly ToolStripMenuItem _exitItem;

    private bool _disposed;

    internal event EventHandler? OpenPanelRequested;
    internal event EventHandler? ToggleRequested;
    internal event EventHandler? UnpinAllRequested;
    internal event EventHandler? AboutRequested;
    internal event EventHandler? ExitRequested;

    internal TrayIcon()
    {
        _statusItem = new ToolStripMenuItem("当前没有置顶窗口") { Enabled = false };

        _openPanelItem = new ToolStripMenuItem("打开主面板…");
        _openPanelItem.Click += (_, _) => Raise(OpenPanelRequested);

        _toggleItem = new ToolStripMenuItem("钉住 / 取消鼠标下的窗口")
        {
            ShowShortcutKeys = false,
        };
        _toggleItem.Click += (_, _) => Raise(ToggleRequested);

        _unpinAllItem = new ToolStripMenuItem("取消全部置顶");
        _unpinAllItem.Click += (_, _) => Raise(UnpinAllRequested);

        _aboutItem = new ToolStripMenuItem("关于 Shadow");
        _aboutItem.Click += (_, _) => Raise(AboutRequested);

        _exitItem = new ToolStripMenuItem("退出（还原全部置顶）");
        _exitItem.Click += (_, _) => Raise(ExitRequested);

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_openPanelItem);
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(_unpinAllItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_aboutItem);
        _menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = Title,
            Icon = SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = false,
        };

        // 双击托盘 = 打开主面板（面板里才有层级调整，比「钉一下」更常用）
        _notifyIcon.DoubleClick += (_, _) => Raise(OpenPanelRequested);
    }

    private void Raise(EventHandler? handler) => handler?.Invoke(this, EventArgs.Empty);

    /// <summary>把实际注册到的热键写进菜单文本（默认组合可能被别的程序占用）。</summary>
    internal void SetHotkeyTexts(string toggle, string unpinAll)
    {
        _toggleItem.Text = $"钉住 / 取消鼠标下的窗口        {toggle}";
        _unpinAllItem.Text = $"取消全部置顶        {unpinAll}";
    }

    internal void Show()
    {
        _notifyIcon.Visible = true;
        Log.Info("托盘图标已显示");
    }

    internal void SetStatus(int pinnedCount)
    {
        _statusItem.Text = pinnedCount switch
        {
            0 => "当前没有置顶窗口",
            1 => "当前置顶：1 个窗口",
            _ => $"当前置顶：{pinnedCount} 个窗口",
        };

        // NotifyIcon.Text 上限 63 字符，越界会抛异常
        string tooltip = pinnedCount == 0 ? Title : $"{Title}（已置顶 {pinnedCount}）";
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    /// <summary>
    /// 已经弹过多少个气泡。只给自检用：用来回归「拖动透明度滑块不该刷屏」
    /// —— 用户反馈 m02923 就是被这个吵到的。
    /// </summary>
    internal int NotifyCount { get; private set; }

    internal void Notify(string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        NotifyCount++;

        string text = message.Length <= 255 ? message : message[..255];
        _notifyIcon.ShowBalloonTip(3000, "Shadow", text, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
