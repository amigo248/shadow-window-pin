using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Shadow.App;
using Shadow.Core;
using Shadow.Persistence;

namespace Shadow.Diagnostics;

/// <summary>
/// 无人值守自检。
///
/// 为什么需要它：窗口置顶的全部能力都建立在 Win32 调用之上，
/// 人工点几下只能证明「看起来是对的」。自检会启动一个真实子进程窗口，
/// 走完「发现窗口 → 置顶 → 回读真实扩展样式 → 取消置顶 → 全量还原」的完整链路。
///
/// 退出码：0 = 全部通过，1 = 有失败项。报告同时写入 %APPDATA%\Shadow\selftest.log。
/// </summary>
internal static class SelfTest
{
    private const string Marker = "SHADOW-SELFTEST";
    private const string PassLabel = "PASS";
    private const string FailLabel = "FAIL";
    private const string SkipLabel = "SKIP";

    internal static int Run()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        int passed = 0;
        int skipped = 0;

        void Check(string name, bool ok, string detail = "")
        {
            lines.Add($"{FailLabel} | {name}" + (string.IsNullOrEmpty(detail) ? string.Empty : $" | {detail}"));
            if (ok)
            {
                passed++;
                lines[^1] = $"{PassLabel} | {name}" + (string.IsNullOrEmpty(detail) ? string.Empty : $" | {detail}");
            }
            else
            {
                failures.Add(name);
            }
        }

        // 环境限制（例如受限账户/沙箱不允许创建计划任务）不算产品失败，
        // 但绝不能静默略过 —— 用 SKIP 留下痕迹，退出码只看 FAIL。
        void Skip(string name, string reason)
        {
            skipped++;
            lines.Add($"{SkipLabel} | {name} | {reason}");
        }

        void Note(string text) => lines.Add("INFO | " + text);

        Process? target = null;
        IntPtr hwnd = IntPtr.Zero;

        Boot.Trace("SelfTest.Run 进入");

        try
        {
            lines.Add($"{Marker} start {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Note($"PID={Environment.ProcessId} 管理员={ElevationHelper.IsElevated} " +
                 $"OS={Environment.OSVersion.VersionString} .NET={Environment.Version}");
            Note($"数据目录={ShadowPaths.DataDirectory}{(ShadowPaths.IsPortable ? "（便携模式）" : string.Empty)}");
            Note($"身份={WindowsIdentity.GetCurrent().Name} " +
                 $"域用户={Environment.UserDomainName}\\{Environment.UserName} 64位进程={Environment.Is64BitProcess}");

            // 数据目录必须实测「能写文件」：能建目录不等于能写，
            // 而写不进去正是最难排查的一类故障（日志与配置会一起消失）。
            string probePath = Path.Combine(ShadowPaths.DataDirectory, "write.probe");
            string probeDetail;
            bool probeOk;
            try
            {
                File.WriteAllText(probePath, "shadow", Encoding.UTF8);
                File.Delete(probePath);
                probeOk = true;
                probeDetail = ShadowPaths.DataDirectory;
            }
            catch (Exception ex)
            {
                probeOk = false;
                probeDetail = $"{ShadowPaths.DataDirectory} → {ex.GetType().Name} {ex.Message}";
            }
            Check("数据目录可写（日志与配置能落地）", probeOk, probeDetail);

            string exePath = Environment.ProcessPath ?? string.Empty;
            Check("能确定自身可执行文件路径", exePath.Length > 0, exePath);

            if (exePath.Length > 0)
            {
                target = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = AppModeParser.TestWindowSwitch,
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                });

                Check("靶进程已启动", target is not null,
                    target is null ? "Process.Start 返回 null" : $"pid={target.Id}");
            }

            if (target is not null)
            {
                hwnd = WaitForTargetWindow((uint)target.Id, TimeSpan.FromSeconds(20));
                Check("20 秒内找到靶窗口", hwnd != IntPtr.Zero,
                    hwnd == IntPtr.Zero
                        ? $"枚举超时，未找到标题匹配的窗口（子进程已退出={SafeHasExited(target)}）"
                        : $"hwnd=0x{hwnd.ToInt64():X}");
            }

            if (hwnd != IntPtr.Zero && target is not null)
            {
                WindowInfo info = WindowInfo.Describe(hwnd);
                Note("窗口快照：" + info.ShortDescription);

                Check("窗口标题匹配", info.Title == TestWindow.SelfTestTitle, $"\"{info.Title}\"");
                Check("窗口属于靶进程", info.ProcessId == (uint)target.Id,
                    $"实际 {info.ProcessId} / 期望 {target.Id}");
                Check("窗口尺寸有效", info.Width > 0 && info.Height > 0, $"{info.Width}x{info.Height}");
                Check("窗口不在 Shadow 自身进程内", !info.IsOwnProcess, $"PID={info.ProcessId}");
                Check("初始状态为非置顶", !info.IsTopmost, $"IsTopmost={info.IsTopmost}");
                Check("窗口可通过可置顶性校验",
                    WindowEnumerator.IsPinnable(info, out string reason), reason);

                // ---------- 置顶 / 取消置顶（核心能力）----------
                Check("SetTopmost(true) 调用成功", PinService.SetTopmost(hwnd, true));
                Check("置顶已生效（回读 WS_EX_TOPMOST）", WindowInfo.IsWindowTopmost(hwnd));
                Check("登记表已记录该窗口", PinService.IsTracked(hwnd),
                    $"PinnedCount={PinService.PinnedCount}");

                Check("SetTopmost(false) 调用成功", PinService.SetTopmost(hwnd, false));
                Check("取消置顶已生效", !WindowInfo.IsWindowTopmost(hwnd));
                Check("登记表已移除该窗口", !PinService.IsTracked(hwnd),
                    $"PinnedCount={PinService.PinnedCount}");

                // ---------- Toggle 语义 ----------
                PinService.ToggleTopmost(hwnd, out bool first);
                Check("Toggle 第一次 → 变为置顶", first);

                PinService.ToggleTopmost(hwnd, out bool second);
                Check("Toggle 第二次 → 取消置顶", !second);

                // ---------- Z 序读取（M2 分层压栈的基础）----------
                PinService.SetTopmost(hwnd, true);
                try
                {
                    IntPtr above = WindowEnumerator.GetWindowAbove(hwnd);
                    Note($"Z 序读取正常：该窗口之上是 hwnd=0x{above.ToInt64():X}");
                    Check("能读取真实 Z 序 (GetWindow/GW_HWNDPREV)", true);
                }
                catch (Exception ex)
                {
                    Check("能读取真实 Z 序 (GetWindow/GW_HWNDPREV)", false, ex.Message);
                }

                // ---------- 全量还原 ----------
                int before = PinService.PinnedCount;
                int unpinned = PinService.UnpinAll();

                Check("UnpinAll 覆盖了登记表中的全部窗口",
                    before == unpinned, $"登记 {before} 个 / 还原 {unpinned} 个");
                Check("UnpinAll 后登记表为空", PinService.PinnedCount == 0);
                Check("UnpinAll 后靶窗口已取消置顶", !WindowInfo.IsWindowTopmost(hwnd));

                Check("靶窗口在全部操作后依然有效", NativeMethods.IsWindow(hwnd));
            }

            // ---------- M2：多窗口层级与 Z 序压栈 ----------
            Process? secondWindow = null;
            WinEventMonitor? monitor = null;

            try
            {
                monitor = new WinEventMonitor();
                // 必须订阅：监视器只负责「发现需要重压」，压栈策略在 PinService.ApplyPolicy。
                // 不订阅就等于装了一堆钩子却没人处理，兜底与防抖都不会生效。
                monitor.ReapplyRequested += () => PinService.ApplyPolicy("自检：监视器触发");
                Check("WinEvent 钩子全部安装成功", monitor.Start(), $"hooks={monitor.HookCount}");

                // 事故回归（真机上「钉住几分钟后自己从列表里消失、徽标也没了」）：
                // 过滤守卫必须「先按事件类型放行、再无条件检查 idObject」。
                // 曾经写成一条 && 串起来的条件，事件类型一命中就短路，idObject 检查被跳过，
                // 于是浏览器/游戏内部销毁无障碍子对象时发来的 DESTROY 事件被当成
                // 「整扇窗口被销毁」，把用户刚钉住的窗口从层级表里摘掉了。
                Check("过滤守卫：子对象事件必须被拒（事故回归）",
                    !WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.OBJID_CLIENT) &&
                    !WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.OBJID_CLIENT) &&
                    !WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.OBJID_CLIENT),
                    "DESTROY / SHOW / LOCATIONCHANGE 带 OBJID_CLIENT 时都必须返回 false");

                Check("过滤守卫：窗口级事件必须放行",
                    WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.OBJID_WINDOW) &&
                    WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.OBJID_WINDOW) &&
                    WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.OBJID_WINDOW) &&
                    WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.OBJID_WINDOW));

                Check("过滤守卫：不关心的事件类型必须被拒",
                    !WinEventMonitor.ShouldHandleEvent(
                        NativeMethods.EVENT_OBJECT_HIDE, NativeMethods.OBJID_WINDOW),
                    "EVENT_OBJECT_HIDE 没有挂钩，不应进入处理逻辑");

                if (exePath.Length > 0)
                {
                    secondWindow = Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = AppModeParser.TestWindowSwitch,
                        UseShellExecute = false,
                        WorkingDirectory = AppContext.BaseDirectory,
                    });
                }

                Check("第二个靶进程已启动", secondWindow is not null,
                    secondWindow is null ? "Process.Start 返回 null" : $"pid={secondWindow.Id}");

                IntPtr hwnd2 = secondWindow is null
                    ? IntPtr.Zero
                    : WaitForTargetWindow((uint)secondWindow.Id, TimeSpan.FromSeconds(20));

                Check("第二个靶窗口已找到", hwnd2 != IntPtr.Zero,
                    hwnd2 == IntPtr.Zero ? "枚举超时" : $"hwnd=0x{hwnd2.ToInt64():X}");

                if (hwnd != IntPtr.Zero && hwnd2 != IntPtr.Zero)
                {
                    bool pinnedFirst = PinService.TryPin(hwnd, out string message1);
                    bool pinnedSecond = PinService.TryPin(hwnd2, out string message2);
                    Check("两个窗口都能置顶", pinnedFirst && pinnedSecond, $"{message1}；{message2}");
                    Check("层级表里有两个窗口", PinService.PinnedCount == 2,
                        $"PinnedCount={PinService.PinnedCount}");
                    Check("后钉的窗口占据第 1 层",
                        PinService.RankOf(hwnd2) == 1 && PinService.RankOf(hwnd) == 2,
                        $"新={PinService.RankOf(hwnd2)} 旧={PinService.RankOf(hwnd)}");
                    Check("真实 Z 序与层级表一致", PinService.IsOrderCorrect());
                    Note("现实 topmost 层带（上→下）：" +
                         string.Join(" > ", ZOrderEnforcer.ReadTopmostBand().Take(4)
                             .Select(h => "0x" + h.ToInt64().ToString("X"))));

                    // 事故回归：把「子对象被销毁」这条事件真的送进系统回调那条路径。
                    // 真机上是浏览器/游戏内部销毁无障碍子对象触发的，事件里的 hwnd
                    // 承载它的顶层窗口，正是我们钉住的那个窗口。
                    monitor.SimulateEventForTest(
                        NativeMethods.EVENT_OBJECT_DESTROY, hwnd2, NativeMethods.OBJID_CLIENT);
                    Check("子对象销毁事件不会把置顶窗口摘表（事故回归）",
                        PinService.IsTracked(hwnd2) && PinService.PinnedCount == 2,
                        $"仍在表里={PinService.IsTracked(hwnd2)} 数量={PinService.PinnedCount}");

                    // 就算事件自称是窗口级销毁，只要句柄还有效就不该摘表（回读兜底）
                    monitor.SimulateEventForTest(
                        NativeMethods.EVENT_OBJECT_DESTROY, hwnd2, NativeMethods.OBJID_WINDOW);
                    Check("窗口仍然有效时收到销毁事件也不摘表（回读兜底）",
                        PinService.IsTracked(hwnd2),
                        $"仍在表里={PinService.IsTracked(hwnd2)}");

                    // 把先钉的窗口提到第 1 层：层级互换必须真的反映到 Z 序上
                    PinService.MoveToRank(hwnd, 1);
                    bool swapped = PinService.RankOf(hwnd) == 1 && PinService.RankOf(hwnd2) == 2 &&
                                   ZOrderEnforcer.IsAbove(hwnd, hwnd2);
                    Check("把旧窗口提到第 1 层后 Z 序真的互换", swapped,
                        $"Rank(旧)={PinService.RankOf(hwnd)} Rank(新)={PinService.RankOf(hwnd2)} " +
                        $"旧在上={ZOrderEnforcer.IsAbove(hwnd, hwnd2)}");

                    PinService.MoveDown(hwnd);
                    Check("下移一层生效",
                        PinService.RankOf(hwnd) == 2 && ZOrderEnforcer.IsAbove(hwnd2, hwnd),
                        $"Rank={PinService.RankOf(hwnd)}");

                    PinService.MoveUp(hwnd);
                    Check("上移一层生效",
                        PinService.RankOf(hwnd) == 1 && ZOrderEnforcer.IsAbove(hwnd, hwnd2),
                        $"Rank={PinService.RankOf(hwnd)}");

                    // 故意破坏层序：把下层窗口强行提到最前，看监视器是否压回去
                    ZOrderEnforcer.SetTopmost(hwnd2);
                    Check("人为破坏层序已生效（测试前提成立）", !PinService.IsOrderCorrect());

                    bool healed = WaitUntil(() => PinService.IsOrderCorrect(), TimeSpan.FromSeconds(5));
                    Check("监视器自动把层序压回（防抖 / 1 秒兜底）", healed,
                        healed ? "已恢复" : "5 秒内未恢复");

                    // 窗口销毁 → 自动摘表（走真实 WinEvent 钩子回调）
                    secondWindow!.Kill();
                    secondWindow.WaitForExit(3000);

                    bool forgotten = WaitUntil(() => !PinService.IsTracked(hwnd2), TimeSpan.FromSeconds(5));
                    Check("窗口销毁后自动移出置顶列表", forgotten,
                        forgotten ? "已摘除" : "仍在层级表里");
                    Check("摘除后层级表只剩 1 个窗口", PinService.PinnedCount == 1,
                        $"PinnedCount={PinService.PinnedCount}");
                    Check("剩余窗口的层级仍然正确", PinService.IsOrderCorrect());

                    // 防闪烁回归（用户 m04650）：顺序本来就对时，维护轮必须一次 Z 序写都不发。
                    // 以前每个兜底周期都会重排一遍，游戏画面就会跟着闪。
                    int writesBefore = PinService.ZOrderWriteCount;
                    for (int round = 0; round < 5; round++)
                    {
                        PinService.ApplyPolicy("自检：顺序已正确时的空转维护");
                    }

                    Check("顺序已正确时，连续 5 轮维护一次 Z 序写都不发（防闪烁）",
                        PinService.ZOrderWriteCount == writesBefore,
                        $"写次数 {writesBefore} → {PinService.ZOrderWriteCount}");

                    // 防闪烁回归（用户 m04650）：真压不住的窗口（游戏抢层级 / 不吃 topmost）
                    // 连压三轮就该收手，而不是每秒再试一次把它闪个没完。
                    // 自检造不出「不吃 topmost」的窗口，所以直接打「这一轮没对上」的记账点。
                    int degradedNotices = 0;
                    void OnDegradedForTest(string _) => degradedNotices++;

                    PinService.Degraded += OnDegradedForTest;
                    try
                    {
                        PinService.BumpFailureForTest(hwnd, "自检：模拟压不住");
                        PinService.BumpFailureForTest(hwnd, "自检：模拟压不住");
                        bool looseAfterTwo = PinService.IsLoose(hwnd);

                        PinService.BumpFailureForTest(hwnd, "自检：模拟压不住");
                        Check("连续三轮压不住就停止强制维护（防闪烁）",
                            !looseAfterTwo && PinService.IsLoose(hwnd) && degradedNotices == 1,
                            $"第 2 轮就松散={looseAfterTwo} 第 3 轮后松散={PinService.IsLoose(hwnd)} " +
                            $"提示 {degradedNotices} 次");

                        PinService.BumpFailureForTest(hwnd, "自检：模拟压不住");
                        Check("已经放弃的窗口不再重复提示（不骚扰用户）",
                            degradedNotices == 1,
                            $"提示 {degradedNotices} 次");
                    }
                    finally
                    {
                        PinService.Degraded -= OnDegradedForTest;
                        PinService.UnpinAll();   // 顺手把松散记账清干净，别影响后面的检查
                    }
                }
            }
            catch (Exception ex)
            {
                Check("M2 层级验证过程不应抛出异常", false, ex.Message);
                Log.Error("M2 层级验证抛出异常", ex);
            }
            finally
            {
                monitor?.Dispose();

                try
                {
                    if (secondWindow is { HasExited: false })
                    {
                        secondWindow.Kill();
                        secondWindow.WaitForExit(3000);
                    }
                }
                catch
                {
                    // 忽略
                }

                secondWindow?.Dispose();
                PinService.UnpinAll();
            }

            // ---------- 枚举能力 ----------
            IReadOnlyList<WindowInfo> pinnable = WindowEnumerator.EnumeratePinnable();
            Check("能枚举到可置顶窗口", pinnable.Count > 0, $"count={pinnable.Count}");
            Note($"枚举到 {pinnable.Count} 个可置顶窗口，前几个：");
            foreach (WindowInfo window in pinnable.Take(5)) Note("  · " + window.ShortDescription);

            // ---------- 全局热键注册能力 ----------
            using (var hotkeyWindow = new HotkeyWindow())
            {
                // 热键是全局命名空间、先到先得，所以按候选表依次尝试；
                // 自检要验证的是「一定能拿到一个可用组合」，而不是某个写死的组合。
                bool first = HotkeyDefaults.TryRegister(
                    hotkeyWindow, HotkeyWindow.IdTogglePin, togglePin: true,
                    out string description1, out int error1, out int attempts1);
                Check("能注册「钉住/取消」全局热键（含自动回退）", first,
                    first
                        ? $"{description1}（第 {attempts1} 个候选）"
                        : $"全部候选被占用，最后错误 Win32Error={error1}");

                bool second = HotkeyDefaults.TryRegister(
                    hotkeyWindow, HotkeyWindow.IdUnpinAll, togglePin: false,
                    out string description2, out int error2, out int attempts2);
                Check("能注册「取消全部」全局热键（含自动回退）", second,
                    second
                        ? $"{description2}（第 {attempts2} 个候选）"
                        : $"全部候选被占用，最后错误 Win32Error={error2}");

                hotkeyWindow.UnregisterAll();
                Note("热键已全部注销");
            }

            // ---------- M3：配置持久化 / 主面板 / 崩溃修复 / 开机自启 ----------
            WindowInfo targetInfo = WindowInfo.Describe(hwnd);

            // 按签名找窗口：崩溃后清理残留、重启后恢复列表都靠它
            IntPtr bySignature = WindowEnumerator.FindBySignature(
                targetInfo.ProcessName, targetInfo.ClassName, targetInfo.Title);
            Check("能按「进程名 + 窗口类 + 标题」找回窗口", bySignature == hwnd,
                bySignature == hwnd
                    ? $"hwnd=0x{hwnd.ToInt64():X}"
                    : $"找到 0x{bySignature.ToInt64():X} / 期望 0x{hwnd.ToInt64():X}");

            IntPtr bogus = WindowEnumerator.FindBySignature(
                "no-such-process.exe", "NoSuchClass", "NoSuchTitle");
            Check("签名对不上时绝不误认窗口", bogus == IntPtr.Zero);

            Profile original = ProfileStore.Current;
            string configBackup = File.Exists(ProfileStore.FilePath)
                ? File.ReadAllText(ProfileStore.FilePath)
                : string.Empty;

            try
            {
                var probe = new Profile
                {
                    StrictMode = false,
                    PanelWidth = 611,
                    PanelHeight = 433,
                    PinnedWindows =
                    {
                        new PinnedEntryDto
                        {
                            ProcessName = targetInfo.ProcessName,
                            ClassName = targetInfo.ClassName,
                            Title = targetInfo.Title,
                        },
                    },
                };

                bool saved = ProfileStore.Save(probe);
                Check("配置能写入且临时文件已被原子替换",
                    saved && !File.Exists(ProfileStore.FilePath + ".tmp"));

                Profile reloaded = ProfileStore.Load();
                Check("配置能原样读回（严格模式 / 面板尺寸 / 置顶列表）",
                    !reloaded.StrictMode &&
                    reloaded.PanelWidth == 611 &&
                    reloaded.PanelHeight == 433 &&
                    reloaded.PinnedWindows.Count == 1 &&
                    reloaded.PinnedWindows[0].Title == targetInfo.Title,
                    $"严格={reloaded.StrictMode} 尺寸={reloaded.PanelWidth}x{reloaded.PanelHeight} " +
                    $"列表={reloaded.PinnedWindows.Count}");

                // 配置损坏必须能继续跑：备份坏文件 + 退回默认值
                File.WriteAllText(ProfileStore.FilePath, "{ 这显然不是合法的 JSON ");
                Profile fallback = ProfileStore.Load();
                Check("配置损坏时退回默认值并备份坏文件",
                    fallback.PinnedWindows.Count == 0 &&
                    fallback.StrictMode &&
                    File.Exists(ProfileStore.CorruptFilePath),
                    $"列表={fallback.PinnedWindows.Count} 备份存在={File.Exists(ProfileStore.CorruptFilePath)}");
            }
            catch (Exception ex)
            {
                Check("M3 配置持久化验证过程不应抛出异常", false, ex.Message);
            }
            finally
            {
                // 还原自检前的配置，别把开发机的配置弄脏
                ProfileStore.SetCurrent(original);
                if (configBackup.Length > 0) File.WriteAllText(ProfileStore.FilePath, configBackup);
                else ProfileStore.Save(original);
            }

            // 主面板：列表面板必须跟着 PinService 走
            try
            {
                using var probeTray = new TrayIcon();
                using var panel = new MainForm(probeTray, "Ctrl+Alt+SelfTest", "Ctrl+Alt+Shift+SelfTest");

                PinService.TryPin(hwnd, out _);
                Check("主面板列表自动跟上置顶列表", panel.RowCount == 1, $"行数={panel.RowCount}");
                Check("主面板行绑定的窗口句柄正确", panel.HandleAt(0) == hwnd);
                Check("主面板「层级」列与层级表一致",
                    panel.RankTextAt(0) == PinService.RankOf(hwnd).ToString(),
                    $"列={panel.RankTextAt(0)} 表={PinService.RankOf(hwnd)}");
                Check("主面板「状态」列显示置顶中", panel.StateTextAt(0) == "置顶中",
                    $"列={panel.StateTextAt(0)}");

                PinService.TryUnpin(hwnd, out _);
                Check("取消置顶后主面板列表清空", panel.RowCount == 0, $"行数={panel.RowCount}");

                // M6 修复（用户实测反馈）：面板唤不出来、模态对话框被置顶面板盖住
                // —— 现场见 TopMostPopupGuard 的类注释。
                panel.ShowPanel();
                Check("唤出面板时会临时置顶（否则压不过已置顶的窗口）",
                    panel.TopMost && WindowInfo.IsWindowTopmost(panel.Handle),
                    $"TopMost={panel.TopMost} 扩展样式=0x{WindowInfo.ExStyleOf(panel.Handle):X}");

                // 用户 m04568：启动时把界面抬到屏幕最上层，但面板自己不要变成置顶窗口。
                panel.ShowPanel(temporaryTopMost: false);
                Check("启动式唤出只做一次性前抬、不给面板加置顶属性（用户要求 m04568）",
                    !panel.TopMost && !WindowInfo.IsWindowTopmost(panel.Handle),
                    $"TopMost={panel.TopMost} 扩展样式=0x{WindowInfo.ExStyleOf(panel.Handle):X}");

                // 布局有时要等一次消息循环才稳定（ApplyMinimumWidth 在 Shown 时量一次，
                // 再用 BeginInvoke 复量一次），所以这里一边泵消息一边等它装得下。
                bool sceneRowFits = WaitUntil(() => { Application.DoEvents(); return panel.SceneRowFits; },
                    TimeSpan.FromSeconds(2));
                Check("面板宽度装得下整个场景行（最右边的按钮不会被裁掉）",
                    sceneRowFits,
                    $"场景行右边缘={panel.SceneRowRight} 客户区宽={panel.ClientWidth} 最小宽度={panel.MinimumSize.Width}");

                using (var probeDialog = new Form { Text = "Shadow SelfTest Dialog", Width = 120, Height = 80 })
                {
                    _ = probeDialog.Handle; // 建出句柄即可，不必真显示出来

                    bool promoted = TopMostPopupGuard.PromotePopup(probeDialog.Handle, panel.Handle);
                    Check("面板上的模态对话框会被临时提到置顶层（否则会被盖住点不动）",
                        promoted && WindowInfo.IsWindowTopmost(probeDialog.Handle),
                        $"提升={promoted} 置顶={WindowInfo.IsWindowTopmost(probeDialog.Handle)}");
                }

                Check("面板自己不会被误当成弹出对话框",
                    !TopMostPopupGuard.PromotePopup(panel.Handle, panel.Handle));

                panel.TopMost = false;
                panel.Hide(); // 别挡着后面拾取器的自检
            }
            catch (Exception ex)
            {
                Check("主面板构建过程不应抛出异常", false, $"{ex.GetType().Name}：{ex.Message}");
            }

            // 崩溃修复：进程死了，窗口还留在 topmost 层带里
            try
            {
                ZOrderEnforcer.SetTopmost(hwnd);
                Check("模拟崩溃残留（已置顶但不在 Shadow 记录里）",
                    WindowInfo.IsWindowTopmost(hwnd) && !PinService.IsTracked(hwnd));

                var leaked = new List<PinnedEntryDto>
                {
                    new()
                    {
                        ProcessName = targetInfo.ProcessName,
                        ClassName = targetInfo.ClassName,
                        Title = targetInfo.Title,
                    },
                };

                int repaired = WindowRecovery.ClearLeakedTopmost(leaked);
                Check("崩溃残留的置顶已被还原",
                    repaired == 1 && !WindowInfo.IsWindowTopmost(hwnd),
                    $"修复={repaired} 仍置顶={WindowInfo.IsWindowTopmost(hwnd)}");

                int restored = WindowRecovery.RePin(leaked, out List<string> missing);
                Check("能按配置把上次的列表恢复回来",
                    restored == 1 && missing.Count == 0 &&
                    PinService.IsTracked(hwnd) && PinService.RankOf(hwnd) == 1,
                    $"恢复={restored} 缺失={missing.Count} 层级={PinService.RankOf(hwnd)}");

                PinService.UnpinAll();
            }
            catch (Exception ex)
            {
                Check("崩溃修复验证过程不应抛出异常", false, $"{ex.GetType().Name}：{ex.Message}");
            }

            // 开机自启：真注册、真查询、真删除（用任务计划是为了免 UAC）
            bool autoStartWasOn = AutoStartManager.IsEnabled();
            string autoStartBefore = AutoStartManager.Describe();
            Note($"自检前开机自启状态：{autoStartBefore}");

            try
            {
                bool enabled = AutoStartManager.Enable(out string enableMessage);
                bool verified = enabled && AutoStartManager.IsEnabled();
                if (!verified && AutoStartManager.LooksLikeAccessDenied(enableMessage))
                {
                    Skip("能把开机自启注册到任务计划（最高权限、静默）",
                        $"环境限制，需在真实用户机器上验证：{enableMessage}");
                }
                else
                {
                    Check("能把开机自启注册到任务计划（最高权限、静默）", verified,
                        verified ? "已注册并查询到" : enableMessage);
                }
            }
            catch (Exception ex)
            {
                Check("注册开机自启过程不应抛出异常", false, $"{ex.GetType().Name}：{ex.Message}");
            }
            finally
            {
                try
                {
                    // 自检不能改变开发机的既有状态：开着的重新打开，关着的确保关掉
                    if (autoStartWasOn)
                    {
                        AutoStartManager.Enable(out _);
                        Check("开机自启已还原到自检前的开启状态", AutoStartManager.IsEnabled());
                    }
                    else
                    {
                        AutoStartManager.Disable(out string disableMessage);
                        Check("开机自启已还原到自检前的关闭状态",
                            !AutoStartManager.IsEnabled(), disableMessage);
                    }
                }
                catch (Exception ex)
                {
                    Check("取消开机自启过程不应抛出异常", false, $"{ex.GetType().Name}：{ex.Message}");
                }
            }

            // ---------- M5：外观（透明度 / 点击穿透）与几何记忆 ----------
            try
            {
                PinService.TryPin(hwnd, out _);

                bool appearanceOk = PinService.SetAppearance(hwnd, 60, clickThrough: true, out string appearanceMessage);
                bool alphaOk = WindowAppearance.TryGetAlpha(hwnd, out byte alpha);
                Check("能给置顶窗口设置透明度与点击穿透（含回读复核）",
                    appearanceOk && WindowAppearance.IsLayered(hwnd) && WindowAppearance.IsClickThrough(hwnd),
                    appearanceMessage);
                Check("分层窗口的透明度能被回读（60% → 153）",
                    alphaOk && alpha == 153,
                    $"alpha={(alphaOk ? alpha.ToString() : "读不到")}");

                PinService.SetAppearance(hwnd, WindowAppearance.MaxOpacity, clickThrough: false, out string resetMessage);
                Check("能把外观重置回不透明、不穿透",
                    !WindowAppearance.IsClickThrough(hwnd),
                    resetMessage);

                // 用户反馈（m02923）：拖动透明度滑块时每改一次就弹一条系统提示，太吵。
                // 现在的规矩是：成功不提示（面板上的数值标签与列表「外观」列已经说明了结果），
                // 只有「真正需要你知道的事」才提示 —— 外观设置失败、以及把点击穿透打开
                // （那是唯一一个可能把用户关在门外的操作）。
                using (var noiseTray = new TrayIcon())
                using (var noisePanel = new MainForm(noiseTray, "Ctrl+Alt+SelfTest", "Ctrl+Alt+Shift+SelfTest"))
                {
                    noisePanel.ShowPanel();
                    int mark = noiseTray.NotifyCount;

                    noisePanel.ApplyAppearanceForTest(hwnd, 70, clickThrough: false);
                    noisePanel.ApplyAppearanceForTest(hwnd, 60, clickThrough: false);
                    noisePanel.ApplyAppearanceForTest(hwnd, 50, clickThrough: false);
                    Check("调整透明度不弹系统提示（用户反馈 m02923）",
                        noiseTray.NotifyCount == mark,
                        $"连续改 3 次透明度，新增提示 {noiseTray.NotifyCount - mark} 条");

                    // 防闪烁回归（用户 m04650）：把透明度设成同一个值时不能再写一次
                    // SetLayeredWindowAttributes —— 每写一次系统都会让窗口重绘一遍，
                    // 调成同一个数值也照闪（拖滑块时手一抖就来回闪）。
                    // 上一步最后设的就是 50，所以这里连设两次 50 应该一次都不写。
                    int layeredWritesBefore = WindowAppearance.LayeredWriteCount;
                    noisePanel.ApplyAppearanceForTest(hwnd, 50, clickThrough: false);
                    noisePanel.ApplyAppearanceForTest(hwnd, 50, clickThrough: false);
                    Check("把透明度设成同一个值不再重复写分层透明度（防闪烁）",
                        WindowAppearance.LayeredWriteCount == layeredWritesBefore,
                        $"写次数 {layeredWritesBefore} → {WindowAppearance.LayeredWriteCount}");

                    noisePanel.ApplyAppearanceForTest(hwnd, 45, clickThrough: false);
                    Check("透明度真的变了仍然会写一次（上面那条不是因为整条路没走）",
                        WindowAppearance.LayeredWriteCount == layeredWritesBefore + 1,
                        $"写次数 {layeredWritesBefore} → {WindowAppearance.LayeredWriteCount}");

                    noisePanel.ApplyAppearanceForTest(hwnd, 50, clickThrough: true);
                    Check("打开点击穿透仍然提示一次（否则用户可能被关在门外）",
                        noiseTray.NotifyCount == mark + 1,
                        $"新增提示 {noiseTray.NotifyCount - mark} 条");

                    noisePanel.ApplyAppearanceForTest(hwnd, 40, clickThrough: true);
                    Check("穿透已打开时继续调透明度依然不提示",
                        noiseTray.NotifyCount == mark + 1,
                        $"新增提示 {noiseTray.NotifyCount - mark} 条");

                    noisePanel.ApplyAppearanceForTest(hwnd, 100, clickThrough: false);
                    Check("关掉点击穿透不提示（面板上看得见状态）",
                        noiseTray.NotifyCount == mark + 1,
                        $"新增提示 {noiseTray.NotifyCount - mark} 条");

                    // 收尾断言故意允许「已经撤掉分层」：不透明 + 不穿透时，SetAppearance 会把
                    // 当初为半透明加上的 WS_EX_LAYERED 还原掉，这时候本来就读不到 alpha。
                    bool finalLayered = WindowAppearance.IsLayered(hwnd);
                    bool finalAlphaRead = WindowAppearance.TryGetAlpha(hwnd, out byte finalAlpha);
                    Check("外观自检收尾：窗口已回到不透明、不穿透",
                        !WindowAppearance.IsClickThrough(hwnd) &&
                        (!finalLayered || (finalAlphaRead && finalAlpha == 255)),
                        $"穿透={WindowAppearance.IsClickThrough(hwnd)} 分层={finalLayered} " +
                        $"透明度={(finalAlphaRead ? finalAlpha.ToString() : "不适用（分层已撤掉）")}");
                }

                bool readRect = WindowGeometry.TryRead(hwnd, out NativeMethods.RECT startRect);
                Check("能读取窗口的当前位置与尺寸",
                    readRect && startRect.Width > 0 && startRect.Height > 0,
                    readRect ? $"{startRect.Left},{startRect.Top} {startRect.Width}x{startRect.Height}" : "GetWindowRect 失败");

                WindowGeometry.MoveResize(hwnd, 140, 140, startRect.Width, startRect.Height);
                GeometryDto? captured = WindowRecovery.CaptureGeometry(hwnd);
                Check("能把窗口几何记录成可用的数据",
                    captured is { IsUsable: true },
                    captured is null ? "捕获失败" : $"{captured.Left},{captured.Top} {captured.Width}x{captured.Height}");

                // 挪走再按记录还原：这是「窗口几何记忆」这条需求的全部内容
                WindowGeometry.MoveResize(hwnd, 500, 380, startRect.Width, startRect.Height);
                bool geometryRestored = captured is not null &&
                    WindowRecovery.TryRestoreGeometry(hwnd, captured, out string geometryMessage);
                bool backHome = WindowGeometry.TryRead(hwnd, out NativeMethods.RECT restoredRect) &&
                                restoredRect.Left == 140 && restoredRect.Top == 140;

                Check("能按记录把窗口摆回原来的位置",
                    geometryRestored && backHome,
                    backHome ? "位置一致" : $"实际={restoredRect.Left},{restoredRect.Top}");

                bool offscreen = WindowRecovery.TryRestoreGeometry(hwnd, new GeometryDto
                {
                    Left = -30000,
                    Top = -30000,
                    Width = 400,
                    Height = 300,
                }, out string offscreenMessage);

                Check("拒绝把窗口还原到不存在的显示器上（拔掉显示器后的经典事故）",
                    !offscreen, offscreenMessage);

                PinService.UnpinAll();
            }
            catch (Exception ex)
            {
                Check("M5 外观与几何验证过程不应抛出异常", false, $"{ex.GetType().Name}：{ex.Message}");
            }

            // ---------- M5：规则引擎（自动置顶 / 排除）与场景 ----------
            try
            {
                // 用靶窗口的进程名做正则，确保只命中我们自己的靶窗口
                string processPattern = "^" + Regex.Escape(targetInfo.ProcessName) + "$";

                RuleEngine.AddOrUpdate(new RuleDto
                {
                    Name = "自检置顶",
                    Pattern = processPattern,
                    Target = "process",
                    Action = "pin",
                    Rank = 1,
                });

                int autoPinned = RuleEngine.ApplyPinRules();
                Check("规则能按进程名自动置顶窗口",
                    autoPinned >= 1 && PinService.IsTracked(hwnd),
                    $"自动置顶 {autoPinned} 个，靶窗口在册={PinService.IsTracked(hwnd)}");

                Check("能删除规则",
                    RuleEngine.Remove("自检置顶") &&
                    !ProfileStore.Current.Rules.Any(rule => rule.Name == "自检置顶"));

                PinService.UnpinAll();

                // 排除规则必须是硬约束：连 IsPinnable 都要拒绝，否则热键/拾取器还能绕过去
                RuleEngine.AddOrUpdate(new RuleDto
                {
                    Name = "自检排除",
                    Pattern = processPattern,
                    Target = "process",
                    Action = "exclude",
                });

                bool pinnableWhenExcluded = WindowEnumerator.IsPinnable(targetInfo, out string excludeReason);
                Check("排除规则能挡住置顶（所有入口共用的判定）", !pinnableWhenExcluded, excludeReason);
                Check("排除的理由能说清是哪一条规则",
                    excludeReason.Contains("自检排除"), excludeReason);

                RuleEngine.Remove("自检排除");
                Check("删掉排除规则后窗口又能置顶",
                    WindowEnumerator.IsPinnable(targetInfo, out string afterReason), afterReason);

                // 场景：保存 → 清空 → 一键切换回来
                PinService.TryPin(hwnd, out _);
                SceneDto scene = SceneManager.Capture("自检场景");
                Check("能把当前置顶窗口保存成场景",
                    scene.Windows.Count == 1 && scene.Windows[0].Geometry is { IsUsable: true },
                    $"窗口数={scene.Windows.Count}");

                PinService.UnpinAll();
                int sceneRestored = SceneManager.Apply("自检场景", out List<string> sceneMissing);
                Check("切换场景能还原窗口与层级",
                    sceneRestored == 1 && sceneMissing.Count == 0 &&
                    PinService.IsTracked(hwnd) && PinService.RankOf(hwnd) == 1,
                    $"恢复={sceneRestored} 缺失={sceneMissing.Count} 层级={PinService.RankOf(hwnd)}");

                Check("能删除场景",
                    SceneManager.Delete("自检场景") && SceneManager.Find("自检场景") is null);

                PinService.UnpinAll();
            }
            catch (Exception ex)
            {
                Check("M5 规则与场景验证过程不应抛出异常", false, $"{ex.GetType().Name}：{ex.Message}");
            }
            finally
            {
                // 自检不能把测试规则留在开发机的配置里
                RuleEngine.Remove("自检置顶");
                RuleEngine.Remove("自检排除");
                SceneManager.Delete("自检场景");
                PinService.UnpinAll();
            }

            // ---------- M5：自定义热键文字的解析（写错也不能让热键失灵） ----------
            Check("自定义热键文字能解析回修饰键与按键",
                HotkeyDefaults.TryParse("Ctrl+Alt+P", out HotkeyModifiers parsedModifiers, out Keys parsedKey) &&
                parsedModifiers.HasFlag(HotkeyModifiers.Control) &&
                parsedModifiers.HasFlag(HotkeyModifiers.Alt) &&
                parsedKey == Keys.P,
                "Ctrl+Alt+P");

            Check("自定义热键解析支持功能键与数字键",
                HotkeyDefaults.TryParse("Ctrl+Shift+F9", out _, out Keys f9) && f9 == Keys.F9 &&
                HotkeyDefaults.TryParse("Win+Alt+3", out HotkeyModifiers winModifiers, out Keys digit) &&
                winModifiers.HasFlag(HotkeyModifiers.Win) && digit == Keys.D3,
                "Ctrl+Shift+F9 / Win+Alt+3");

            Check("看不懂的热键文字会被拒绝（退回候选表，而不是彻底失灵）",
                !HotkeyDefaults.TryParse("这不是热键", out _, out _) &&
                !HotkeyDefaults.TryParse("P", out _, out _) &&
                !HotkeyDefaults.TryParse(string.Empty, out _, out _),
                "三种非法输入均被拒绝");

            // ---------- M4：风险判定（反作弊 / 独占全屏 / 无重定向位图）----------
            Check("反作弊名单能命中已知组件",
                AntiCheatGuard.IsTargetProcess("EasyAntiCheat.exe") &&
                AntiCheatGuard.IsTargetProcess("BEService_x64") &&
                AntiCheatGuard.IsTargetProcess(@"C:\Program Files\vgk.exe"),
                "EasyAntiCheat / BEService_x64 / vgk");

            Check("反作弊名单不会误伤普通进程",
                !AntiCheatGuard.IsTargetProcess("notepad.exe") &&
                !AntiCheatGuard.IsTargetProcess("explorer") &&
                !AntiCheatGuard.IsTargetProcess("Shadow.exe"),
                "notepad / explorer / Shadow");

            Note($"本机在跑的反作弊组件：{(AntiCheatGuard.Describe() ?? "（无）")}");

            {
                FullscreenVerdict verdict = FullscreenDetector.DetectForWindow(hwnd, out string modeDetail);
                Check("能判定显示器当前是否处于独占全屏", verdict != FullscreenVerdict.Unknown, modeDetail);
                Note($"显示模式：{modeDetail}");

                bool covers = FullscreenDetector.CoversWholeMonitor(hwnd, out string coverDetail);
                Check("「铺满显示器」判定不会把普通窗口误判成无边框全屏", !covers, coverDetail);

                // 防闪烁回归（用户 m04650）：铺满整屏的无边框窗口（多数游戏的全屏模式）会被
                // 判成「松散模式」候选 —— 对它们反复维护层级只会让画面一直闪，所以只做一次
                // 置顶就不再动。这里临时造一个「无边框 + 铺满主显示器」的窗口来验判定；
                // 它从不 Show()，不会在用户屏幕上冒出来，也不会被钉住。
                using (var fakeFullscreen = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    Bounds = Screen.PrimaryScreen!.Bounds,
                })
                {
                    IntPtr fakeHandle = fakeFullscreen.Handle;
                    bool looseForFake = PinService.WouldUseLooseMode(fakeHandle, out string fakeWhy);
                    Check("无边框铺满整屏的窗口会被判成松散模式候选（防闪烁）", looseForFake,
                        string.IsNullOrEmpty(fakeWhy) ? "未命中" : fakeWhy);

                    bool looseForTarget = PinService.WouldUseLooseMode(hwnd, out string targetWhy);
                    Check("普通带边框窗口不会被误判成松散模式", !looseForTarget,
                        looseForTarget ? targetWhy : "未命中，仍按普通窗口维护层级");
                }
            }

            if (hwnd != IntPtr.Zero)
            {
                WindowInfo advisedInfo = WindowInfo.Describe(hwnd);
                PinAdvice advice = PinAdvisor.Evaluate(advisedInfo);
                Check("拾取建议：靶窗口可以置顶", advice.CanPin, advice.Message);
                if (advice.HasWarning) Note("拾取建议里的提醒：" + advice.Warning);

                Note("回读失败时的解释话术：" + PinAdvisor.ExplainFailedReadback(advisedInfo));
            }

            // ---------- M4：准星拾取器 + 层级徽标（真的合成鼠标动作）----------
            TestInput.TryGetCursor(out int savedX, out int savedY);

            // M4 的界面检查（拾取器、层级徽标）都依赖「靶窗口真的在鼠标下可达」：本机实测会跑
            // 独占全屏游戏（三角洲行动，2560x1440 全屏、而且还是前台窗口），它把靶窗口、拾取器
            // 边框、徽标一起盖住 —— 鼠标命中测试落不到靶窗口上，置顶也会被「置顶需要前台权限」
            // 那条规则拒绝。这时的失败全是环境造成的假失败，还会淹掉真正的回归。所以不可达时
            // 这一类检查一律记 SKIP（附原因），并且不合成任何点击 —— 别把点击送进用户正在玩的游戏里。
            // 真正的判定在把光标移到靶窗口中心之后（见 uiReachable 的赋值处）。
            bool uiReachable = true;
            string uiSkipReason = string.Empty;

            void UiCheck(string name, bool ok, string detail)
            {
                if (!uiReachable)
                {
                    Skip(name, uiSkipReason);
                    return;
                }

                Check(name, ok, detail);
            }

            NativeMethods.RECT targetRect = default;
            bool haveTargetRect = hwnd != IntPtr.Zero &&
                                  NativeMethods.GetWindowRect(hwnd, out targetRect);

            try
            {
                if (haveTargetRect)
                {
                    int centerX = (targetRect.Left + targetRect.Right) / 2;
                    int centerY = (targetRect.Top + targetRect.Bottom) / 2;

                    // 同进程候选：靶窗口自己必须排第 1，否则 Tab 切换的起点是错的
                    List<WindowInfo> siblings = WindowEnumerator.SiblingsOf(hwnd);
                    Check("同进程窗口候选列表以当前窗口为起点",
                        siblings.Count >= 1 && siblings[0].Handle == hwnd,
                        $"count={siblings.Count}");

                    using var picker = new PickerOverlay();
                    IntPtr pickedHandle = IntPtr.Zero;
                    bool cancelled = false;
                    picker.Picked += handle => pickedHandle = handle;
                    picker.Cancelled += () => cancelled = true;

                    TestInput.MoveTo(centerX, centerY);
                    uiReachable = WindowEnumerator.GetRootWindowUnderCursor() == hwnd;
                    if (!uiReachable)
                    {
                        WindowInfo blocker = WindowInfo.Describe(WindowEnumerator.GetRootWindowUnderCursor());
                        uiSkipReason = "鼠标下的窗口不是靶窗口，而是 "
                                       + (blocker.IsValid ? blocker.ShortDescription : "未知窗口")
                                       + "（多半是有全屏应用盖住了靶窗口，例如正在运行的游戏），本机环境下无法判定";
                    }

                    DateTime pickStart = DateTime.UtcNow;
                    picker.Start();

                    // 自检里没有 Application.Run，WM_TIMER 可能被无限推迟，所以由自检直接驱动轮询
                    // （见 PickerOverlay.PollNow 的注释）。产品路径仍由 30ms 定时器驱动。
                    bool tracked = WaitUntil(() => { picker.PollNow(); return picker.CurrentHandle == hwnd; },
                        TimeSpan.FromSeconds(3));

                    // 光标可能被独占全屏应用「抢走」：这类应用会锁定或把光标回中，SetCursorPos 很快
                    // 就被改回去，于是同一坐标的命中测试会时而命中靶窗口、时而命中那个应用。
                    // 这不是拾取器坏了，而是本机环境没法验证 —— 这种情况如实记 SKIP。
                    IntPtr cursorNow = WindowEnumerator.GetRootWindowUnderCursor();
                    bool cursorDrifted = cursorNow != hwnd;
                    WindowInfo driftInfo = WindowInfo.Describe(cursorNow);
                    string driftReason = "光标已被「"
                                         + (driftInfo.IsValid ? driftInfo.ShortDescription : $"0x{cursorNow.ToInt64():X}")
                                         + "」占住（多半是全屏应用在锁定光标），本机环境下无法判定";

                    if (tracked)
                    {
                        Check("拾取器能实时框住鼠标下的窗口", true,
                            $"hwnd=0x{picker.CurrentHandle.ToInt64():X}");
                    }
                    else if (cursorDrifted && picker.CurrentHandle == cursorNow)
                    {
                        Skip("拾取器能实时框住鼠标下的窗口", driftReason);
                    }
                    else
                    {
                        Check("拾取器能实时框住鼠标下的窗口", false, "3 秒内未命中靶窗口");
                    }

                    UiCheck("拾取器能给出「可以置顶」的建议",
                        picker.CurrentAdvice is { CanPin: true },
                        picker.CurrentAdvice?.Message ?? "没有建议");

                    bool frameShown = picker.FrameWindow.Visible;
                    long frameEx = NativeMethods
                        .GetWindowLongPtr(picker.FrameWindow.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                    UiCheck("拾取器边框已显示且不抢焦点（WS_EX_NOACTIVATE）",
                        frameShown && (frameEx & NativeMethods.WS_EX_NOACTIVATE) != 0,
                        $"可见={frameShown} 扩展样式=0x{frameEx:X}");

                    // 前提：左键必须是抬起的。拾取器判的是「按下」这个边沿，如果自检开始时
                    // 左键就已经被按住（用户正在用鼠标或玩游戏），合成的 down 只是一直按着、
                    // 不构成边沿 —— 这一项会假失败，此时如实记 SKIP 而不是 FAIL。
                    bool leftFree = WaitUntil(() => !TestInput.IsLeftDown(), TimeSpan.FromSeconds(3));
                    if (!uiReachable)
                    {
                        Skip("左键点击能选中被框住的窗口", uiSkipReason);
                        Skip("选中后拾取器自动收起", uiSkipReason);
                        picker.Stop();
                    }
                    else if (!leftFree)
                    {
                        Skip("左键点击能选中被框住的窗口",
                            "自检期间左键一直处于按下状态（用户正在用鼠标），合成点击不构成新的按下边沿，无法判定");
                        Skip("选中后拾取器自动收起", "同上：左键点击那一项被跳过");
                        picker.Stop();
                    }
                    else if (WindowEnumerator.GetRootWindowUnderCursor() != hwnd)
                    {
                        // 合成点击之前再确认一次光标还在靶窗口上：宁可 SKIP，
                        // 也绝不能把这一下点击送进用户正在玩的全屏游戏里。
                        Skip("左键点击能选中被框住的窗口", driftReason);
                        Skip("选中后拾取器自动收起", "同上：左键点击那一项被跳过");
                        picker.Stop();
                    }
                    else
                    {
                        // 点「拾取窗口」那一下的静默期是 250ms，等过去再合成点击
                        WaitUntil(() => (DateTime.UtcNow - pickStart).TotalMilliseconds > 500, TimeSpan.FromSeconds(2));

                        // 合成点击必须放在后台线程：主线程正忙着轮询拾取器，如果在这里同步
                        // down→Sleep(120)→up，那 120ms 里主线程一次轮询都做不了，「当前按下」
                        // （0x8000）就整个被错过；只剩「上次调用之后被按过」（0x0001）能兜底，
                        // 而那个位一读就清、实测极不可靠 —— 这正是这项检查反复假失败的根因。
                        var clickThread = new Thread(TestInput.LeftClick) { IsBackground = true };
                        clickThread.Start();

                        bool picked = WaitUntil(() => { picker.PollNow(); return pickedHandle != IntPtr.Zero; },
                            TimeSpan.FromSeconds(3));
                        clickThread.Join(TimeSpan.FromSeconds(2));

                        if (pickedHandle != IntPtr.Zero && pickedHandle != hwnd &&
                            pickedHandle == WindowEnumerator.GetRootWindowUnderCursor())
                        {
                            // 拾取器框住并选中的确实不是靶窗口，而是此刻光标底下那个窗口（通常是全屏游戏）：
                            // 选中它是**正确行为**，问题在本机环境而不在拾取器。
                            Skip("左键点击能选中被框住的窗口", driftReason);
                            Skip("选中后拾取器自动收起", "同上：左键点击那一项被跳过");
                        }
                        else
                        {
                            string pickedDetail;
                            if (pickedHandle == IntPtr.Zero)
                            {
                                pickedDetail = "3 秒内没有收到选中事件（" + picker.LeftDiagnostic
                                               + "；" + TestInput.LastLeftClickObservation + "）";
                            }
                            else if (pickedHandle == hwnd)
                            {
                                pickedDetail = $"picked=0x{pickedHandle.ToInt64():X}";
                            }
                            else
                            {
                                // 拾取到了别的窗口时，必须说清是哪一个 —— 否则只看到一个十六进制句柄，
                                // 根本无从判断是拾取器自己的边框、主面板，还是别的顶层窗口。
                                WindowInfo hit = WindowInfo.Describe(pickedHandle);
                                pickedDetail = $"picked=0x{pickedHandle.ToInt64():X}（期望 0x{hwnd.ToInt64():X}）实际命中："
                                               + (hit.IsValid ? hit.ShortDescription : "窗口已失效");
                            }

                            Check("左键点击能选中被框住的窗口", picked && pickedHandle == hwnd, pickedDetail);
                            Check("选中后拾取器自动收起", !picker.FrameWindow.Visible,
                                $"可见={picker.FrameWindow.Visible}");
                        }
                    }

                    // 取消路径：右键必须能退出拾取且不选中任何窗口
                    if (!uiReachable)
                    {
                        Skip("右键能取消拾取且不选中窗口", uiSkipReason);
                        picker.Stop();
                    }
                    else if (!WaitUntil(() => !TestInput.IsLeftDown(), TimeSpan.FromMilliseconds(800)))
                    {
                        // 拾取器判的是「按下」这个边沿：左键一直按着时，合成的右键会被读成
                        // 「左键按下了」而变成选中，看上去就像右键功能坏了 —— 如实记 SKIP。
                        Skip("右键能取消拾取且不选中窗口",
                            "自检期间左键一直处于按下状态（用户正在用鼠标），无法判定右键行为");
                        picker.Stop();
                    }
                    else if (WindowEnumerator.GetRootWindowUnderCursor() != hwnd)
                    {
                        // 同上：右键也不能合成到用户的全屏游戏里去。
                        Skip("右键能取消拾取且不选中窗口", driftReason);
                        picker.Stop();
                    }
                    else
                    {
                        pickedHandle = IntPtr.Zero;
                        DateTime cancelStart = DateTime.UtcNow;
                        picker.Start();
                        Check("重新启动拾取器（右键取消路径的前提）", picker.IsRunning, $"IsRunning={picker.IsRunning}");
                        WaitUntil(() => { picker.PollNow(); return picker.CurrentHandle == hwnd; }, TimeSpan.FromSeconds(3));
                        WaitUntil(() => (DateTime.UtcNow - cancelStart).TotalMilliseconds > 500, TimeSpan.FromSeconds(2));

                        // 与左键同理：合成必须走后台线程，主线程要一直轮询才看得见按下状态。
                        var rightThread = new Thread(TestInput.RightClick) { IsBackground = true };
                        rightThread.Start();

                        bool cancelledSeen = WaitUntil(() => { picker.PollNow(); return cancelled; },
                            TimeSpan.FromSeconds(3));
                        rightThread.Join(TimeSpan.FromSeconds(2));
                        Note($"拾取器轮询：自检补驱 {picker.TickCount} 次，按键判定见 GetAsyncKeyState 的 0x8000/0x0001 两位");
                        Check("右键能取消拾取且不选中窗口",
                            cancelledSeen && pickedHandle == IntPtr.Zero && !picker.IsRunning,
                            $"cancelled={cancelledSeen} picked=0x{pickedHandle.ToInt64():X}");
                    }
                }

                // ---------- 层级徽标 ----------
                using var badges = new RankBadgeOverlay();
                badges.SetVisible(true);

                PinService.UnpinAll();
                PinService.TryPin(hwnd, out _);
                badges.Refresh(PinService.Items);

                Form? badge = badges.BadgeWindowFor(hwnd);
                UiCheck("层级徽标已贴在置顶窗口上", badges.BadgeCount == 1 && badge is not null,
                    $"count={badges.BadgeCount}");

                if (badge is not null && NativeMethods.GetWindowRect(badge.Handle, out NativeMethods.RECT badgeRect))
                {
                    long badgeEx = NativeMethods
                        .GetWindowLongPtr(badge.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                    UiCheck("徽标点击穿透（WS_EX_TRANSPARENT + 分层 + 不进 Alt-Tab）",
                        (badgeEx & NativeMethods.WS_EX_TRANSPARENT) != 0 &&
                        (badgeEx & NativeMethods.WS_EX_LAYERED) != 0 &&
                        (badgeEx & NativeMethods.WS_EX_TOOLWINDOW) != 0,
                        $"扩展样式=0x{badgeEx:X}");

                    bool insideTarget =
                        badgeRect.Left >= targetRect.Left && badgeRect.Top >= targetRect.Top &&
                        badgeRect.Right <= targetRect.Right && badgeRect.Bottom <= targetRect.Bottom;
                    UiCheck("徽标贴在目标窗口内部左上角", !haveTargetRect || insideTarget,
                        $"徽标={badgeRect.Left},{badgeRect.Top} 目标={targetRect.Left},{targetRect.Top}");

                    // 关键行为验证：光标落在徽标上时，命中的必须仍是目标窗口
                    TestInput.MoveTo(badgeRect.Left + 6, badgeRect.Top + 6);
                    IntPtr underBadge = WindowEnumerator.GetRootWindowUnderCursor();
                    UiCheck("光标压在徽标上时命中的仍是目标窗口（真点击穿透）",
                        underBadge == hwnd,
                        $"命中=0x{underBadge.ToInt64():X} 徽标=0x{badge.Handle.ToInt64():X} 目标=0x{hwnd.ToInt64():X}");

                    // 压栈会把置顶窗口抬到徽标之上，徽标必须能自愈
                    PinService.ReapplyAll("自检：故意压栈");
                    badges.RaiseAll();
                    UiCheck("徽标被压栈盖住后能重新抬到目标之上",
                        ZOrderEnforcer.IsAbove(badge.Handle, hwnd),
                        $"徽标=0x{badge.Handle.ToInt64():X} 目标=0x{hwnd.ToInt64():X}");

                    // 防闪烁回归（用户 m04650）：徽标位置没变、也还在最上层时，
                    // 500ms 定时器每次 RaiseAll 都不该再发 SetWindowPos ——
                    // 那正是「游戏画面上方每秒闪两次」的来源。
                    int badgeWritesBefore = badges.ZOrderWriteCount;
                    for (int round = 0; round < 4; round++)
                    {
                        badges.RaiseAll();
                    }

                    UiCheck("徽标位置没变时连续抬起不再重写 Z 序（防闪烁）",
                        badges.ZOrderWriteCount == badgeWritesBefore,
                        $"写次数 {badgeWritesBefore} → {badges.ZOrderWriteCount}");
                }

                badges.SetVisible(false);
                Check("关闭徽标开关后所有徽标窗口被收掉", badges.BadgeCount == 0,
                    $"count={badges.BadgeCount}");
            }
            catch (Exception ex)
            {
                Check("M4 拾取器与徽标验证过程不应抛出异常", false,
                    $"{ex.GetType().Name}：{ex.Message}");
            }
            finally
            {
                PinService.UnpinAll();
                TestInput.MoveTo(savedX, savedY);
            }

            // ---------- 鼠标拾取（信息项）----------
            IntPtr underCursor = WindowEnumerator.GetRootWindowUnderCursor();
            if (underCursor == IntPtr.Zero)
            {
                Note("鼠标下的顶层窗口：未取到（无人交互时会这样，不作为失败判据）");
            }
            else
            {
                Note("鼠标下的顶层窗口：" + WindowInfo.Describe(underCursor).ShortDescription);
            }
        }
        catch (Exception ex)
        {
            Check("自检过程本身不应抛出异常", false, ex.Message);
            Log.Error("自检过程抛出异常", ex);
            Note(ex.ToString());
            Boot.Trace("自检抛异常：" + ex);
        }

        return Finish(lines, failures, passed, skipped, target);
    }

    /// <summary>
    /// 轮询等待条件成立，期间抽送消息队列。
    /// 抽送是必须的：WinEvent 的 OUTOFCONTEXT 回调与 WinForms 定时器都靠消息队列投递，
    /// 而 --selftest 模式没有 Application.Run，不 DoEvents 就永远收不到。
    /// </summary>
    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Application.DoEvents();
            Thread.Sleep(25);
        }

        return condition();
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>按 PID + 标题轮询查找靶窗口。</summary>
    private static IntPtr WaitForTargetWindow(uint processId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        var stopwatch = Stopwatch.StartNew();
        int rounds = 0;

        while (DateTime.UtcNow < deadline)
        {
            rounds++;

            // 先按「可置顶」枚举（正常路径）
            foreach (WindowInfo info in WindowEnumerator.EnumeratePinnable())
            {
                if (info.ProcessId == processId && info.Title == TestWindow.SelfTestTitle)
                {
                    return info.Handle;
                }
            }

            // 再退回原始枚举：万一靶窗口被过滤掉，仍要能定位并报告
            foreach (IntPtr candidate in WindowEnumerator.EnumerateTopLevel())
            {
                WindowInfo info = WindowInfo.Describe(candidate);
                if (info.ProcessId == processId && info.Title == TestWindow.SelfTestTitle)
                {
                    return candidate;
                }
            }

            Thread.Sleep(200);
        }

        Boot.Trace($"靶窗口未命中：轮数={rounds} 耗时={stopwatch.ElapsedMilliseconds}ms");
        return IntPtr.Zero;
    }

    private static int Finish(
        List<string> lines, List<string> failures, int passed, int skipped, Process? target)
    {
        Boot.Trace($"Finish 进入 明细行={lines.Count} 失败项={failures.Count}");

        try
        {
            PinService.UnpinAll();

        }
        catch
        {
            // 忽略
        }

        try
        {
            if (target is { HasExited: false })
            {
                target.Kill();
                target.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            lines.Add("INFO | 关闭靶进程时出错：" + ex.Message);
        }
        finally
        {
            target?.Dispose();
        }

        int failed = failures.Count;
        lines.Add($"{Marker} result {(failed == 0 ? "ALL-PASS" : "FAILED")} " +
                  $"pass={passed} skip={skipped} fail={failed}");

        foreach (string name in failures) lines.Add($"FAILED-CHECK | {name}");

        lines.Add($"{Marker} end {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string report = string.Join(Environment.NewLine, lines) + Environment.NewLine;

        WriteReport(report);
        WriteToParentConsole(report);

        return failed == 0 ? 0 : 1;
    }

    private static void WriteReport(string report)
    {
        try
        {
            string path = ShadowPaths.SelfTestLogPath;
            string header = new string('-', 72) + Environment.NewLine;
            File.AppendAllText(path, header + report + header, Encoding.UTF8);
            Boot.Trace($"报告已写入 {path}（{report.Length} 字符）");
        }
        catch (Exception ex)
        {
            Boot.Trace($"报告写入失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// WinExe 进程默认没有控制台。附加到父进程（PowerShell）的控制台，
    /// 让自检结果能直接打在命令行里。
    /// 注意：AttachConsole 失败不代表标准句柄不可用（父进程重定向时句柄是继承来的），
    /// 所以失败也要继续尝试写一次。
    /// </summary>
    private static void WriteToParentConsole(string report)
    {
        try
        {
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

            using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            stdout.WriteLine();
            stdout.WriteLine(report);
            stdout.Flush();
        }
        catch (Exception ex)
        {
            Boot.Trace("写父控制台失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }
}
