using System.Runtime.InteropServices;
using Shadow.Core;

namespace Shadow.App;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,

    /// <summary>按住不放时不重复触发 —— 全局热键必须带这个，否则长按会疯狂刷屏。</summary>
    NoRepeat = 0x4000,
}

/// <summary>
/// 消息泵 + 全局热键接收器。
///
/// 这是一个「永不显示」的窗体：SetVisibleCore 被覆写为强制 false，
/// 但它依然是 Application.Run 的主窗体，因此拥有消息循环与窗口句柄，
/// 可以接收 WM_HOTKEY。这样托盘模式下不需要任何可见窗口。
/// </summary>
internal sealed class HotkeyWindow : Form
{
    internal const int IdTogglePin = 0x5101;
    internal const int IdUnpinAll = 0x5102;

    /// <summary>Ctrl+Alt+1…9「指定层级」的 id 区间起点。</summary>
    internal const int IdRankFirst = 0x5110;

    /// <summary>可用的层级热键数量（1…9 层）。</summary>
    internal const int RankCount = 9;

    internal static int RankId(int rank) => IdRankFirst + (rank - 1);

    /// <summary>id 是否属于层级热键区间；是则给出 1 起的层号。</summary>
    internal static bool TryGetRank(int id, out int rank)
    {
        rank = id - IdRankFirst + 1;
        return rank >= 1 && rank <= RankCount;
    }

    private readonly Dictionary<int, string> _registered = new();

    internal event EventHandler? TogglePinRequested;
    internal event EventHandler? UnpinAllRequested;

    /// <summary>按下 Ctrl+Alt+数字：把鼠标下的窗口设为第 N 层。</summary>
    internal event Action<int>? RankRequested;

    internal HotkeyWindow()
    {
        Text = "Shadow Hotkey Sink";
        Name = "HotkeyWindow";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
    }

    /// <summary>彻底阻止窗体显示，但保证句柄被创建。</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(false);
    }

    internal bool Register(
        int id, HotkeyModifiers modifiers, Keys key, out int win32Error, out string description)
    {
        description = Describe(modifiers, key);

        if (_registered.ContainsKey(id))
        {
            win32Error = 0;
            return true;
        }

        bool ok = NativeMethods.RegisterHotKey(
            Handle, id, (uint)(modifiers | HotkeyModifiers.NoRepeat), (uint)key);

        if (ok)
        {
            win32Error = 0;
            _registered[id] = description;
            Log.Info($"已注册全局热键 id={id} {description}");
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        Log.Warn($"注册全局热键失败 id={id} {description} Win32Error={win32Error}");
        return false;
    }

    internal void UnregisterAll()
    {
        foreach (int id in _registered.Keys.ToArray())
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }
        _registered.Clear();
    }

    internal static string Describe(HotkeyModifiers modifiers, Keys key)
    {
        var parts = new List<string>(5);

        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            switch (id)
            {
                case IdTogglePin:
                    TogglePinRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case IdUnpinAll:
                    UnpinAllRequested?.Invoke(this, EventArgs.Empty);
                    break;

                default:
                    if (TryGetRank(id, out int rank))
                    {
                        RankRequested?.Invoke(rank);
                    }
                    else
                    {
                        Log.Warn($"收到未知热键消息 id={id}");
                    }

                    break;
            }
        }

        base.WndProc(ref m);
    }
}
