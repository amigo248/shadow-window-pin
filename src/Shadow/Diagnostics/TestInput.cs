using System.Runtime.InteropServices;
using Shadow.Core;

namespace Shadow.Diagnostics;

/// <summary>
/// 自检专用的输入合成（移动鼠标、点一下左键）。
///
/// 只有自检会用 —— 产品代码绝不合成输入：
/// 这个工具的原则是「只改窗口的 Z 序」，一旦开始伪造用户输入，行为就不可预期了。
/// 拾取器靠轮询 GetAsyncKeyState 判断左右键，所以这里用 mouse_event 造出来的按键状态
/// 与真实鼠标在拾取器眼里没有区别。
/// </summary>
internal static class TestInput
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;

    /// <summary>
    /// 合成点击时按住的时间。
    /// <para>
    /// 别改小：拾取器每 30ms 轮询一次，按住 40ms 时「当前按下」（0x8000）有时会整个落在两次轮询之间，
    /// 只能靠「上次调用之后被按过」（0x0001）这个粘滞位补救 —— 实测这个位偶尔读不到（同一份代码
    /// 连续三次自检里两次报「没有收到选中事件」，加上日志就再也没出现过，典型的时序性假失败）。
    /// 按住 120ms 时按下的物理状态一定能被至少一次轮询看见，判定就不再依赖那个粘滞位。
    /// 真人点一下也要 80～150ms，所以这个时长仍然贴近真实点击。
    /// </para>
    /// </summary>
    private const int HoldMs = 120;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    /// <summary>当前鼠标位置，失败时返回 false。</summary>
    internal static bool TryGetCursor(out int x, out int y)
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>把鼠标移到屏幕坐标。</summary>
    internal static bool MoveTo(int x, int y) => NativeMethods.SetCursorPos(x, y);

    /// <summary>
    /// 左键当前是否被按住。
    /// <para>
    /// 自检合成点击前必须先确认它是抬起的：拾取器判的是「按下」这个边沿，
    /// 如果检查开始时左键就已经被按住（例如用户正在用鼠标或玩游戏），
    /// 合成出来的 down 只是一直按着，不构成边沿，这一项就会假失败。
    /// </para>
    /// </summary>
    internal static bool IsLeftDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

    /// <summary>
    /// 最近一次 <see cref="LeftClick"/> 前后本进程读到的左键原始状态（自检诊断用）。
    /// <para>
    /// 自检偶尔报「没有收到选中事件」时，这个串能区分两种情况：注入的按下在本进程里
    /// 压根没出现过（环境/权限问题，比如全屏游戏或反作弊拦住了合成输入），
    /// 还是出现过、但拾取器的轮询没看见（判定逻辑问题）。
    /// </para>
    /// </summary>
    internal static string LastLeftClickObservation { get; private set; } = "（还没合成过点击）";

    /// <summary>
    /// 在当前位置按一下并松开左键（按住时长见 <see cref="HoldMs"/> 的说明）。
    /// </summary>
    internal static void LeftClick()
    {
        short before = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        short afterDown = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
        Thread.Sleep(HoldMs);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        short afterUp = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);

        LastLeftClickObservation =
            $"合成前=0x{before & 0xFFFF:X4} 按下后=0x{afterDown & 0xFFFF:X4} 松开后=0x{afterUp & 0xFFFF:X4}";
    }

    /// <summary>在当前位置按一下并松开右键（拾取器的取消手势）。</summary>
    internal static void RightClick()
    {
        mouse_event(MouseEventRightDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(HoldMs);
        mouse_event(MouseEventRightUp, 0, 0, 0, UIntPtr.Zero);
    }
}
