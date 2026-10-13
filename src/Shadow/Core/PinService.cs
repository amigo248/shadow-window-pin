namespace Shadow.Core;

/// <summary>
/// 置顶管理层（进程内单例）。
///
/// 它是「层级顺序」的唯一真相来源：一张有序表 + 每次变更后按表重新压栈。
/// 之所以必须自己维护顺序，见 <see cref="ZOrderEnforcer"/> 的说明。
///
/// 线程约定：所有会调用 SetWindowPos 的方法都假定在 UI 线程调用
/// （WinEvent 回调与 WinForms 定时器都在 UI 线程，天然满足）。
/// </summary>
internal static class PinService
{
    private static readonly object Gate = new();

    /// <summary>[0] 是最上层。Rank 与下标一致（下标 + 1）。</summary>
    private static readonly List<PinnedWindow> Order = new();

    private static DateTime _lastOrderWarnAt = DateTime.MinValue;

    // ---------------------------------------------------------------- 防闪烁记账

    /// <summary>
    /// 自动维护路径下的写节流间隔：同一个窗口在这个间隔内不重复写 Z 序。
    /// 写 Z 序必然让窗口闪一下，所以宁可晚一个周期对上，也不要每秒重排一次。
    /// 用户主动操作（钉住 / 调整层级 / 取消置顶）不受此限制。
    /// </summary>
    internal static TimeSpan MinWriteInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>连续这么多次「写下去还是回读不到期望状态」之后，放弃这个窗口（避免和它互相抢）。</summary>
    private const int GiveUpStreak = 3;

    /// <summary>每个窗口最近一次真正发出去 Z 序写的时刻（节流用）。</summary>
    private static readonly Dictionary<IntPtr, DateTime> LastWriteAt = new();

    /// <summary>每个窗口「写了也不管用」的连续次数。</summary>
    private static readonly Dictionary<IntPtr, int> FailureStreak = new();

    /// <summary>
    /// 已降级为「松散模式」的窗口：铺满整屏 / 独占全屏 / 自己抢层级的窗口。
    /// 它们仍留在层级表里（列表、托盘、取消置顶都照旧），只是我们不再反复压它的 Z 序 ——
    /// 见 <see cref="IsLooseCandidate"/> 与 <see cref="BumpFailure"/> 的说明。
    /// </summary>
    private static readonly HashSet<IntPtr> Loose = new();

    private static int _zOrderWrites;

    /// <summary>进程启动至今真的发出去过多少次 Z 序写（自检用它证明「顺序对时一次都不写」）。</summary>
    internal static int ZOrderWriteCount
    {
        get { lock (Gate) return _zOrderWrites; }
    }

    /// <summary>某个窗口被降级成松散模式时触发一次（托盘提示用）。</summary>
    internal static event Action<string>? Degraded;

    /// <summary>层级表发生变化（钉住/取消/移动/窗口销毁）。</summary>
    internal static event Action? Changed;

    /// <summary>
    /// 严格模式（默认）：坚决维护层序，谁抢到前面都会被压回去。
    /// 宽松模式：允许用户点击把某个已置顶窗口提到最前，我们只补回它丢失的 topmost 状态。
    /// </summary>
    internal static bool StrictMode { get; set; } = true;

    internal static IReadOnlyList<PinnedWindow> Items
    {
        get { lock (Gate) return Order.ToArray(); }
    }

    internal static IReadOnlyList<IntPtr> PinnedWindows
    {
        get { lock (Gate) return Order.Select(item => item.Handle).ToArray(); }
    }

    internal static int PinnedCount
    {
        get { lock (Gate) return Order.Count; }
    }

    internal static bool IsTracked(IntPtr hwnd)
    {
        lock (Gate) return IndexOf(hwnd) >= 0;
    }

    /// <summary>1 起的层级号；0 表示不在层级表里。</summary>
    internal static int RankOf(IntPtr hwnd)
    {
        lock (Gate)
        {
            int index = IndexOf(hwnd);
            return index < 0 ? 0 : index + 1;
        }
    }

    internal static PinnedWindow? Find(IntPtr hwnd)
    {
        lock (Gate)
        {
            int index = IndexOf(hwnd);
            return index < 0 ? null : Order[index];
        }
    }

    // ---------------------------------------------------------------- 钉住

    /// <summary>把一个窗口加入置顶层。已在内则视为成功（幂等）。</summary>
    internal static bool TryPin(IntPtr hwnd, out string message, PinPosition position = PinPosition.Top)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            message = "窗口已失效";
            return false;
        }

        WindowInfo info = WindowInfo.Describe(hwnd);
        bool loose;
        string looseWhy;

        lock (Gate)
        {
            int existing = IndexOf(hwnd);
            if (existing >= 0)
            {
                Order[existing].Refresh();
                message = $"「{Order[existing].DisplayName}」已经在置顶列表里（层级 #{existing + 1}）";
                return true;
            }

            int insertAt = position == PinPosition.Top ? 0 : Order.Count;
            Order.Insert(insertAt, new PinnedWindow(hwnd, insertAt + 1, info));
            Renumber();
        }

        // 铺满整屏 / 独占全屏的窗口（多数游戏就是这种）：只做一次性置顶，之后不再维护它的层级。
        // 理由见 IsLooseCandidate —— 对这类窗口越勤快，画面越闪。
        // （放在锁外：它要读显示器/注册表，别在持锁时做 IO。）
        loose = IsLooseCandidate(hwnd, info, out looseWhy);
        if (loose) lock (Gate) Loose.Add(hwnd);

        bool ok = loose
            ? ZOrderEnforcer.SetTopmost(hwnd)              // 一次性，允许抢前台（用户刚点了它）
            : ApplyZOrder("钉住窗口", userInitiated: true);

        if (!ok)
        {
            // 系统没接受置顶请求（实测 Windows 11 新版记事本：SetWindowPos 返回成功，但 WS_EX_TOPMOST 位
            // 始终不置位）。这时绝不能把窗口留在层级表里：
            //   1) 界面会出现一个永远「置顶丢失」的行，看起来像本工具坏了；
            //   2) 每次事件/兜底压栈都会白调一次 SetWindowPos；
            //   3) 层级徽标会画在一个其实没被置顶的窗口上。
            // 直接回滚，并给出可读的原因（而不是只有「失败了」）。
            lock (Gate)
            {
                int index = IndexOf(hwnd);
                if (index >= 0) Order.RemoveAt(index);
                Renumber();
                ForgetBookkeeping(hwnd);
            }

            RaiseChanged();
            message = $"「{info.DisplayName}」无法置顶：{PinAdvisor.ExplainFailedReadback(info)}";
            return false;
        }

        RaiseChanged();

        lock (Gate)
        {
            int rank = IndexOf(hwnd) + 1;
            message = rank > 0
                ? $"已置顶「{info.DisplayName}」（层级 #{rank}）"
                : $"「{info.DisplayName}」未能加入置顶层";

            if (loose && rank > 0)
            {
                message += $"；{looseWhy}，只做这一次置顶，之后不再动它的层级（免得画面反复闪）";
            }
        }

        return ok;
    }

    /// <summary>把窗口移出置顶层并取消它的 topmost 位。</summary>
    internal static bool TryUnpin(IntPtr hwnd, out string message)
    {
        PinnedWindow? removedItem;
        bool removed;

        lock (Gate)
        {
            int index = IndexOf(hwnd);
            removed = index >= 0;
            removedItem = removed ? Order[index] : null;
            if (removed) Order.RemoveAt(index);
            Renumber();
            ForgetBookkeeping(hwnd);
        }

        bool cleared = true;
        if (NativeMethods.IsWindow(hwnd))
        {
            RestoreAppearance(removedItem, hwnd);

            if (WindowInfo.IsWindowTopmost(hwnd))
            {
                ZOrderEnforcer.ClearTopmost(hwnd);

                // 回读确认：SetWindowPos 的返回值不可信
                if (WindowInfo.IsWindowTopmost(hwnd))
                {
                    cleared = false;
                    Log.Warn($"取消置顶未生效 hwnd=0x{hwnd.ToInt64():X}（可能受保护或以更高权限运行）");
                }
            }
        }

        ApplyZOrder("取消置顶", userInitiated: true);
        RaiseChanged();
        message = removed ? "已移出置顶列表" : "该窗口不在置顶列表里";
        return cleared;
    }

    // ---------------------------------------------------------------- 外观

    /// <summary>
    /// 设置透明度 / 点击穿透。opacity 传 100 且 clickThrough 传 false 表示「还原外观」。
    /// </summary>
    internal static bool SetAppearance(IntPtr hwnd, int opacity, bool clickThrough, out string message)
    {
        PinnedWindow? item = Find(hwnd);
        if (item is null)
        {
            message = "该窗口不在置顶列表里";
            return false;
        }

        if (!item.OriginalStyle.Captured) item.OriginalStyle = WindowAppearance.Capture(hwnd);

        bool ok = WindowAppearance.Apply(hwnd, opacity, clickThrough, item.OriginalStyle, out string detail);

        item.Opacity = Math.Clamp(opacity, WindowAppearance.MinOpacity, WindowAppearance.MaxOpacity);
        item.ClickThrough = clickThrough;

        message = detail;
        if (!ok) Log.Warn($"设置窗口外观失败「{item.DisplayName}」：{detail}");
        else Log.Info($"窗口外观「{item.DisplayName}」：{detail}");

        RaiseChanged();
        return ok;
    }

    /// <summary>取消置顶时把我们改过的窗口外观还回去（半透明窗口不该在取消置顶后还是半透明的）。</summary>
    private static void RestoreAppearance(PinnedWindow? item, IntPtr hwnd)
    {
        if (item is null || !item.HasCustomAppearance) return;

        try
        {
            WindowAppearance.Restore(hwnd, item.OriginalStyle, out string detail);
            Log.Info($"取消置顶时还原窗口外观「{item.DisplayName}」：{detail}");
        }
        catch (Exception ex)
        {
            Log.Warn($"还原窗口外观失败 hwnd=0x{hwnd.ToInt64():X}：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>退出前把所有窗口的外观还原（最省事的做法是逐项调 RestoreAppearance）。</summary>
    private static void RestoreAllAppearances(IReadOnlyList<PinnedWindow> items)
    {
        foreach (PinnedWindow item in items)
        {
            if (!item.IsAlive) continue;
            RestoreAppearance(item, item.Handle);
        }
    }

    /// <summary>
    /// 「钉住 / 取消」的开关语义（M1 兼容入口）。
    /// true = 钉住并置于层级最上层，false = 取消钉住。
    /// </summary>
    internal static bool SetTopmost(IntPtr hwnd, bool topmost)
    {
        return topmost ? TryPin(hwnd, out _) : TryUnpin(hwnd, out _);
    }

    /// <summary>翻转置顶状态。返回值经「回读真实扩展样式」确认，不是推测值。</summary>
    internal static bool ToggleTopmost(IntPtr hwnd, out bool isTopmostNow)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            isTopmostNow = false;
            return false;
        }

        bool target = !WindowInfo.IsWindowTopmost(hwnd);

        if (target) TryPin(hwnd, out _);
        else TryUnpin(hwnd, out _);

        // 关键：不信任 SetWindowPos 的返回值，回读真实状态（UIPI / 独占全屏会让它说谎）
        isTopmostNow = WindowInfo.IsWindowTopmost(hwnd);

        if (isTopmostNow != target)
        {
            Log.Warn(
                $"置顶未生效 hwnd=0x{hwnd.ToInt64():X} 期望={target} 实际={isTopmostNow} " +
                "（可能原因：目标以更高权限运行 / 独占全屏 / 反作弊保护）");
        }

        return true;
    }

    // ---------------------------------------------------------------- 层级调整

    /// <summary>把窗口移到指定层级（1 起，超出会被夹到有效范围）。</summary>
    internal static bool MoveToRank(IntPtr hwnd, int newRank)
    {
        lock (Gate)
        {
            int index = IndexOf(hwnd);
            if (index < 0) return false;

            int target = Math.Clamp(newRank - 1, 0, Order.Count - 1);
            if (target == index) return true;

            PinnedWindow item = Order[index];
            Order.RemoveAt(index);
            Order.Insert(target, item);
            Renumber();
        }

        ApplyZOrder("调整层级", userInitiated: true);
        RaiseChanged();
        return true;
    }

    /// <summary>上移一层（rank 变小）。</summary>
    internal static bool MoveUp(IntPtr hwnd)
    {
        int rank = RankOf(hwnd);
        return rank > 1 && MoveToRank(hwnd, rank - 1);
    }

    /// <summary>下移一层（rank 变大）。</summary>
    internal static bool MoveDown(IntPtr hwnd)
    {
        int rank = RankOf(hwnd);
        return rank > 0 && rank < PinnedCount && MoveToRank(hwnd, rank + 1);
    }

    // ---------------------------------------------------------------- 维护

    /// <summary>窗口已销毁：只摘表，不再对它调用 SetWindowPos。</summary>
    internal static bool Forget(IntPtr hwnd)
    {
        bool removed;

        lock (Gate)
        {
            int index = IndexOf(hwnd);
            removed = index >= 0;
            if (removed) Order.RemoveAt(index);
            Renumber();
            ForgetBookkeeping(hwnd);
        }

        if (removed)
        {
            Log.Info($"窗口已销毁，自动移出置顶列表 hwnd=0x{hwnd.ToInt64():X}");
            RaiseChanged();
        }

        return removed;
    }

    /// <summary>
    /// 事件防抖后（或 1 秒兜底）统一走这里的策略。
    /// 严格模式：按层级表重新压栈；宽松模式：只补回丢失的 topmost 位，允许点击提升。
    /// </summary>
    internal static void ApplyPolicy(string reason)
    {
        if (PinnedCount == 0) return;

        if (StrictMode) ReapplyAll(reason);
        else RestoreTopmostBits();
    }

    /// <summary>
    /// 把当前层级表压到真实 Z 序上（调用方负责表的正确性）。
    ///
    /// 防闪烁纪律（用户 m04650 批准）：
    ///   1) ZOrderEnforcer.Enforce 内部先做幂等回读 —— 顺序本来就对时一次 SetWindowPos 都不发，
    ///      所以「自己触发 WinEvent → 再压栈」不会形成抖动循环；
    ///   2) 自动维护路径按 <see cref="MinWriteInterval"/> 节流，并且绝不抢前台（<paramref name="userInitiated"/> 为 false 时）；
    ///   3) 写下去还是回读不到期望状态 → 按窗口记账，连续 <see cref="GiveUpStreak"/> 次就把它降级为松散模式并提示一次。
    /// </summary>
    /// <param name="userInitiated">true = 用户主动操作（钉住/调整层级/取消置顶）：立刻生效、不限速、允许抢前台。</param>
    private static bool ApplyZOrder(string reason, bool userInitiated = false)
    {
        IntPtr[] handles;
        lock (Gate)
        {
            handles = Order.Where(item => item.IsAlive && !Loose.Contains(item.Handle))
                           .Select(item => item.Handle)
                           .ToArray();
        }

        if (handles.Length == 0) return true;

        if (userInitiated)
        {
            lock (Gate)
            {
                foreach (IntPtr handle in handles) FailureStreak.Remove(handle);
            }
        }

        ZOrderEnforcer.ChainResult result = ZOrderEnforcer.Enforce(
            handles,
            allowForegroundSteal: userInitiated,
            writeAllowed: handle => userInitiated || WriteAllowed(handle),
            onWrote: NoteWrite);

        if (result.Correct)
        {
            // 对上了就把「写了也不管用」的记账清零：它下次再被顶开，重新开始数。
            lock (Gate)
            {
                foreach (IntPtr handle in handles) FailureStreak.Remove(handle);
            }

            return true;
        }

        // 这一轮一次都没写（都被节流拦下）：不是「修不好」，下个周期再来。
        if (result.Writes == 0) return false;

        // 写过了还是不对 → 记账。连续多次还这样，说明它自己在抢层级（或者压根不吃我们的 topmost），
        // 继续压只会让它的画面一直闪，不如退一步。
        foreach (IntPtr handle in handles)
        {
            if (!NativeMethods.IsWindow(handle)) continue;
            if (!WindowInfo.IsWindowTopmost(handle)) BumpFailure(handle, "置顶位反复丢失");
        }

        for (int i = 0; i + 1 < handles.Length; i++)
        {
            IntPtr upper = handles[i];
            IntPtr lower = handles[i + 1];

            if (!NativeMethods.IsWindow(upper) || !NativeMethods.IsWindow(lower)) continue;
            if (!WindowInfo.IsWindowTopmost(upper) || !WindowInfo.IsWindowTopmost(lower)) continue;
            if (ZOrderEnforcer.IsAbove(upper, lower)) continue;

            BumpFailure(upper, "别人抢到了它前面");
            BumpFailure(lower, "别人抢到了它前面");
        }

        if (DateTime.Now - _lastOrderWarnAt > TimeSpan.FromSeconds(10))
        {
            _lastOrderWarnAt = DateTime.Now;
            IReadOnlyList<int> actual = ZOrderEnforcer.DescribeActualOrder(handles);
            Log.Warn(
                $"压栈复核未通过（{reason}）：期望 {handles.Length} 层，" +
                $"实际层带序号=[{string.Join(",", actual)}]；本轮写了 {result.Writes} 次");
        }

        return false;
    }

    /// <summary>自动维护路径的写节流：同一个窗口在 <see cref="MinWriteInterval"/> 内不重复写。</summary>
    private static bool WriteAllowed(IntPtr hwnd)
    {
        lock (Gate)
        {
            return !LastWriteAt.TryGetValue(hwnd, out DateTime last)
                   || DateTime.UtcNow - last >= MinWriteInterval;
        }
    }

    /// <summary>记一笔「我们真的写了 Z 序」——写一次就会闪一下，所以要能数得出来。</summary>
    private static void NoteWrite(IntPtr hwnd)
    {
        lock (Gate)
        {
            LastWriteAt[hwnd] = DateTime.UtcNow;
            _zOrderWrites++;
        }
    }

    /// <summary>
    /// 累计「写了也不管用」的次数；够了就把它降级为松散模式（只提示一次）。
    ///
    /// 为什么必须放弃：这类窗口（游戏抢层级、独占全屏、权限更高的窗口）我们永远压不住，
    /// 每一次尝试都会让它的画面闪一下 —— 用户看到的「置顶时一直闪」就是这么来的。
    /// 降级后它仍在层级表里：列表照旧显示、可以取消置顶、重启后照旧恢复。
    /// </summary>
    private static void BumpFailure(IntPtr hwnd, string why)
    {
        int streak;
        lock (Gate)
        {
            streak = FailureStreak.TryGetValue(hwnd, out int n) ? n + 1 : 1;
            FailureStreak[hwnd] = streak;
        }

        if (streak < GiveUpStreak) return;

        string name = Find(hwnd)?.DisplayName ?? $"0x{hwnd.ToInt64():X}";

        lock (Gate)
        {
            if (!Loose.Add(hwnd)) return;
            FailureStreak.Remove(hwnd);
        }

        Log.Warn(
            $"已停止对「{name}」强制维护置顶（{why}，连续 {streak} 次写下去都没对上）：" +
            "它多半是独占全屏/铺满整屏的游戏，或者自己在抢层级。继续压只会让它反复闪；" +
            "它仍在置顶列表里，可以照常取消置顶。");

        try
        {
            Degraded?.Invoke($"「{name}」不肯保持置顶（{why}），已停止反复调整它的层级。");
        }
        catch (Exception ex)
        {
            Log.Error("松散模式通知处理失败", ex);
        }
    }

    /// <summary>
    /// 仅供自检：把「一轮维护没对上」这条记账路径直接跑一遍。
    /// 真机上它是被 <see cref="ApplyZOrder"/> / <see cref="RestoreTopmostBits"/> 在
    /// 写入后回读失败时调用的，自检没法伪造一个「不吃 topmost」的窗口，所以直接打点验证。
    /// </summary>
    internal static void BumpFailureForTest(IntPtr hwnd, string why) => BumpFailure(hwnd, why);

    /// <summary>调用方持锁：清掉某个句柄的所有记账（它已离开层级表，句柄也可能被系统复用）。</summary>
    private static void ForgetBookkeeping(IntPtr hwnd)
    {
        Loose.Remove(hwnd);
        FailureStreak.Remove(hwnd);
        LastWriteAt.Remove(hwnd);
    }

    /// <summary>
    /// 这个窗口是不是「铺满整屏 / 独占全屏」—— 对它维护层级链的代价（画面反复闪）大于收益。
    ///
    /// 判据（任一命中即降级）：
    ///   1) 所在显示器处于独占全屏（当前显示模式与注册表里记录的模式不同）；
    ///   2) 无边框（没有 WS_CAPTION）且矩形覆盖所在显示器的整个 rcMonitor。
    /// 这两类窗口恰恰最可能「自己抢层级」或者根本不吃 topmost，
    /// 我们越勤快它越闪，所以退一步：钉一次，然后只看不管。
    /// </summary>
    private static bool IsLooseCandidate(IntPtr hwnd, WindowInfo info, out string why)
    {
        why = string.Empty;

        if (FullscreenDetector.DetectForWindow(hwnd, out string fullscreenDetail)
            == FullscreenVerdict.ExclusiveFullscreen)
        {
            why = $"它正处于独占全屏（{fullscreenDetail}）";
            return true;
        }

        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        bool borderless = (style & NativeMethods.WS_CAPTION) == 0;

        if (borderless && FullscreenDetector.CoversWholeMonitor(hwnd, out string coverDetail))
        {
            why = $"它是无边框铺满整屏的窗口（{coverDetail}）";
            return true;
        }

        return false;
    }

    /// <summary>某个窗口是否已降级为松散模式（不参与层级维护）。</summary>
    internal static bool IsLoose(IntPtr hwnd)
    {
        lock (Gate) return Loose.Contains(hwnd);
    }

    /// <summary>自检用：只问「这个窗口会不会被降级成松散模式」，不产生任何副作用。</summary>
    internal static bool WouldUseLooseMode(IntPtr hwnd, out string why) =>
        IsLooseCandidate(hwnd, WindowInfo.Describe(hwnd), out why);

    /// <summary>按当前层级表重新压栈并回读复核。</summary>
    internal static bool ReapplyAll(string reason)
    {
        if (PinnedCount == 0) return true;

        int removed;
        lock (Gate) removed = Order.RemoveAll(item => !item.IsAlive);

        if (removed > 0)
        {
            Renumber();
            RaiseChanged();
        }

        return ApplyZOrder(reason);
    }

    /// <summary>
    /// 只补回丢失的 topmost 位，不干预层内顺序（宽松模式用）。
    /// 同样受节流与记账约束：同一个窗口刚写过就先不写，连着写不好就降级（别和它互相抢）。
    /// </summary>
    internal static int RestoreTopmostBits()
    {
        IntPtr[] handles;
        lock (Gate)
        {
            handles = Order.Where(item => item.IsAlive && !Loose.Contains(item.Handle))
                           .Select(item => item.Handle)
                           .ToArray();
        }

        int restored = 0;
        foreach (IntPtr hwnd in handles)
        {
            if (WindowInfo.IsWindowTopmost(hwnd)) continue;

            if (!WriteAllowed(hwnd)) continue;

            NoteWrite(hwnd);

            // 不抢前台：自动维护去改用户焦点，比丢一次置顶更让人讨厌（游戏还会被切出全屏）。
            if (ZOrderEnforcer.SetTopmost(hwnd, allowForegroundSteal: false))
            {
                lock (Gate) FailureStreak.Remove(hwnd);
                restored++;
            }
            else
            {
                BumpFailure(hwnd, "置顶位反复丢失");
            }
        }

        if (restored > 0) Log.Info($"补回 {restored} 个窗口的顶部状态（宽松模式）");
        return restored;
    }

    /// <summary>有没有哪个已钉住的窗口丢了 topmost 位（松散模式的窗口不算：我们本来就不管它了）。</summary>
    internal static bool AnyLostTopmost()
    {
        lock (Gate)
        {
            foreach (PinnedWindow item in Order)
            {
                if (!item.IsAlive) continue;
                if (Loose.Contains(item.Handle)) continue;
                if (!WindowInfo.IsWindowTopmost(item.Handle)) return true;
            }
        }

        return false;
    }

    /// <summary>现实 Z 序是否符合我们的层级表（松散模式的窗口不参与判定）。</summary>
    internal static bool IsOrderCorrect()
    {
        IntPtr[] handles;
        lock (Gate)
        {
            handles = Order.Where(item => item.IsAlive && !Loose.Contains(item.Handle))
                           .Select(item => item.Handle)
                           .ToArray();
        }

        return ZOrderEnforcer.IsOrderCorrect(handles);
    }

    /// <summary>刷新标题等信息（窗口改名后 UI 需要）。</summary>
    internal static void RefreshSnapshots()
    {
        lock (Gate)
        {
            foreach (PinnedWindow item in Order) item.Refresh();
        }
    }

    /// <summary>取消所有由 Shadow 置顶的窗口。退出前必须调用。</summary>
    internal static int UnpinAll()
    {
        IntPtr[] handles;
        PinnedWindow[] items;

        lock (Gate)
        {
            items = Order.ToArray();
            handles = items.Select(item => item.Handle).ToArray();
            Order.Clear();
            Loose.Clear();
            FailureStreak.Clear();
            LastWriteAt.Clear();
        }

        // 先把外观还回去：置顶状态可以留到下次启动修，半透明窗口留给用户才是更糟的体验
        RestoreAllAppearances(items);

        int stillTopmost = 0;
        foreach (IntPtr hwnd in handles)
        {
            if (!NativeMethods.IsWindow(hwnd)) continue;

            ZOrderEnforcer.ClearTopmost(hwnd);
            if (WindowInfo.IsWindowTopmost(hwnd)) stillTopmost++;
        }

        if (stillTopmost > 0)
        {
            Log.Warn($"退出还原：有 {stillTopmost} 个窗口未能取消置顶（受保护或已失效）");
        }

        RaiseChanged();
        return handles.Length;
    }

    // ---------------------------------------------------------------- 内部

    private static int IndexOf(IntPtr hwnd) => Order.FindIndex(item => item.Handle == hwnd);

    private static void Renumber()
    {
        for (int i = 0; i < Order.Count; i++) Order[i].Rank = i + 1;
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("置顶列表变更通知处理失败", ex);
        }
    }
}
