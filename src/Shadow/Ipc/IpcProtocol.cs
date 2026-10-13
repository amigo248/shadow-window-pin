using System.Text.Json.Serialization;

namespace Shadow.Ipc;

/// <summary>
/// 命令行接口的消息结构。GUI 与 CLI 两侧共用同一份定义
/// （ShadowCli.csproj 把本文件以链接的方式编进去），避免两边协议漂移。
///
/// 为什么用命名管道而不是窗口消息：Shadow 以管理员身份运行，
/// 普通权限的 CLI 向提权进程发 SendMessage / WM_COPYDATA 会被 UIPI 直接拦掉；
/// 命名管道是内核对象，不受 UIPI 限制，也不需要给 CLI 提权。
/// </summary>
internal sealed class IpcRequest
{
    /// <summary>list / pin / unpin / toggle / scene / appearance / status</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>窗口标题的正则（与 Process 至少给一个）。</summary>
    public string? Title { get; set; }

    /// <summary>进程名的正则。</summary>
    public string? Process { get; set; }

    /// <summary>十六进制窗口句柄（精确指定时用，优先级最高）。</summary>
    public string? Handle { get; set; }

    /// <summary>场景名。</summary>
    public string? Scene { get; set; }

    /// <summary>scene save &lt;name&gt; 时表示保存。</summary>
    public bool Save { get; set; }

    /// <summary>unpin --all。</summary>
    public bool All { get; set; }

    /// <summary>pin 之后把第一个匹配的窗口移到第 N 层。</summary>
    public int? Rank { get; set; }

    /// <summary>appearance 命令：透明度。</summary>
    public int? Opacity { get; set; }

    /// <summary>appearance 命令：点击穿透。</summary>
    public bool? ClickThrough { get; set; }
}

/// <summary>一条置顶窗口信息（list 命令的输出）。</summary>
internal sealed class IpcItem
{
    public int Rank { get; set; }

    public string Handle { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Process { get; set; } = string.Empty;

    public uint Pid { get; set; }

    public bool Topmost { get; set; }

    public int Opacity { get; set; } = 100;

    public bool ClickThrough { get; set; }
}

/// <summary>命令执行结果。</summary>
internal sealed class IpcResponse
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool Running { get; set; } = true;

    public int PinnedCount { get; set; }

    public bool StrictMode { get; set; }

    public List<IpcItem> Items { get; set; } = new();

    internal static IpcResponse Fail(string message) => new() { Ok = false, Message = message };
}

/// <summary>协议常量。改动协议时同步改这里，别散落在各处硬编码。</summary>
internal static class IpcProtocol
{
    /// <summary>
    /// 管道名。故意不带 <c>Local\</c> 命名空间前缀：那会让 <c>NamedPipeClientStream</c>
    /// 拼出的设备路径多一层反斜杠，容易在客户端侧变成「连不上」这种难查的故障。
    /// 真正的访问控制交给管道的安全描述符（只放行当前用户与管理员）。
    /// </summary>
    internal const string PipeName = "Shadow.Command.v1";

    /// <summary>单条消息的上限，防止有人往管道里灌垃圾把内存吃掉。</summary>
    internal const int MaxMessageBytes = 256 * 1024;

    /// <summary>客户端连接超时（毫秒）。连不上通常意味着 Shadow 没在运行。</summary>
    internal const int ConnectTimeoutMs = 3000;
}

/// <summary>
/// IPC 消息的「源生成」序列化上下文。
///
/// 为什么不用反射版 <c>JsonSerializer.Serialize&lt;T&gt;</c>：CLI 用 <c>PublishTrimmed</c>
/// 发布（10 MB 对比 34 MB），裁剪器看不见反射用的属性，会把它们当死代码删掉 ——
/// 结果是运行期序列化出空对象，而且只在裁剪版里复现（IL2026 就是警告这件事）。
/// 源生成上下文在编译期把元数据固化成代码，既不受裁剪影响，也省掉运行期反射开销。
///
/// 两侧共用同一份定义与同一套选项，所以 GUI 与 CLI 的线格式不可能漂移。
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(IpcItem))]
internal sealed partial class IpcJsonContext : JsonSerializerContext
{
}
