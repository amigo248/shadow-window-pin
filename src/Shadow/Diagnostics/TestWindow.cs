namespace Shadow.Diagnostics;

/// <summary>
/// 自检靶窗口：由 --selftest 以子进程方式启动，
/// 提供一个「跨进程、真实存在」的窗口用于验证置顶链路。
/// </summary>
internal sealed class TestWindow : Form
{
    internal const string SelfTestTitle = "Shadow Self-Test Window";

    private readonly System.Windows.Forms.Timer _autoCloseTimer;

    internal TestWindow()
    {
        Text = SelfTestTitle;
        Name = "ShadowSelfTestWindow";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 260);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;

        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 11f),
            Text = "Shadow 自检靶窗口" + Environment.NewLine + Environment.NewLine +
                   "本窗口由 --selftest 自动创建，" + Environment.NewLine +
                   "最长 60 秒后自动关闭。",
        });

        // 保险丝：万一父进程崩溃，这个窗口也不会永远留在屏幕上
        _autoCloseTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            Close();
        };
        _autoCloseTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoCloseTimer.Stop();
            _autoCloseTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
