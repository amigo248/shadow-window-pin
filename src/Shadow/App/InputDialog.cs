namespace Shadow.App;

/// <summary>
/// 一个极简的输入框（「给场景起个名字」这种一步小交互）。
///
/// 为什么不引 Microsoft.VisualBasic 的 InputBox：那个对话框的按钮是英文的、
/// 尺寸和字体都不受我们控制，在这个全中文的界面里很突兀。自己写 30 行更省事。
/// </summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _input = new();

    private InputDialog(string title, string prompt, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(380, 116);

        var label = new Label
        {
            Text = prompt,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 38,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 8, 10, 0),
        };

        _input.Dock = DockStyle.Top;
        _input.Text = initial;
        _input.Margin = new Padding(10);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(0, 6, 10, 0),
        };

        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
        host.Controls.Add(_input);

        Controls.Add(host);
        Controls.Add(label);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;

        Shown += (_, _) =>
        {
            _input.SelectAll();
            _input.Focus();
        };
    }

    /// <summary>弹出输入框；用户取消或输入为空时返回 null。</summary>
    internal static string? Ask(IWin32Window? owner, string title, string prompt, string initial = "")
    {
        using var dialog = new InputDialog(title, prompt, initial);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;

        string text = dialog._input.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
