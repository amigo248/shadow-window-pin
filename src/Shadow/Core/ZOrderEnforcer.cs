namespace Shadow.Core;

/// <summary>
/// Z 序压栈引擎 —— 本项目的核心难点所在。
///
/// 背景：Windows 只有「两个层带」（topmost / 非 topmost），同一层带内部只剩普通 Z 序，
/// 而且用户点击任一 topmost 窗口都会把它提到该层带最前。所以「N 级自定义层级」
/// 系统原生不提供，只能由我们自己维护一张有序表，并持续把窗口按这个顺序重新压栈。
///
/// 压栈手法（比循环调用 HWND_TOPMOST 抖动小得多）：
///   SetWindowPos(R1, HWND_TOPMOST, ...)                       R1 提到层带最前
///   for i = 2..n: SetWindowPos(Ri, R(i-1), ..., SWP_NOACTIVATE)  逐个贴到前一个的正下方
/// 最终 Z 序 = R1 &gt; R2 &gt; … &gt; Rn &gt; 其他 topmost 窗口 &gt; 普通窗口。
/// </summary>
internal static class ZOrderEnforcer
{
    /// <summary>Z 序链的最大遍历步数：防止极端情况下死循环，走不到头就放弃判定。</summary>
    private const int MaxWalk = 512;

    /// <summary>
    /// 一次压栈尝试的结果。
    ///
    /// 为什么需要它：改 Z 序一定会让窗口闪一下，所以调用方必须知道
    /// 「这一轮我们到底动手了没有、动手几次、被拦下几次」——只靠一个 bool
    /// 无法区分「本来就是对」和「写了但没生效」，而这两者的处置完全不同。
    /// </summary>
    internal readonly struct ChainResult
    {
        internal ChainResult(bool correct, int writes, int skipped)
        {
            Correct = correct;
            Writes = writes;
            Skipped = skipped;
        }

        /// <summary>压栈后回读确认：顺序是否真的符合我们的层级表。</summary>
        internal bool Correct { get; }

        /// <summary>真的发出去的 Z 序写次数（0 = 这一轮全程只读）。</summary>
        internal int Writes { get; }

        /// <summary>被 <c>writeAllowed</c> 拦下、没发出去的写次数（不算「修不好」，下一轮再说）。</summary>
        internal int Skipped { get; }
    }

    /// <summary>
    /// 按给定顺序重新压栈。<paramref name="orderedHandles"/>[0] 为最上层。
    /// 返回压栈后「顺序是否确实正确」的复核结果（不信任 SetWindowPos 的返回值）。
    ///
    /// 防闪烁纪律（用户 m04650 批准）：
    ///   1) 顺序本来就对 → 一次写都不发（<see cref="IsOrderCorrect"/> 幂等短路）；
    ///   2) 自动维护路径可以用 <paramref name="writeAllowed"/> 做节流，写不写由调用方决定；
    ///   3) 只有用户主动操作（钉住 / 调整层级）才允许抢前台，自动维护绝不改用户焦点。
    /// </summary>
    internal static ChainResult Enforce(
        IReadOnlyList<IntPtr> orderedHandles,
        bool allowForegroundSteal = true,
        Func<IntPtr, bool>? writeAllowed = null,
        Action<IntPtr>? onWrote = null)
    {
        if (orderedHandles.Count == 0) return new ChainResult(true, 0, 0);

        // 幂等：已经正确就绝不动手。
        // 否则我们自己的 SetWindowPos 会触发 WinEvent，回调里再压栈 —— 形成闪烁死循环。
        if (IsOrderCorrect(orderedHandles)) return new ChainResult(true, 0, 0);

        int writes = 0;
        int skipped = 0;
        IntPtr first = orderedHandles[0];

        // R1 这一步即使它已经是 topmost 也要发：只有它真的待在层带最前，
        // 后面的链才是我们期望的形状（否则整条链会挂在一个不在最前的头下面）。
        if (Allowed(first, writeAllowed))
        {
            writes++;
            onWrote?.Invoke(first);

            if (!SetTopmost(first, allowForegroundSteal))
            {
                Log.Warn($"压栈：把 R1 提到层带最前失败 hwnd=0x{first.ToInt64():X}");
            }
        }
        else
        {
            skipped++;
        }

        for (int i = 1; i < orderedHandles.Count; i++)
        {
            IntPtr handle = orderedHandles[i];
            if (handle == orderedHandles[i - 1]) continue;

            if (!Allowed(handle, writeAllowed))
            {
                skipped++;
                continue;
            }

            writes++;
            onWrote?.Invoke(handle);

            bool ok = NativeMethods.SetWindowPos(
                handle,
                orderedHandles[i - 1],
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            if (!ok)
            {
                // 返回值会说谎（UIPI / 独占全屏 / 反作弊），所以这里只是记一笔，
                // 真正的判定交给下面的 IsOrderCorrect 回读。
                Log.Warn(
                    $"压栈：R{i + 1} 贴到 R{i} 之下调用失败 hwnd=0x{handle.ToInt64():X} " +
                    $"Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
        }

        return new ChainResult(IsOrderCorrect(orderedHandles), writes, skipped);
    }

    private static bool Allowed(IntPtr hwnd, Func<IntPtr, bool>? writeAllowed) =>
        writeAllowed is null || writeAllowed(hwnd);

    /// <summary>
    /// 把单个窗口提到 topmost 层带最前（不改动我们的层级表）。
    ///
    /// 返回值为真 = 已经回读确认 WS_EX_TOPMOST 真的置位了 —— 只信 SetWindowPos 的返回值是不够的：
    /// 实测 Windows 11 的资源管理器窗口在**不在前台**时，SetWindowPos(HWND_TOPMOST) 返回 True，
    /// 但 topmost 位根本不置位。官方文档《SetWindowPos》的 Remarks 写的正是这个前提：
    /// 「To use SetWindowPos to bring a window to the top, the process that owns the window
    ///   must have SetForegroundWindow permission.」
    /// 所以第一次失败后补一次「先把它切到前台，再置顶」——目标窗口的宿主进程由此拿到前台权限。
    /// 只在失败之后才抢前台，正常路径绝不动用户的焦点。
    /// </summary>
    /// <param name="allowForegroundSteal">
    /// 是否允许「失败就抢前台再试」这一招。抢前台会改变用户的焦点（游戏里尤其明显，
    /// 可能直接把游戏切出全屏），所以只给用户主动操作（钉住、调整层级）用；
    /// 自动维护路径传 false —— 丢一次置顶远没有夺走焦点那么讨厌。
    /// </param>
    internal static bool SetTopmost(IntPtr hwnd, bool allowForegroundSteal = true)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

        if (TrySetTopmost(hwnd)) return true;

        if (allowForegroundSteal && NativeMethods.GetForegroundWindow() != hwnd)
        {
            Log.Info($"压栈：目标窗口不在前台，先把它切到前台再置顶 hwnd=0x{hwnd.ToInt64():X}");

            NativeMethods.SetForegroundWindow(hwnd);
            System.Threading.Thread.Sleep(80);

            if (TrySetTopmost(hwnd)) return true;
        }

        return WindowInfo.IsWindowTopmost(hwnd);
    }

    /// <summary>发一次置顶请求，并以回读判定是否真的生效（不采信返回值）。</summary>
    private static bool TrySetTopmost(IntPtr hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        return WindowInfo.IsWindowTopmost(hwnd);
    }

    /// <summary>取消置顶（回到普通层带，会沉到所有 topmost 窗口之下）。</summary>
    internal static bool ClearTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

        return NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 回读真实 Z 序，判断给定顺序是否符合。
    ///
    /// 判据用的是「链式可达」而不是「严格相邻」：别人的 topmost 窗口可能插在我们两项之间，
    /// 那不算我们的层级错。只要从下一项往上走、在离开 topmost 层带之前能遇到上一项，就算对。
    /// </summary>
    internal static bool IsOrderCorrect(IReadOnlyList<IntPtr> orderedHandles)
    {
        // 前提：每一项都必须真的在 topmost 层带里。
        // 少了这一步，单个窗口时会「一项都没配对 → 直接返回 true」，
        // 于是 Enforce 的幂等短路会把「还没置顶」误判成「顺序已对」，永远不调用 SetWindowPos。
        foreach (IntPtr hwnd in orderedHandles)
        {
            if (!NativeMethods.IsWindow(hwnd)) return false;
            if (!WindowInfo.IsWindowTopmost(hwnd)) return false;
        }

        for (int i = 0; i + 1 < orderedHandles.Count; i++)
        {
            if (!IsAbove(orderedHandles[i], orderedHandles[i + 1])) return false;
        }

        return true;
    }

    /// <summary><paramref name="upper"/> 是否真的压在 <paramref name="lower"/> 之上。</summary>
    internal static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        if (upper == lower) return true;
        if (!NativeMethods.IsWindow(upper) || !NativeMethods.IsWindow(lower)) return false;

        IntPtr current = NativeMethods.GetWindow(lower, NativeMethods.GW_HWNDPREV);

        for (int step = 0; current != IntPtr.Zero && step < MaxWalk; step++)
        {
            if (current == upper) return true;

            // 已经走出 topmost 层带，说明上面不可能再有 upper 了
            if (!WindowInfo.IsWindowTopmost(current)) return false;

            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDPREV);
        }

        return false;
    }

    /// <summary>
    /// 读出现实的 topmost 层带顺序（从最上层往下）。
    /// 仅用于诊断、自检与 UI 显示，不参与决策。
    /// </summary>
    internal static IReadOnlyList<IntPtr> ReadTopmostBand()
    {
        var band = new List<IntPtr>();
        IntPtr current = NativeMethods.GetTopWindow(IntPtr.Zero);

        for (int step = 0; current != IntPtr.Zero && step < MaxWalk; step++)
        {
            if (!WindowInfo.IsWindowTopmost(current)) break;

            band.Add(current);
            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
        }

        return band;
    }

    /// <summary>给定顺序里，每个窗口现实中的「层带内序号」（1 起）；不在层带内记 0。</summary>
    internal static IReadOnlyList<int> DescribeActualOrder(IReadOnlyList<IntPtr> orderedHandles)
    {
        IReadOnlyList<IntPtr> band = ReadTopmostBand();
        var result = new int[orderedHandles.Count];

        for (int i = 0; i < orderedHandles.Count; i++)
        {
            for (int j = 0; j < band.Count; j++)
            {
                if (band[j] == orderedHandles[i])
                {
                    result[i] = j + 1;
                    break;
                }
            }
        }

        return result;
    }
}
