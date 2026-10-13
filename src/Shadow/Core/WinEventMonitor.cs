using System.Runtime.InteropServices;

namespace Shadow.Core;

/// <summary>
/// Z 序干扰的监视与防抖。
///
/// 为什么需要它：topmost 层带内部只有普通 Z 序，用户点一下别的置顶窗口、
/// 或某个应用自己抢一下 Z 序，我们的层级就被破坏了。所以要盯着系统事件，
/// 防抖后按层级表重新压栈。
///
/// 安全设计：只用 WINEVENT_OUTOFCONTEXT 的 SetWinEventHook —— 回调通过消息队列
/// 回到本进程，不注入目标进程、不安插全局钩子，对杀软与反作弊最友好。
/// </summary>
internal sealed class WinEventMonitor : IDisposable
{
    /// <summary>防抖窗口：连续事件（拖动、切换）合并成一次压栈。</summary>
    private const int DebounceMs = 150;

    /// <summary>兜底周期：漏掉事件、或目标周期性抢 Z 序时，最终仍能收敛。</summary>
    private const int SafetyMs = 1000;

    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = DebounceMs };
    private readonly System.Windows.Forms.Timer _safety = new() { Interval = SafetyMs };

    /// <summary>
    /// 必须持有委托引用。SetWinEventHook 只保存一个函数指针，
    /// 如果托管委托被 GC 回收，系统回调到已释放的桩上会让进程直接崩溃。
    /// </summary>
    private readonly NativeMethods.WinEventDelegate _callback;

    private readonly List<IntPtr> _hooks = new();
    private bool _dirty;
    private bool _disposed;

    internal WinEventMonitor()
    {
        _callback = OnWinEvent;
        _debounce.Tick += (_, _) => Flush();
        _safety.Tick += (_, _) => SafetyCheck();
    }

    /// <summary>被销毁的、且在我们层级表里的窗口。</summary>
    internal event Action<IntPtr>? WindowDestroyed;

    /// <summary>新窗口显示（M5 规则引擎自动接管用）。</summary>
    internal event Action<IntPtr>? WindowShown;

    /// <summary>需要重新压栈（已防抖）。</summary>
    internal event Action? ReapplyRequested;

    /// <summary>成功安装的钩子数量。</summary>
    internal int HookCount => _hooks.Count;

    internal bool Start()
    {
        // 逐个事件挂钩：单独挂可以精确控制关心的事件，避免海量无关回调
        int installed = 0;
        if (Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND)) installed++;
        if (Hook(NativeMethods.EVENT_OBJECT_DESTROY)) installed++;
        if (Hook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)) installed++;
        if (Hook(NativeMethods.EVENT_OBJECT_SHOW)) installed++;

        _debounce.Start();
        _safety.Start();

        return installed == 4;
    }

    private bool Hook(uint eventType)
    {
        IntPtr hook = NativeMethods.SetWinEventHook(
            eventType,
            eventType,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (hook == IntPtr.Zero)
        {
            Log.Warn(
                $"SetWinEventHook 失败 event=0x{eventType:X} " +
                $"Win32Error={Marshal.GetLastWin32Error()}");
            return false;
        }

        _hooks.Add(hook);
        return true;
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;

        if (!ShouldHandleEvent(eventType, idObject)) return;

        try
        {
            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    // 严格模式下，别人被激活就把我们的层级压回去；
                    // 宽松模式允许「点击提升」，所以不动手。
                    if (PinService.StrictMode && PinService.PinnedCount > 0) MarkDirty();
                    break;

                case NativeMethods.EVENT_OBJECT_DESTROY:
                    // 必须先回读确认句柄真的失效了再摘表。
                    //
                    // 曾经的写法是「事件类型是 DESTROY 就摘」，结果浏览器/游戏内部销毁
                    // 无障碍子对象（idObject != OBJID_WINDOW、但事件里的 hwnd 是承载它的
                    // 顶层窗口）时也走到这里，把用户刚钉住的窗口从置顶表里摘掉了：
                    // 面板少一行、徽标消失、之后不再维护它的 Z 序，而且 Forget 只摘表
                    // 不还原 topmost，于是留下一个 Shadow 管不到的顶位泄漏。
                    if (PinService.IsTracked(hwnd))
                    {
                        if (NativeMethods.IsWindow(hwnd))
                        {
                            // 真被销毁的窗口，事件送达时句柄已经失效；
                            // 句柄还有效就说明死的是它内部的子对象，不动我们的层级表。
                            Log.Info(
                                $"忽略一次销毁事件（窗口仍然有效，应是子对象被销毁）" +
                                $"hwnd=0x{hwnd.ToInt64():X} idObject={idObject}");
                        }
                        else
                        {
                            PinService.Forget(hwnd);
                            WindowDestroyed?.Invoke(hwnd);
                        }
                    }
                    break;

                case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                    // 这个事件极其频繁，只对已置顶的窗口响应
                    if (PinService.IsTracked(hwnd)) MarkDirty();
                    break;

                case NativeMethods.EVENT_OBJECT_SHOW:
                    WindowShown?.Invoke(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"WinEvent 回调处理失败 event=0x{eventType:X}", ex);
        }
    }

    /// <summary>
    /// 这个事件是否值得处理：必须是窗口级的目标事件。
    ///
    /// 单独抽成纯函数有两个理由：
    /// 1) 曾经这里是「A && B && C && idObject != OBJID_WINDOW」的一条 if，
    ///    只要事件类型命中（DESTROY / SHOW / LOCATIONCHANGE），整条判断就短路，
    ///    idObject 的检查被整个跳过 —— 子对象事件被当成窗口事件，正是「钉住的窗口
    ///    过几秒从列表里消失」那个事故的根因。分开写才不会重犯。
    /// 2) 纯函数可以在自检里直接回归（见 SelfTest 的 WinEvent 段）。
    /// </summary>
    /// <param name="eventType">WinEvent 事件类型。</param>
    /// <param name="idObject">事件里的对象类型；窗口本身是 OBJID_WINDOW。</param>
    internal static bool ShouldHandleEvent(uint eventType, int idObject)
    {
        // EVENT_SYSTEM_FOREGROUND 是系统事件，不是对象事件，idObject 的语义与
        // EVENT_OBJECT_* 不同（不保证是 OBJID_WINDOW），所以不做子对象过滤。
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND) return true;

        // 对象事件：只认窗口本身，菜单/滚动条/无障碍元素等子对象一律忽略。
        if (idObject != NativeMethods.OBJID_WINDOW) return false;

        return eventType == NativeMethods.EVENT_OBJECT_DESTROY ||
               eventType == NativeMethods.EVENT_OBJECT_SHOW ||
               eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE;
    }

    /// <summary>
    /// 仅供自检：把系统回调那条路径直接跑一遍，用来回归「子对象销毁事件不得摘掉
    /// 置顶表里的窗口」这条事故。产品路径永远由系统回调驱动，不会调用这个方法。
    /// </summary>
    internal void SimulateEventForTest(uint eventType, IntPtr hwnd, int idObject) =>
        OnWinEvent(IntPtr.Zero, eventType, hwnd, idObject, 0, 0, 0);

    private void MarkDirty()
    {
        _dirty = true;
        _debounce.Stop();   // 重新计时 = 防抖
        _debounce.Start();
    }

    private void Flush()
    {
        _debounce.Stop();
        if (!_dirty) return;

        _dirty = false;

        try
        {
            ReapplyRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("重新压栈失败", ex);
        }
    }

    private void SafetyCheck()
    {
        try
        {
            if (PinService.PinnedCount == 0) return;

            // 丢了 topmost 位必须补回来（两种模式都要），顺序则只在严格模式下维护
            if (PinService.AnyLostTopmost() ||
                (PinService.StrictMode && !PinService.IsOrderCorrect()))
            {
                ReapplyRequested?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Z 序兜底检查失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounce.Stop();
        _safety.Stop();
        _debounce.Dispose();
        _safety.Dispose();

        foreach (IntPtr hook in _hooks)
        {
            if (!NativeMethods.UnhookWinEvent(hook))
            {
                Log.Warn($"UnhookWinEvent 失败 hook=0x{hook.ToInt64():X}");
            }
        }

        _hooks.Clear();
    }
}
