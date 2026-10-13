using System.Reflection;
using Shadow.Core;
using Shadow.Ipc;
using Shadow.Persistence;

namespace Shadow.App;

/// <summary>把托盘、热键、主面板、置顶服务组装起来的应用外壳。</summary>
internal sealed class ShadowApp : IDisposable
{
    private readonly HotkeyWindow _hotkeyWindow = new();
    private readonly TrayIcon _tray = new();
    private readonly WinEventMonitor _monitor = new();
    private readonly RankBadgeOverlay _badges = new();

    /// <summary>命令行接口（命名管道服务端）。默认开启，可在「更多 ▾」里关掉。</summary>
    private PipeServer? _pipe;

    private string _toggleHotkeyText = HotkeyDefaults.PreferredToggleText;
    private string _unpinAllHotkeyText = HotkeyDefaults.PreferredUnpinAllText;

    private MainForm? _panel;
    private bool _disposed;

    /// <summary>退出流程中临时关闭「列表变化 → 写配置」，避免把上次清单也清掉。</summary>
    private bool _suppressProfileCapture;

    /// <summary>规则自动置顶的总量上限（见 OnWindowShown 的理由）。</summary>
    private const int MaxAutoPinned = 12;

    private bool _autoPinLimitWarned;

    internal ShadowApp()
    {
        _hotkeyWindow.TogglePinRequested += (_, _) => TogglePinUnderCursor();
        _hotkeyWindow.UnpinAllRequested += (_, _) => UnpinAll();
        _hotkeyWindow.RankRequested += OnRankRequested;

        _tray.ToggleRequested += (_, _) => TogglePinUnderCursor();
        _tray.UnpinAllRequested += (_, _) => UnpinAll();
        _tray.AboutRequested += (_, _) => ShowAbout();
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.OpenPanelRequested += (_, _) => ShowPanel();

        // 提前创建句柄，否则 RegisterHotKey 无处落脚
        _ = _hotkeyWindow.Handle;
    }

    /// <summary>交给 Application.Run 的主窗体（永不显示的隐藏窗体）。</summary>
    internal Form MainWindow => _hotkeyWindow;

    /// <param name="showPanelOnStart">
    /// 启动时是否把主面板摆出来。用户双击启动（无参数）时为 true —— 否则他只看到一个托盘图标，
    /// 会以为「打不开」；开机自启用 <c>--autostart</c> 启动时为 false，安静待在托盘里。
    /// </param>
    internal void Start(bool showPanelOnStart)
    {
        bool firstRun = !File.Exists(ProfileStore.FilePath);

        // 严格模式必须在任何压栈动作之前定下来，否则会按默认值先把层序压一遍
        PinService.StrictMode = ProfileStore.Current.StrictMode;
        PinService.Changed += OnPinnedListChanged;

        // 某个窗口被降级成松散模式（它自己在抢层级 / 独占全屏，我们压不住）：
        // 提示一次就够 —— 用户需要知道「它可能不会一直待在最上面」，否则会以为工具坏了。
        PinService.Degraded += OnPinDegraded;

        bool unclean = ProfileStore.DetectUncleanExit();

        _tray.SetStatus(PinService.PinnedCount);
        _tray.Show();
        RegisterHotkeys();
        _tray.SetHotkeyTexts(_toggleHotkeyText, _unpinAllHotkeyText);

        ProfileStore.Current.ToggleHotkey = _toggleHotkeyText;
        ProfileStore.Current.UnpinAllHotkey = _unpinAllHotkeyText;

        // 标记写在自启修复之前：无论后面哪一步出问题，下次启动都能发现「上次没跑完」
        ProfileStore.MarkRunning();

        if (unclean)
        {
            // 先摘掉残留在 topmost 层带里的窗口，再按清单自动钉回去：
            // 顺序反了会把残留状态和新钉的状态混在一起，用户根本看不出差别。
            int repaired = RepairLeakedTopmost();
            RestoreLastList(auto: true, repaired: repaired);
        }

        _panel = new MainForm(_tray, _toggleHotkeyText, _unpinAllHotkeyText);
        _panel.RestoreRequested += (_, _) => RestoreLastList();
        _panel.ExitRequested += (_, _) => ExitFromPanel();
        _panel.RankBadgeVisibilityChanged += OnRankBadgeVisibilityChanged;

        // 徽标开关是持久化设置，面板第一次 Load 时会读回来，这里按配置初始化一次
        _badges.SetVisible(ProfileStore.Current.ShowRankBadges);
        RefreshBadges();

        _monitor.WindowDestroyed += _ => UpdateStatus();
        _monitor.WindowShown += OnWindowShown;
        _monitor.ReapplyRequested += OnReapplyRequested;

        bool allHooks = _monitor.Start();
        Log.Info($"Z 序维护已启动：事件钩子 {_monitor.HookCount}/4，" +
                 $"严格模式={PinService.StrictMode}，兜底周期 1000ms");

        if (!allHooks)
        {
            Log.Warn("部分 WinEvent 钩子安装失败，Z 序维护将更依赖 1 秒兜底定时器");
        }

        // 启动时先按规则扫描一遍：规则命中的常驻窗口（编辑器、聊天工具）开机就该是置顶的
        int fromRules = RuleEngine.ApplyPinRules();
        if (fromRules > 0) _tray.Notify($"规则已自动置顶 {fromRules} 个窗口。");

        // 第一次运行、或用户自己双击启动时直接把面板摆出来 —— 否则用户只看到一个托盘图标，
        // 不知道该点哪里（实测这正是用户报「打不开 Shadow」的原因之一）。
        //
        // 启动时用「一次性前抬」而不是「临时置顶」：用户要求界面出现在屏幕最上层，
        // 但面板本身不要变成置顶窗口（不要一直浮在别人的窗口之上）。
        if (firstRun || showPanelOnStart) ShowPanel(temporaryTopMost: false);

        StartPipeServer();
    }

    /// <summary>
    /// 启动命令行接口。放在最后：它需要界面句柄已存在（命令要编送到 UI 线程执行），
    /// 而且命令行来得太快时，置顶服务必须已经初始化完毕。
    /// </summary>
    private void StartPipeServer()
    {
        if (!ProfileStore.Current.EnableCli)
        {
            Log.Info("命令行接口已在配置里关闭");
            return;
        }

        _pipe = new PipeServer(_hotkeyWindow);

        // 命令行能触发退出、「恢复上次列表」和「打开主面板」：这三个动作本来只能在界面上点。
        // 打开面板尤其需要 GUI 自己动手 —— 外部进程抬不动被置顶窗口盖住的面板。
        CommandProcessor.ShutdownRequested = Shutdown;
        CommandProcessor.RestoreRequested = () => RestoreLastList();
        CommandProcessor.PanelRequested = () => ShowPanel();

        if (_pipe.Start(out string detail))
        {
            Log.Info(detail);
        }
        else
        {
            _tray.Notify(detail, ToolTipIcon.Warning);
        }
    }

    /// <param name="temporaryTopMost">见 MainForm.ShowPanel：只有启动那一次传 false。</param>
    private void ShowPanel(bool temporaryTopMost = true) => _panel?.ShowPanel(temporaryTopMost);

    private void ExitFromPanel()
    {
        _panel?.AllowClose();
        Shutdown();
    }

    /// <summary>事件防抖后（或 1 秒兜底）被调用，策略见 PinService.ApplyPolicy。</summary>
    private void OnReapplyRequested()
    {
        PinService.ApplyPolicy("事件触发");

        // 压栈会把置顶窗口抬到层带最前，徽标自然被盖住 —— 压完立刻把徽标抬回去，
        // 否则用户会看到层号在操作后「消失」，过 500ms 才又出现。
        RefreshBadges();
    }

    /// <summary>
    /// 有窗口显示出来时问一遍规则引擎（自动接管：规则命中的窗口一出现就置顶）。
    /// 自动置顶有总量上限 —— 一条写得太宽的规则（比如 `.*`）会在用户开一堆窗口时把整屏都钉住，
    /// 那就从「帮助」变成了「事故」。到顶只提示一次，不反复刷气泡。
    /// </summary>
    private void OnWindowShown(IntPtr hwnd)
    {
        WindowInfo info = WindowInfo.Describe(hwnd);
        if (!info.IsValid) return;

        RuleMatch? match = RuleEngine.Evaluate(info);
        if (match is not { Action: RuleAction.Pin }) return;
        if (PinService.IsTracked(hwnd)) return;
        if (!WindowEnumerator.IsPinnable(info, out _)) return;

        if (PinService.PinnedCount >= MaxAutoPinned)
        {
            if (!_autoPinLimitWarned)
            {
                _autoPinLimitWarned = true;
                Log.Warn($"自动置顶已达上限 {MaxAutoPinned} 个，后续规则命中不再自动置顶");
                _tray.Notify($"规则自动置顶已达上限（{MaxAutoPinned} 个窗口），不再自动接管新窗口。",
                    ToolTipIcon.Warning);
            }

            return;
        }

        if (!PinService.TryPin(hwnd, out _, PinPosition.Bottom)) return;

        if (match.Opacity < WindowAppearance.MaxOpacity || match.ClickThrough)
        {
            PinService.SetAppearance(hwnd, match.Opacity, match.ClickThrough, out _);
        }

        Log.Info($"规则「{match.Rule.Name}」自动接管新窗口：{info.ShortDescription}");
        _tray.Notify($"规则「{match.Rule.Name}」已自动置顶：{info.DisplayName}");
    }

    private void OnRankBadgeVisibilityChanged(bool visible)
    {
        _badges.SetVisible(visible);
        RefreshBadges();
    }

    private void RefreshBadges() => _badges.Refresh(PinService.Items);

    /// <summary>
    /// 某个窗口被降级成松散模式（我们压不住它，停止反复调整它的层级）。
    /// 只提示一次，并把「松散」这个事实写进日志和托盘状态 —— 用户需要知道
    /// 「这个窗口可能不会一直待在最上面」，否则会以为工具坏了。
    /// </summary>
    private void OnPinDegraded(string message)
    {
        Log.Warn($"松散模式：{message}");
        _tray.Notify(message, ToolTipIcon.Warning);
        UpdateStatus();
    }

    /// <summary>
    /// 置顶列表变化时：刷新托盘状态 + 把列表写进配置。
    /// 每次变化都写盘是有意的 —— 崩溃时列表必须是最新的，否则下次启动不知道要修复谁。
    /// </summary>
    private void OnPinnedListChanged()
    {
        UpdateStatus();
        RefreshBadges();

        // 退出流程里会临时关掉捕获：UnpinAll 之后列表必然是空的，
        // 照旧写盘就等于把「上次清单」也一起抹掉了。
        if (_suppressProfileCapture) return;

        Profile profile = ProfileStore.Current;
        List<PinnedEntryDto> live = PinService.Items
            .Select(item => new PinnedEntryDto
            {
                ProcessName = item.Snapshot.ProcessName,
                ClassName = item.Snapshot.ClassName,
                Title = item.Snapshot.Title,

                // 外观与几何一起记下来：下次启动恢复的不只是「谁在最上面」，
                // 还有「半透明的那个还是半透明、摆过位置的还在原位」
                Opacity = item.Opacity,
                ClickThrough = item.ClickThrough,
                Geometry = WindowRecovery.CaptureGeometry(item.Handle),
            })
            .ToList();

        profile.PinnedWindows = live;

        // 只记住「非空」的清单：清空列表是无意义的记载，不该覆盖掉用户真正想恢复的那批窗口。
        // 这里必须存一份副本 —— 退出流程会对 PinnedWindows 调 Clear()，
        // 若两者指向同一个 List 实例，「上次清单」会被一起清掉，功能直接失效。
        if (live.Count > 0) profile.LastSession = live.ToList();

        ProfileStore.Save(profile);
    }

    /// <summary>
    /// 崩过一次之后，被钉住的窗口还留在 topmost 层带里（进程一死就没人给它们摘了）。
    /// 按「进程名 + 窗口类 + 标题」把上次的记录找回来并取消置顶。
    /// 刻意不清空配置里的清单 —— 紧接着要用它自动恢复；清空由调用方在恢复后负责。
    /// </summary>
    private int RepairLeakedTopmost()
    {
        Profile profile = ProfileStore.Current;
        if (profile.PinnedWindows.Count == 0) return 0;

        // 日志由 WindowRecovery 统一记录，这里不再重复打一遍
        return WindowRecovery.ClearLeakedTopmost(profile.PinnedWindows);
    }

    /// <summary>恢复置顶列表（按保存时的层级顺序重建）。</summary>
    private void RestoreLastList(bool auto = false, int repaired = 0)
    {
        Profile profile = ProfileStore.Current;

        // 异常退出后要恢复的是「崩溃那一刻正在置顶的清单」；手动恢复用「上次清单」
        List<PinnedEntryDto> source = auto && profile.PinnedWindows.Count > 0
            ? profile.PinnedWindows
            : profile.LastSession;

        if (source.Count == 0)
        {
            if (auto)
            {
                if (repaired > 0) _tray.Notify($"上次没有正常退出，已还原 {repaired} 个残留的置顶窗口。");
                else Log.Info("上次非正常退出，但配置里没有可恢复的置顶清单");
            }
            else
            {
                _tray.Notify("配置里没有保存过置顶列表。", ToolTipIcon.Warning);
            }

            return;
        }

        int restored = WindowRecovery.RePin(source, out List<string> missing);

        string message = auto
            ? $"上次没有正常退出：已还原 {repaired} 个残留置顶，并自动恢复 {restored} 个窗口"
            : $"已恢复 {restored} 个窗口的置顶";

        if (missing.Count > 0)
        {
            message += $"，{missing.Count} 个没找到：" + string.Join("、", missing.Take(3));
            if (missing.Count > 3) message += "…";
        }

        _tray.Notify(message, missing.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>Ctrl+Alt+数字：把鼠标下的窗口直接设到第 N 层（没置顶就先置顶）。</summary>
    private void OnRankRequested(int rank)
    {
        IntPtr hwnd = WindowEnumerator.GetRootWindowUnderCursor();
        if (hwnd == IntPtr.Zero)
        {
            _tray.Notify("没有找到鼠标指针下的窗口。", ToolTipIcon.Warning);
            return;
        }

        WindowInfo info = WindowInfo.Describe(hwnd);

        if (!PinService.IsTracked(hwnd))
        {
            if (!WindowEnumerator.IsPinnable(info, out string reason))
            {
                _tray.Notify($"这个窗口不支持置顶：{reason}。", ToolTipIcon.Warning);
                return;
            }

            if (!PinService.TryPin(hwnd, out string pinMessage))
            {
                _tray.Notify(pinMessage, ToolTipIcon.Warning);
                return;
            }
        }

        PinService.MoveToRank(hwnd, rank);

        int actual = PinService.RankOf(hwnd);
        string note = actual == rank ? string.Empty : $"（只到第 {actual} 层，当前共 {PinService.PinnedCount} 个窗口）";
        _tray.Notify($"「{info.DisplayName}」已设为第 {actual} 层{note}。");
    }

    private void RegisterHotkeys()
    {
        var failures = new List<string>();
        Profile profile = ProfileStore.Current;

        if (HotkeyDefaults.TryRegister(
                _hotkeyWindow, HotkeyWindow.IdTogglePin, togglePin: true,
                out string toggleText, out int toggleError, out int toggleAttempts,
                preferred: profile.ToggleHotkeyOverride))
        {
            _toggleHotkeyText = toggleText;
            if (toggleAttempts > 1)
            {
                Log.Warn($"首选组合 {HotkeyDefaults.PreferredToggleText} 被占用，改用 {toggleText}");
            }
        }
        else
        {
            failures.Add($"钉住/取消热键（最后尝试 {toggleText}，Win32 错误 {toggleError}）");
        }

        if (HotkeyDefaults.TryRegister(
                _hotkeyWindow, HotkeyWindow.IdUnpinAll, togglePin: false,
                out string unpinText, out int unpinError, out int unpinAttempts,
                preferred: profile.UnpinAllHotkeyOverride))
        {
            _unpinAllHotkeyText = unpinText;
            if (unpinAttempts > 1)
            {
                Log.Warn($"首选组合 {HotkeyDefaults.PreferredUnpinAllText} 被占用，改用 {unpinText}");
            }
        }
        else
        {
            failures.Add($"取消全部热键（最后尝试 {unpinText}，Win32 错误 {unpinError}）");
        }

        // 层级热键：Ctrl+Alt+1…9，缺几个不影响主流程，只记日志
        int rankOk = 0;
        for (int rank = 1; rank <= HotkeyWindow.RankCount; rank++)
        {
            if (HotkeyDefaults.TryRegisterRank(_hotkeyWindow, rank, out _)) rankOk++;
        }

        if (rankOk < HotkeyWindow.RankCount)
        {
            Log.Warn($"层级热键只注册上 {rankOk}/{HotkeyWindow.RankCount} 个（数字键常被截图工具或输入法占用）");
        }

        if (failures.Count > 0)
        {
            _tray.Notify(
                "全局热键注册失败：" + string.Join("；", failures) +
                "。可能被其他程序占用；托盘菜单的相同功能依然可用。",
                ToolTipIcon.Warning);
        }
    }

    /// <summary>钉住 / 取消钉住「鼠标指针下的顶层窗口」。</summary>
    internal void TogglePinUnderCursor()
    {
        IntPtr hwnd = WindowEnumerator.GetRootWindowUnderCursor();
        if (hwnd == IntPtr.Zero)
        {
            _tray.Notify("没有找到鼠标指针下的窗口。", ToolTipIcon.Warning);
            return;
        }

        WindowInfo info = WindowInfo.Describe(hwnd);

        // 已经在册：直接取消（不再做可置顶判定，否则窗口状态变了就取消不掉）
        if (PinService.IsTracked(hwnd))
        {
            PinService.TryUnpin(hwnd, out _);
            UpdateStatus();
            _tray.Notify($"已取消置顶：{info.DisplayName}");
            return;
        }

        if (!WindowEnumerator.IsPinnable(info, out string reason))
        {
            Log.Warn($"拒绝置顶：{reason} | {info.ShortDescription}");
            _tray.Notify($"这个窗口不支持置顶：{reason}。", ToolTipIcon.Warning);
            return;
        }

        bool expected = true;

        if (!PinService.TryPin(hwnd, out string message))
        {
            _tray.Notify(message, ToolTipIcon.Warning);
            return;
        }

        UpdateStatus();

        // 回读真实扩展样式 —— SetWindowPos 在独占全屏 / UIPI 场景下会「成功但没生效」
        if (WindowInfo.IsWindowTopmost(hwnd) != expected)
        {
            _tray.Notify(
                $"对「{info.DisplayName}」的置顶未生效。" +
                "常见原因：该窗口是独占全屏游戏、以更高权限运行，或被系统保护。",
                ToolTipIcon.Error);
            return;
        }

        int rank = PinService.RankOf(hwnd);
        string suffix = PinService.PinnedCount > 1
            ? $"（层级 #{rank}/{PinService.PinnedCount}）"
            : string.Empty;
        _tray.Notify($"已置顶：{info.DisplayName}{suffix}");
    }

    internal void UnpinAll()
    {
        int count = PinService.UnpinAll();
        UpdateStatus();
        _tray.Notify(count == 0
            ? "当前没有由 Shadow 置顶的窗口。"
            : $"已取消 {count} 个窗口的置顶。");
    }

    private void UpdateStatus() => _tray.SetStatus(PinService.PinnedCount);

    private void ShowAbout()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

        string text = string.Join(Environment.NewLine, new[]
        {
            $"Shadow v{version} —— 轻量级窗口置顶工具",
            string.Empty,
            "快捷键：",
            $"  {_toggleHotkeyText,-22}钉住 / 取消鼠标下的窗口",
            $"  {_unpinAllHotkeyText,-22}取消全部置顶",
            $"  {"Ctrl+Alt+1…9",-22}把鼠标下的窗口设为第 N 层",
            string.Empty,
            $"当前权限：{(ElevationHelper.IsElevated ? "管理员" : "普通用户")}",
            $"置顶窗口数：{PinService.PinnedCount}" +
            $"（{(PinService.StrictMode ? "严格层级" : "宽松层级")}）",
            $"开机自启：{AutoStartManager.Describe()}",
            $"数据目录：{ShadowPaths.DataDirectory}{(ShadowPaths.IsPortable ? "（便携模式）" : string.Empty)}",
            $"日志文件：{Log.FilePath}",
        });

        // 把面板当 owner：一是位置居中在面板上，二是主面板可能正临时置顶，
        // 没有 owner 的对话框会被它盖住（见 TopMostPopupGuard 的注释）。
        Dialogs.Message(_panel, text, "关于 Shadow");
    }

    private void Shutdown()
    {
        Log.Info("收到退出请求");

        _pipe?.Dispose();
        _pipe = null;
        _monitor.Dispose();
        _hotkeyWindow.UnregisterAll();

        // 先关面板（它会顺手把窗口尺寸写回配置），再清空置顶记录
        if (_panel is not null)
        {
            try
            {
                _panel.AllowClose();
                _panel.Close();
            }
            catch (Exception ex)
            {
                Log.Warn($"关闭主面板失败：{ex.Message}");
            }
        }

        // UnpinAll 会把列表清空，这里必须先把捕获关掉，
        // 否则「上次清单」（用户重启后还想要的）会被一起抹掉。
        _suppressProfileCapture = true;
        _badges.SetVisible(false);   // 徽标是独立顶层窗口，退出前必须收掉，否则会残留在桌面上
        int count = PinService.UnpinAll();
        Log.Info($"退出前还原了 {count} 个窗口的置顶状态");

        // 正在置顶的清单清空（下次启动不该误判为「有残留要修复」），
        // 但 LastSession 保留 —— 那是「恢复上次列表」的数据来源。
        ProfileStore.Current.PinnedWindows.Clear();
        ProfileStore.SaveCurrent();
        ProfileStore.ClearRunningMarker();

        _tray.Dispose();
        Application.ExitThread();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _pipe?.Dispose();
            _pipe = null;
            PinService.Changed -= OnPinnedListChanged;
            PinService.Degraded -= OnPinDegraded;
            _monitor.Dispose();
            _hotkeyWindow.UnregisterAll();
            PinService.UnpinAll();
        }
        catch (Exception ex)
        {
            Log.Error("退出清理失败", ex);
        }

        _panel?.Dispose();
        _badges.Dispose();
        _tray.Dispose();
        _hotkeyWindow.Dispose();
    }
}
