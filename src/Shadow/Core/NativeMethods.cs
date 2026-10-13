using System.Runtime.InteropServices;
using System.Text;

namespace Shadow.Core;

/// <summary>
/// Shadow 用到的全部 Win32 API。
///
/// 安全原则（贯穿整个项目）：
///   只使用 user32 / dwmapi / kernel32 的常规接口，
///   绝不做 DLL 注入、绝不安插全局钩子（SetWindowsHookEx）、绝不 hook 目标进程的任何函数。
///   这样可以最大程度避免杀软误报与反作弊冲突。
/// </summary>
internal static class NativeMethods
{
    // ---------------- 窗口样式 ----------------
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    internal const long WS_EX_TOPMOST = 0x00000008L;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_LAYERED = 0x00080000L;
    internal const long WS_EX_TRANSPARENT = 0x00000020L;
    internal const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;

    /// <summary>WS_BORDER | WS_DLGFRAME：有它说明窗口带标题栏/边框（无边框全屏游戏都没有）。</summary>
    internal const long WS_CAPTION = 0x00C00000L;

    // ---------------- SetWindowPos 的特殊 insertAfter ----------------
    /// <summary>层带最前（= 0）：只把窗口抬到同层带的最上面，不给它置顶属性。</summary>
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    // ---------------- SetWindowPos 标志 ----------------
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>改完扩展样式后强制刷新非客户区（分层/点击穿透生效需要）。</summary>
    internal const uint SWP_FRAMECHANGED = 0x0020;

    // ---------------- GetWindow 命令 ----------------
    internal const uint GW_HWNDPREV = 3;
    internal const uint GW_HWNDNEXT = 2;

    // ---------------- GetAncestor 标志 ----------------
    internal const uint GA_ROOT = 2;

    // ---------------- DWM ----------------
    internal const int DWMWA_CLOAKED = 14;

    // ---------------- 消息 ----------------
    internal const int WM_HOTKEY = 0x0312;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>
    /// 该窗口最近一次激活的弹出窗口（模态对话框 / MessageBox / 文件对话框都算）。
    /// 没有弹出窗口时返回 hWnd 自己 —— 用它判断「现在有没有对话框压在这个窗口上」。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetLastActivePopup(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    /// <summary>按当前进程位数自动选择 32/64 位实现。</summary>
    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // ---------------- WinEvent 事件钩子 ----------------
    // 只用 WINEVENT_OUTOFCONTEXT：回调走消息队列，不注入目标进程、不打断它的线程。
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    internal const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    internal const uint EVENT_OBJECT_DESTROY = 0x8001;
    internal const uint EVENT_OBJECT_SHOW = 0x8002;
    internal const uint EVENT_OBJECT_HIDE = 0x8003;
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    /// <summary>OBJID_WINDOW：只关心窗口本身的事件，忽略菜单/滚动条等子对象。</summary>
    internal const int OBJID_WINDOW = 0;

    /// <summary>
    /// OBJID_CLIENT（0xFFFFFFFC）：窗口内部的客户区对象（无障碍/UIA 元素等）。
    /// 浏览器、游戏这类程序每秒都在销毁重建这类子对象，它们触发的
    /// EVENT_OBJECT_DESTROY 绝不能当成「整扇窗口被销毁」—— 见 WinEventMonitor.ShouldHandleEvent。
    /// </summary>
    internal const int OBJID_CLIENT = -4;

    internal delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>Z 序最顶端的窗口（topmost 层带的头部）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 把窗口切到前台。注意：调用方自己得有 SetForegroundWindow 权限（通常是「当前就是前台进程」），
    /// 否则系统只会让任务栏图标闪烁而返回 false。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---------------- 显示器与独占全屏判定（M4） ----------------

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;
    internal const uint ENUM_REGISTRY_SETTINGS = 0xFFFFFFFE;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>
    /// MONITORINFOEXW。
    /// <para>
    /// <c>CharSet = CharSet.Unicode</c> 不能省：ByValTStr 字段按 ANSI 计数的话
    /// <c>Marshal.SizeOf</c> 会算出 72 字节（应为 104），而 GetMonitorInfoW 校验 cbSize，
    /// 直接返回失败并给出 err=87（ERROR_INVALID_PARAMETER）——结构体大小差 32 字节，
    /// 错误码却是「参数无效」，很容易查错方向。DllImport 上的 CharSet 不影响结构体布局。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    /// <summary>
    /// DEVMODEW 的字段布局必须与原生结构逐字节对齐：
    /// dmOrientation/dmPrintQuality 与 dmPosition/dmDisplayOrientation 共用一段 16 字节的 union，
    /// 只声明短的那个版本会让后面所有字段偏移 8 字节，读出来的宽高就是垃圾。
    /// 这里用 POINTL 版本占住完整的 16 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        public POINTL dmPosition;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;

        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTL
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsW(
        string? lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

    // ---------------- 扩展样式写入（M4：点击穿透的徽标层） ----------------

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    // ---------------- 窗口外观（M5：透明度与点击穿透） ----------------

    internal const uint LWA_COLORKEY = 0x00000001;
    internal const uint LWA_ALPHA = 0x00000002;

    /// <summary>
    /// 设置分层窗口的透明度。
    /// 注意 dwFlags 只给 LWA_ALPHA 时，会把窗口原有的 LWA_COLORKEY 语义覆盖掉 ——
    /// 少数应用（老式不规则窗口）本来就靠 colorkey 挖洞，对它们改透明度会破坏外观。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>回读分层窗口的当前透明度（不信任 SetLayeredWindowAttributes 的返回值）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLayeredWindowAttributes(
        IntPtr hwnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);

    // ---------------- 键盘状态轮询（M4：拾取器的点击/取消按键） ----------------

    internal const int VK_LBUTTON = 0x01;
    internal const int VK_RBUTTON = 0x02;
    internal const int VK_TAB = 0x09;
    internal const int VK_ESCAPE = 0x1B;

    // 修饰键的虚拟键码：热键设置界面按 Alt 组合时，KeyEventArgs 里判断修饰键并不可靠，
    // 只能直接问系统「这些键现在是不是按着的」。
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;      // Alt
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    /// <summary>把鼠标移到屏幕坐标（供自检合成「用户把鼠标移到某个窗口上」）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    // ---------------- 控制台（仅供 --selftest 输出） ----------------
    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint dwProcessId);
}
