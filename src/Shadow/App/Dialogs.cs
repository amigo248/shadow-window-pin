using Shadow.Core;

namespace Shadow.App;

/// <summary>
/// 模态对话框的「临时置顶」守护。
///
/// 为什么必须有它（2026-10-06 实机踩到的死局）：
/// 主面板被唤出时会临时置顶（见 MainForm.ShowPanel），屏幕上本来就可能有用户自己置顶的
/// 窗口。这时如果从面板里打开一个模态对话框，而对话框不是置顶的，它会被压到面板下面 ——
/// 但 ShowDialog 已经按规矩把 owner（主面板）EnableWindow(false) 禁用了：
/// 于是面板点不动、对话框又看不见，用户除了杀进程没别的办法。
/// 实测现场：点「自动规则…」后，规则对话框 783,743 776x509 完全落在置顶的主面板
/// 782,671 778x654 里面，主面板 IsWindowEnabled=False，GetLastActivePopup 能找到它但看不见。
///
/// 做法：对话框运行期间用 WinForms 定时器轮询 GetLastActivePopup(owner)，一旦出现
/// 「不是 owner 自己」的弹出窗口就把它临时设为置顶。
/// 用 System.Windows.Forms.Timer 而不是后台线程，是因为模态对话框自带消息循环，
/// WM_TIMER 照样会被泵到 —— 我们才可能在 ShowDialog 阻塞调用「内部」完成提升。
/// </summary>
internal sealed class TopMostPopupGuard : IDisposable
{
    private readonly Control _owner;
    private readonly System.Windows.Forms.Timer _timer;

    internal TopMostPopupGuard(Control? owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _timer = new System.Windows.Forms.Timer { Interval = 60 };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 找到并提升过一次就收工：同一个对话框不需要每 60 毫秒摸一遍。
        if (Promote(_owner)) _timer.Stop();
    }

    /// <summary>把 owner 当前挂着的模态对话框提到置顶层；找到（或确认没有）时返回 true。</summary>
    internal static bool Promote(Control owner)
    {
        if (!owner.IsHandleCreated) return false;

        IntPtr popup = NativeMethods.GetLastActivePopup(owner.Handle);
        return PromotePopup(popup, owner.Handle);
    }

    /// <summary>
    /// 把 popup 设为置顶。popup 非法、或就是 owner 自己（说明没有对话框）时返回 false。
    /// 单独拆出来是为了让自检能直接喂句柄，不依赖「谁被激活了」这种时序。
    /// </summary>
    internal static bool PromotePopup(IntPtr popup, IntPtr ownerHandle)
    {
        if (popup == IntPtr.Zero || popup == ownerHandle) return false;
        if (!NativeMethods.IsWindow(popup)) return false;

        if (!WindowInfo.IsWindowTopmost(popup))
        {
            NativeMethods.SetWindowPos(
                popup,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);

            Log.Info($"模态对话框已临时置顶：0x{popup.ToInt64():X}");
        }

        return true;
    }

    public void Dispose()
    {
        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer.Dispose();
    }
}

/// <summary>
/// 项目里模态对话框的统一入口。
///
/// 直接调 MessageBox.Show / Form.ShowDialog / FileDialog.ShowDialog 的话，只要主面板
/// 或用户置顶的窗口压在上面，就会出现「点不动又看不见」的死局（见 TopMostPopupGuard
/// 注释里的实测现场），所以这里一律套上守护。
/// </summary>
internal static class Dialogs
{
    internal static DialogResult Message(
        Control? owner,
        string text,
        string caption = "Shadow",
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        if (owner is null || !owner.IsHandleCreated)
        {
            // 没有 owner 就无从判断弹出窗口是谁，只能交给系统（启动期错误对话框走这条）。
            return MessageBox.Show(text, caption, buttons, icon);
        }

        using var guard = new TopMostPopupGuard(owner);
        return MessageBox.Show(owner, text, caption, buttons, icon);
    }

    /// <summary>我们自己的对话框窗体（规则编辑器、热键设置、输入框…）。</summary>
    internal static DialogResult Show(Control owner, Form dialog)
    {
        using var guard = new TopMostPopupGuard(owner);
        return dialog.ShowDialog(owner);
    }

    /// <summary>系统文件对话框（打开/保存）。它跑在本进程的线程上，所以同样能被提升。</summary>
    internal static DialogResult Show(Control owner, FileDialog dialog)
    {
        using var guard = new TopMostPopupGuard(owner);
        return dialog.ShowDialog(owner);
    }
}
