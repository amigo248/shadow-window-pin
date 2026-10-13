using Shadow.Core;
using Shadow.Persistence;

namespace Shadow.App;

/// <summary>
/// 自定义全局热键。
///
/// 一个诚实的限制：RegisterHotKey 是在启动时注册的，改完必须重启 Shadow 才生效 ——
/// 与其偷偷在运行中注销又重注册（期间热键会短暂失灵，还可能注册不回来），
/// 不如在界面上直说。留空表示「用内置候选表」。
/// </summary>
internal sealed class HotkeyForm : Form
{
    private readonly TextBox _toggle = new();
    private readonly TextBox _unpinAll = new();
    private readonly Label _status = new();

    internal HotkeyForm()
    {
        Text = "Shadow · 自定义热键";
        Icon = SystemIcons.Application;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 236);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(12, 12, 12, 0),
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        grid.Controls.Add(NewLabel("钉住 / 取消窗口"), 0, 0);
        grid.Controls.Add(PrepareCapture(_toggle, "Ctrl+Alt+T"), 1, 0);

        grid.Controls.Add(NewLabel("取消全部置顶"), 0, 1);
        grid.Controls.Add(PrepareCapture(_unpinAll, "Ctrl+Alt+Shift+T"), 1, 1);

        var note = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 108,
            ForeColor = SystemColors.GrayText,
            Text = "点进输入框后直接按组合键即可，Backspace 清空（清空 = 用内置候选表）。" +
                   Environment.NewLine +
                   "保存后需要重启 Shadow 才生效。" + Environment.NewLine +
                   $"钉住：{string.Join(" → ", HotkeyDefaults.ToggleCandidateTexts)}" + Environment.NewLine +
                   $"取消全部：{string.Join(" → ", HotkeyDefaults.UnpinAllCandidateTexts)}",
        };

        grid.Controls.Add(note, 0, 2);
        grid.SetColumnSpan(note, 2);

        var commands = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(0, 4, 12, 0),
        };

        var save = new Button { Text = "保存", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "恢复默认", AutoSize = true };
        reset.Click += (_, _) =>
        {
            _toggle.Text = string.Empty;
            _unpinAll.Text = string.Empty;
            _status.Text = "已清空，改为使用内置候选表。";
        };

        save.Click += (_, _) => Save();

        commands.Controls.Add(cancel);
        commands.Controls.Add(save);
        commands.Controls.Add(reset);

        Controls.Add(grid);
        Controls.Add(commands);
        Controls.Add(BuildStatus());

        AcceptButton = save;
        CancelButton = cancel;

        Load += (_, _) =>
        {
            Profile profile = ProfileStore.Current;
            _toggle.Text = profile.ToggleHotkeyOverride;
            _unpinAll.Text = profile.UnpinAllHotkeyOverride;
        };
    }

    private Control BuildStatus()
    {
        _status.Dock = DockStyle.Bottom;
        _status.Height = 26;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = SystemColors.GrayText;
        _status.Text = "留空使用内置组合。按下的组合若被别的程序占用，启动时会自动退到下一个候选。";
        return _status;
    }

    private static Label NewLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 0, 0) };

    private TextBox PrepareCapture(TextBox box, string placeholder)
    {
        box.Dock = DockStyle.Fill;
        box.ReadOnly = true;
        box.ShortcutsEnabled = false;
        box.PlaceholderText = placeholder;
        box.Margin = new Padding(0, 6, 0, 0);
        box.KeyDown += OnCaptureKeyDown;
        box.Enter += (_, _) => _status.Text = "请按下组合键（至少一个 Ctrl / Alt / Shift / Win 加一个按键）。";
        return box;
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
        {
            box.Text = string.Empty;
            _status.Text = "已清空。";
            return;
        }

        // 只按住修饰键时什么都不做，等真正的按键
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;

        // Alt 组合会让菜单抢占按键，这里统一用系统修饰键的当前状态判断
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0) modifiers |= HotkeyModifiers.Control;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0) modifiers |= HotkeyModifiers.Alt;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0) modifiers |= HotkeyModifiers.Shift;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0)
        {
            modifiers |= HotkeyModifiers.Win;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            _status.Text = "必须包含 Ctrl / Alt / Shift / Win 之一，否则会抢走正常输入。";
            return;
        }

        box.Text = HotkeyWindow.Describe(modifiers, e.KeyCode);
        _status.Text = $"已设定为 {box.Text}。保存后重启 Shadow 生效。";
    }

    private void Save()
    {
        Profile profile = ProfileStore.Current;

        profile.ToggleHotkeyOverride = Normalize(_toggle.Text, out string toggleText);
        profile.UnpinAllHotkeyOverride = Normalize(_unpinAll.Text, out string unpinText);

        ProfileStore.Save(profile);
        _status.Text = "已保存。";
        Log.Info($"自定义热键：钉住={Describe(toggleText)}，取消全部={Describe(unpinText)}");

        static string Normalize(string text, out string description)
        {
            string value = text.Trim();
            description = value.Length == 0 ? "内置候选" : value;
            return value;
        }

        static string Describe(string value) => value.Length == 0 ? "（内置候选）" : value;
    }
}
