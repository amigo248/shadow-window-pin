using System.Text.Encodings.Web;
using System.Text.Json;
using Shadow.Core;

namespace Shadow.Persistence;

/// <summary>一条置顶记录。注意这里**不存 HWND**：HWND 会被系统回收复用，
/// 重启后靠「进程名 + 窗口类 + 标题」重新找回窗口。</summary>
internal sealed class PinnedEntryDto
{
    public string ProcessName { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>透明度百分比（10–100），100 = 不透明。</summary>
    public int Opacity { get; set; } = 100;

    public bool ClickThrough { get; set; }

    /// <summary>上次记住的窗口位置与大小（用户手动「记住位置」或退出时自动记录）。</summary>
    public GeometryDto? Geometry { get; set; }
}

/// <summary>窗口位置与大小。</summary>
internal sealed class GeometryDto
{
    public int Left { get; set; }

    public int Top { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsUsable => Width >= 80 && Height >= 60;
}

/// <summary>一个场景 = 一批窗口 + 它们的层级顺序/外观，可一键切换。</summary>
internal sealed class SceneDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>顺序即层级：第 0 个在最上层。</summary>
    public List<PinnedEntryDto> Windows { get; set; } = new();
}

/// <summary>
/// 一条自动置顶规则。
/// Pattern 是正则（忽略大小写），Target 决定拿进程名还是窗口标题去比。
/// 只做「pin / exclude」两类动作，够用且不会让用户配出互相矛盾的规则。
/// </summary>
internal sealed class RuleDto
{
    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    /// <summary>"process"（进程名）或 "title"（窗口标题）。</summary>
    public string Target { get; set; } = "process";

    /// <summary>"pin"（自动置顶）或 "exclude"（永不置顶）。</summary>
    public string Action { get; set; } = "pin";

    /// <summary>自动置顶时放到第几层（1 起）。默认 1。</summary>
    public int Rank { get; set; } = 1;

    /// <summary>自动置顶时顺带设置的外观。</summary>
    public int Opacity { get; set; } = 100;

    public bool ClickThrough { get; set; }
}

/// <summary>配置文件模型（%APPDATA%\Shadow\config.json，便携模式下落在 exe 目录）。</summary>
internal sealed class Profile
{
    public int Version { get; set; } = 1;

    /// <summary>严格层级模式：谁抢到前面都压回去。</summary>
    public bool StrictMode { get; set; } = true;

    /// <summary>开机自启（走任务计划「以最高权限运行」，不弹 UAC）。</summary>
    public bool AutoStart { get; set; }

    /// <summary>实际生效的热键描述，仅供界面回显。</summary>
    public string ToggleHotkey { get; set; } = string.Empty;

    public string UnpinAllHotkey { get; set; } = string.Empty;

    /// <summary>用户自定义的热键（形如 "Ctrl+Alt+P"）；为空表示用内置候选表自动挑一个可用的。</summary>
    public string ToggleHotkeyOverride { get; set; } = string.Empty;

    public string UnpinAllHotkeyOverride { get; set; } = string.Empty;

    /// <summary>场景分组（一键切换工作区）。</summary>
    public List<SceneDto> Scenes { get; set; } = new();

    /// <summary>自动置顶规则（按进程名/标题正则匹配）。</summary>
    public List<RuleDto> Rules { get; set; } = new();

    public int PanelWidth { get; set; } = 660;

    public int PanelHeight { get; set; } = 560;

    /// <summary>
    /// 是否在每个已置顶窗口的左上角贴层号徽标。
    /// 默认开：「自定义层级」是这工具的核心功能，不把层号画出来用户根本看不出顺序。
    /// </summary>
    public bool ShowRankBadges { get; set; } = true;

    /// <summary>
    /// 是否启用命令行接口（命名管道 Shadow.Command.v1）。
    /// 默认开：脚本化调用是这项功能的卖点之一；在意攻击面的人可以在「更多 ▾」里关掉。
    /// </summary>
    public bool EnableCli { get; set; } = true;

    /// <summary>
    /// 本次运行正在置顶的窗口（每次列表变化都写）。崩溃后靠它找出「谁还留在 topmost 层带里」。
    /// 正常退出时会被清空。
    /// </summary>
    public List<PinnedEntryDto> PinnedWindows { get; set; } = new();

    /// <summary>
    /// 最近一次「非空」的置顶清单，供主面板的「恢复上次列表」使用。
    /// 与 PinnedWindows 分开是因为：正常退出会把正在置顶的清单清空，
    /// 但用户重启后往往还想一键把那批窗口钉回去。
    /// </summary>
    public List<PinnedEntryDto> LastSession { get; set; } = new();
}

/// <summary>
/// 配置读写 + 运行标记。
///
/// 两条健壮性原则：
/// 1. 写配置必须「先写临时文件再原子替换」，否则断电/崩溃会留下半个 JSON，下次启动直接读不出来。
/// 2. 读配置绝不因为文件损坏而崩：备份坏文件、退回默认值继续跑。
/// </summary>
internal static class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // 不转义非 ASCII：中文窗口标题在配置里要能直接看懂
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static Profile? _current;

    internal static string FilePath => Path.Combine(ShadowPaths.DataDirectory, "config.json");

    internal static string CorruptFilePath => Path.Combine(ShadowPaths.DataDirectory, "config.corrupt.json");

    /// <summary>运行标记文件：正常退出时删除，存在即说明上次是崩溃/被强杀。</summary>
    internal static string RunningMarkerPath => Path.Combine(ShadowPaths.DataDirectory, "running.marker");

    internal static Profile Current => _current ??= Load();

    internal static Profile Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Log.Info($"没有配置文件，使用默认配置：{FilePath}");
                return new Profile();
            }

            string json = File.ReadAllText(FilePath);
            Profile? profile = JsonSerializer.Deserialize<Profile>(json, Options);

            if (profile is null)
            {
                Log.Warn("配置反序列化结果为 null，改用默认配置");
                return new Profile();
            }

            profile.PinnedWindows ??= new List<PinnedEntryDto>();
            profile.LastSession ??= new List<PinnedEntryDto>();
            profile.Scenes ??= new List<SceneDto>();
            profile.Rules ??= new List<RuleDto>();

            foreach (SceneDto scene in profile.Scenes) scene.Windows ??= new List<PinnedEntryDto>();

            Log.Info($"已读取配置：{FilePath}（本次置顶记录 {profile.PinnedWindows.Count} 条，" +
                     $"上次清单 {profile.LastSession.Count} 条，场景 {profile.Scenes.Count} 个，" +
                     $"规则 {profile.Rules.Count} 条）");
            return profile;
        }
        catch (Exception ex)
        {
            Log.Warn($"配置读取失败（{ex.GetType().Name}：{ex.Message}），改用默认配置");

            try
            {
                File.Copy(FilePath, CorruptFilePath, overwrite: true);
                Log.Warn($"已将损坏的配置备份为 {CorruptFilePath}");
            }
            catch
            {
                // 备份失败也无所谓，重要的是能继续跑
            }

            return new Profile();
        }
    }

    internal static void SetCurrent(Profile profile) => _current = profile;

    internal static bool Save(Profile profile)
    {
        try
        {
            string json = JsonSerializer.Serialize(profile, Options);
            string temp = FilePath + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"配置保存失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    internal static bool SaveCurrent() => Save(Current);

    /// <summary>上次是否非正常退出。为真则桌面可能残留被钉住的窗口，需要修复。</summary>
    internal static bool DetectUncleanExit()
    {
        try
        {
            if (!File.Exists(RunningMarkerPath)) return false;

            string text = File.ReadAllText(RunningMarkerPath).Trim();
            Log.Warn($"检测到上次运行未正常退出（运行标记：{text}），将修复残留的置顶状态");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"无法判断上次运行状态：{ex.Message}");
            return false;
        }
    }

    internal static void MarkRunning()
    {
        try
        {
            File.WriteAllText(
                RunningMarkerPath,
                $"pid={Environment.ProcessId} 启动于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Log.Warn($"无法写入运行标记：{ex.Message}");
        }
    }

    internal static void ClearRunningMarker()
    {
        try
        {
            if (File.Exists(RunningMarkerPath)) File.Delete(RunningMarkerPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"无法删除运行标记：{ex.Message}");
        }
    }
}
