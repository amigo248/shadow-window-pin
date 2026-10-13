using System.Globalization;
using System.Text.RegularExpressions;
using Shadow.Core;
using Shadow.Persistence;

namespace Shadow.Ipc;

/// <summary>
/// 命令行接口的命令执行器（跑在 GUI 进程里）。
///
/// 为什么要放在 GUI 侧而不是 CLI 侧：所有窗口操作都要经过 PinService 那份唯一的层级表，
/// 让 CLI 自己去 SetWindowPos 会和 GUI 的压栈逻辑打架。CLI 只负责组包与打印。
/// </summary>
internal static class CommandProcessor
{
    private const int RegexTimeoutMs = 200;

    private const string CommandList =
        "list / pin / unpin / toggle / scene / appearance / restore / panel / exit / status";

    /// <summary>
    /// 退出应用的回调。由 ShadowApp 在启动时挂上 —— 命令执行器不认识应用外壳，
    /// 也不想为了一条命令就把它整个传进来。
    /// </summary>
    internal static Action? ShutdownRequested { get; set; }

    /// <summary>「恢复上次列表」的回调，同样由 ShadowApp 挂上。</summary>
    internal static Action? RestoreRequested { get; set; }

    /// <summary>
    /// 「打开主面板」的回调。放在这里是因为：面板被别的置顶窗口盖住时，
    /// **外部进程根本抬不动它**（Windows 只允许拥有前台权限的进程抢先），
    /// 只有 Shadow 自己调用 TopMost 才能让它现身。
    /// </summary>
    internal static Action? PanelRequested { get; set; }

    internal static IpcResponse Execute(IpcRequest request)
    {
        try
        {
            string command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
            return command switch
            {
                "status" or "ping" => Status(),
                "list" => List(),
                "pin" => Pin(request),
                "unpin" or "unpinall" => Unpin(request),
                "toggle" => Toggle(),
                "scene" or "workspace" => Scene(request),
                "appearance" or "alpha" => Appearance(request),
                "restore" or "resume" => Restore(),
                "panel" or "open" or "show" => Panel(),
                "exit" or "quit" or "shutdown" => Exit(),
                "" => IpcResponse.Fail($"缺少命令；可用：{CommandList}"),
                _ => IpcResponse.Fail($"未知命令「{request.Command}」；可用：{CommandList}"),
            };
        }
        catch (Exception ex)
        {
            // 命令执行失败绝不能把 GUI 带崩：这里兜住所有异常并如实回报。
            Log.Warn($"命令行接口执行失败：{ex.GetType().Name} {ex.Message}");
            return IpcResponse.Fail($"执行失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static IpcResponse Status()
    {
        IpcResponse response = new()
        {
            Ok = true,
            Running = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = PinService.PinnedCount == 0
                ? $"Shadow 正在运行，当前没有置顶窗口（{(PinService.StrictMode ? "严格层级" : "宽松层级")}模式）"
                : $"Shadow 正在运行，当前置顶 {PinService.PinnedCount} 个窗口（{(PinService.StrictMode ? "严格层级" : "宽松层级")}模式）",
        };
        response.Items.AddRange(Snapshot());
        return response;
    }

    private static IpcResponse List()
    {
        IpcResponse response = new()
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = PinService.PinnedCount == 0 ? "当前没有置顶窗口" : $"当前置顶 {PinService.PinnedCount} 个窗口",
        };
        response.Items.AddRange(Snapshot());
        return response;
    }

    private static IpcResponse Pin(IpcRequest request)
    {
        if (!TryResolve(request, pinnableOnly: true, out List<WindowInfo> matches, out string error))
        {
            return IpcResponse.Fail(error);
        }

        int pinned = 0;
        int skipped = 0;
        var notes = new List<string>();
        foreach (WindowInfo info in matches)
        {
            if (!PinService.TryPin(info.Handle, out string message))
            {
                skipped++;
                notes.Add(message);
                continue;
            }

            pinned++;
            if (request.Opacity is not null || request.ClickThrough is not null)
            {
                int opacity = Math.Clamp(request.Opacity ?? 100, WindowAppearance.MinOpacity, WindowAppearance.MaxOpacity);
                PinService.SetAppearance(info.Handle, opacity, request.ClickThrough ?? false, out string appearanceMessage);
                if (!string.IsNullOrEmpty(appearanceMessage) && notes.Count < 3)
                {
                    notes.Add(appearanceMessage);
                }
            }
        }

        if (pinned == 0)
        {
            return IpcResponse.Fail(notes.Count > 0 ? string.Join("；", notes) : "没有窗口被置顶");
        }

        if (request.Rank is int rank && matches.Count > 0)
        {
            int target = Math.Clamp(rank, 1, Math.Max(1, PinService.PinnedCount));
            PinService.MoveToRank(matches[0].Handle, target);
        }

        IpcResponse response = new()
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = skipped == 0
                ? $"已置顶 {pinned} 个窗口"
                : $"已置顶 {pinned} 个窗口，{skipped} 个未处理",
        };
        response.Items.AddRange(Snapshot());
        return response;
    }

    private static IpcResponse Unpin(IpcRequest request)
    {
        if (request.All)
        {
            int removed = PinService.UnpinAll();
            IpcResponse all = new()
            {
                Ok = true,
                PinnedCount = PinService.PinnedCount,
                StrictMode = PinService.StrictMode,
                Message = removed == 0 ? "原本就没有置顶窗口" : $"已取消全部置顶（{removed} 个窗口）",
            };
            return all;
        }

        if (!TryResolve(request, pinnableOnly: false, out List<WindowInfo> matches, out string error))
        {
            return IpcResponse.Fail(error);
        }

        int count = 0;
        var notes = new List<string>();
        foreach (WindowInfo info in matches)
        {
            if (PinService.TryUnpin(info.Handle, out string message))
            {
                count++;
            }
            else if (notes.Count < 3)
            {
                notes.Add(message);
            }
        }

        if (count == 0)
        {
            return IpcResponse.Fail(notes.Count > 0 ? string.Join("；", notes) : "没有窗口被取消置顶");
        }

        IpcResponse response = new()
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = $"已取消 {count} 个窗口的置顶",
        };
        return response;
    }

    private static IpcResponse Toggle()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IpcResponse.Fail("取不到前台窗口");
        }

        WindowInfo info = WindowInfo.Describe(foreground);
        if (!info.IsValid)
        {
            return IpcResponse.Fail("前台窗口无效");
        }

        if (!PinService.IsTracked(foreground) && !WindowEnumerator.IsPinnable(info, out string reason))
        {
            return IpcResponse.Fail(reason);
        }

        if (!PinService.ToggleTopmost(foreground, out bool topmostNow))
        {
            return IpcResponse.Fail($"切换失败：{info.DisplayName}");
        }

        IpcResponse response = new()
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = topmostNow
                ? $"已置顶「{info.DisplayName}」（层级 #{PinService.RankOf(foreground)}）"
                : $"已取消「{info.DisplayName}」的置顶",
        };
        response.Items.AddRange(Snapshot());
        return response;
    }

    private static IpcResponse Scene(IpcRequest request)
    {
        string? name = request.Scene?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            List<string> names = SceneManager.All.Select(scene => scene.Name).ToList();
            return new IpcResponse
            {
                Ok = true,
                PinnedCount = PinService.PinnedCount,
                Message = names.Count == 0
                    ? "还没有保存过场景（在主面板「保存为场景…」或 shadowcli scene save <名称>）"
                    : $"共 {names.Count} 个场景：{string.Join("、", names)}",
            };
        }

        if (request.Save)
        {
            int count = SceneManager.Capture(name).Windows.Count;
            return new IpcResponse
            {
                Ok = true,
                PinnedCount = PinService.PinnedCount,
                Message = $"已把当前 {count} 个置顶窗口保存为场景「{name}」",
            };
        }

        SceneDto? scene = SceneManager.Find(name);
        if (scene is null)
        {
            List<string> names = SceneManager.All.Select(item => item.Name).ToList();
            return IpcResponse.Fail(names.Count == 0
                ? $"没有找到场景「{name}」，当前还没有任何场景"
                : $"没有找到场景「{name}」，现有场景：{string.Join("、", names)}");
        }

        int restored = SceneManager.Apply(name, out List<string> missing);
        IpcResponse response = new()
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = missing.Count == 0
                ? $"已切换到场景「{name}」（{restored} 个窗口）"
                : $"已切换到场景「{name}」（{restored} 个窗口，{missing.Count} 个没找到：{string.Join("、", missing)}）",
        };
        response.Items.AddRange(Snapshot());
        return response;
    }

    /// <summary>「恢复上次列表」：等价于主面板上的那个按钮，方便开机脚本一键还原工作环境。</summary>
    /// <summary>
    /// 打开主面板。
    ///
    /// 这条命令存在的理由：面板平时收在托盘里，而**面板被别的置顶窗口盖住时，
    /// 外部进程无论用 SetForegroundWindow 还是 SetWindowPos(HWND_TOP) 都抬不动它**
    /// （Windows 只让「拥有最后一次输入的进程」改前台/抬 Z 序，实测两个调用都返回成功但什么也没发生）。
    /// 只有 Shadow 自己把面板置顶才有效，所以把这个动作交给 GUI 侧执行。
    /// </summary>
    private static IpcResponse Panel()
    {
        Action? show = PanelRequested;
        if (show is null)
        {
            return IpcResponse.Fail("当前进程没有挂上「打开主面板」的处理（自检模式或界面未就绪）");
        }

        show();

        return new IpcResponse
        {
            Ok = true,
            Running = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = "已打开主面板（面板会临时置顶几秒，免得被已置顶的窗口压住）",
        };
    }

    private static IpcResponse Restore()
    {
        Action? restore = RestoreRequested;
        if (restore is null)
        {
            return IpcResponse.Fail("当前进程没有挂上「恢复上次列表」的处理（自检模式或界面未就绪）");
        }

        restore();

        IpcResponse response = new()
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            StrictMode = PinService.StrictMode,
            Message = PinService.PinnedCount == 0
                ? "没有可恢复的记录（或记录里的窗口都已经关闭）"
                : $"已恢复 {PinService.PinnedCount} 个窗口的置顶",
        };
        response.Items.AddRange(Snapshot());
        return response;
    }

    /// <summary>
    /// 让 Shadow 正常退出。
    ///
    /// 关键顺序：先把「正在退出」这句回话发回给客户端，再真正退出。
    /// 直接同步退出的话，客户端拿到的是「连接被关闭」而不是「已退出」，
    /// 脚本里根本分不清是命令失败还是工具正常收工。
    /// </summary>
    private static IpcResponse Exit()
    {
        Action? shutdown = ShutdownRequested;
        if (shutdown is null)
        {
            return IpcResponse.Fail("当前进程没有挂上退出处理（自检模式或界面未就绪）");
        }

        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            shutdown();
        };
        timer.Start();

        return new IpcResponse
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            Message = "Shadow 正在退出（会先把所有窗口还原成不置顶）",
        };
    }

    private static IpcResponse Appearance(IpcRequest request)
    {
        if (request.Opacity is null && request.ClickThrough is null)
        {
            return IpcResponse.Fail("需要至少给出 --opacity 或 --click-through");
        }

        // 不给选择器时作用于「当前所有置顶窗口」（--all 等价）：调暗/点击穿透大多是批量操作，
        // 否则用户得先把每个窗口的标题抄出来。给了选择器就只改选择器命中的窗口。
        bool noSelector = string.IsNullOrWhiteSpace(request.Title)
            && string.IsNullOrWhiteSpace(request.Process)
            && string.IsNullOrWhiteSpace(request.Handle);

        List<WindowInfo> matches;
        if (noSelector)
        {
            matches = PinService.Items.Select(item => item.Snapshot).ToList();
            if (matches.Count == 0)
            {
                return IpcResponse.Fail("当前没有置顶窗口，请先置顶窗口或给出 --title / --process / --handle");
            }
        }
        else if (!TryResolve(request, pinnableOnly: false, out matches, out string error))
        {
            return IpcResponse.Fail(error);
        }

        int opacity = Math.Clamp(request.Opacity ?? WindowAppearance.MaxOpacity, WindowAppearance.MinOpacity, WindowAppearance.MaxOpacity);
        int changed = 0;
        var notes = new List<string>();
        foreach (WindowInfo info in matches)
        {
            if (!PinService.IsTracked(info.Handle))
            {
                if (notes.Count < 3)
                {
                    notes.Add($"「{info.DisplayName}」不在置顶列表里");
                }

                continue;
            }

            bool clickThrough = request.ClickThrough ?? PinService.Find(info.Handle)?.ClickThrough ?? false;
            if (PinService.SetAppearance(info.Handle, opacity, clickThrough, out string message))
            {
                changed++;
            }
            else if (notes.Count < 3)
            {
                notes.Add(message);
            }
        }

        if (changed == 0)
        {
            return IpcResponse.Fail(notes.Count > 0 ? string.Join("；", notes) : "没有窗口被修改");
        }

        return new IpcResponse
        {
            Ok = true,
            PinnedCount = PinService.PinnedCount,
            Message = $"已更新 {changed} 个窗口的外观（透明度 {opacity}%{(request.ClickThrough == true ? "，点击穿透开" : string.Empty)}）",
        };
    }

    /// <summary>
    /// 按 --handle / --title / --process 找出目标窗口。
    /// 句柄优先：用户已经从 list 里拿到句柄时，不该再被正则的模糊匹配干扰。
    /// </summary>
    private static bool TryResolve(IpcRequest request, bool pinnableOnly, out List<WindowInfo> matches, out string error)
    {
        matches = new List<WindowInfo>();
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(request.Handle))
        {
            string raw = request.Handle.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[2..];
            }

            if (!long.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value) || value == 0)
            {
                error = $"句柄格式不对：{request.Handle}（应为十六进制，如 0x1A05C2）";
                return false;
            }

            WindowInfo info = WindowInfo.Describe(new IntPtr(value));
            if (!info.IsValid)
            {
                error = $"句柄 {request.Handle} 不是有效窗口（可能已经关闭）";
                return false;
            }

            if (pinnableOnly && !WindowEnumerator.IsPinnable(info, out string reason))
            {
                error = reason;
                return false;
            }

            matches.Add(info);
            return true;
        }

        if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Process))
        {
            error = "需要至少给出 --title / --process / --handle 之一";
            return false;
        }

        Regex? titlePattern = null;
        Regex? processPattern = null;
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            titlePattern = Compile(request.Title!, out error);
            if (titlePattern is null)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Process))
        {
            processPattern = Compile(request.Process!, out error);
            if (processPattern is null)
            {
                return false;
            }
        }

        // EnumerateTopLevel 给的是句柄（要连不可置顶的窗口一起看），EnumeratePinnable 给的是已筛过的信息
        List<WindowInfo> candidates = pinnableOnly
            ? WindowEnumerator.EnumeratePinnable().ToList()
            : WindowEnumerator.EnumerateTopLevel().Select(WindowInfo.Describe).ToList();

        foreach (WindowInfo info in candidates)
        {
            if (!info.IsValid)
            {
                continue;
            }

            if (titlePattern is not null && !titlePattern.IsMatch(info.Title))
            {
                continue;
            }

            if (processPattern is not null && !processPattern.IsMatch(info.ProcessName))
            {
                continue;
            }

            matches.Add(info);
        }

        if (matches.Count == 0)
        {
            error = "没有找到匹配的窗口（--title / --process 是正则，匹配标题与进程名，不区分大小写）";
            return false;
        }

        return true;
    }

    private static Regex? Compile(string pattern, out string error)
    {
        error = string.Empty;
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(RegexTimeoutMs));
        }
        catch (ArgumentException ex)
        {
            error = $"正则无效：{ex.Message}";
            return null;
        }
    }

    private static List<IpcItem> Snapshot()
    {
        var items = new List<IpcItem>();
        foreach (PinnedWindow item in PinService.Items)
        {
            items.Add(new IpcItem
            {
                Rank = item.Rank,
                Handle = $"0x{item.Handle.ToInt64():X}",
                Title = item.Snapshot.Title,
                Process = item.Snapshot.ProcessName,
                Pid = item.ProcessId,
                Topmost = WindowInfo.IsWindowTopmost(item.Handle),
                Opacity = item.Opacity,
                ClickThrough = item.ClickThrough,
            });
        }

        return items;
    }
}
