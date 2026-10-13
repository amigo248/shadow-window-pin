using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shadow.Ipc;

namespace ShadowCli;

/// <summary>
/// Shadow 的命令行接口客户端。
///
/// 它自己不做任何窗口操作，只把命令打成一行 JSON 通过命名管道发给 GUI 进程，
/// 再把结果打印出来。这样「唯一的层级表」始终只在 GUI 进程里，
/// 不会出现 CLI 与 GUI 各自压栈互相打架的情况。
///
/// 用法：
///   shadowcli list
///   shadowcli pin --title "^Notepad$" --rank 1
///   shadowcli unpin --all
///   shadowcli toggle
///   shadowcli scene 写代码
///   shadowcli scene save 写代码
///   shadowcli appearance --title "^Notepad$" --opacity 70 --click-through
///   shadowcli appearance --opacity 70          （不给选择器 = 改全部置顶窗口）
///   shadowcli status
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            // 重定向到文件时按 UTF-8 写，中文才不会变成乱码。
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 某些宿主（旧 conhost / 重定向到奇怪的后端）不允许改编码，忽略即可。
        }

        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        string first = args[0].TrimStart('-', '/').ToLowerInvariant();
        if (first is "help" or "h" or "?")
        {
            PrintHelp();
            return 0;
        }

        if (!TryBuildRequest(args, out IpcRequest request, out bool rawJson, out string error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            PrintHelp();
            return 1;
        }

        return Send(request, rawJson);
    }

    private static bool TryBuildRequest(string[] args, out IpcRequest request, out bool rawJson, out string error)
    {
        request = new IpcRequest();
        rawJson = false;
        error = string.Empty;

        string command = args[0].TrimStart('-', '/').ToLowerInvariant();
        request.Command = command;

        // 位置参数：scene 的场景名（shadowcli scene save 写代码 / shadowcli scene 写代码）
        var positional = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--json")
            {
                rawJson = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string name = arg[2..].ToLowerInvariant();
                string? value = null;
                if (name.Contains('='))
                {
                    int split = name.IndexOf('=');
                    value = name[(split + 1)..];
                    name = name[..split];
                }

                switch (name)
                {
                    case "title" or "t":
                        request.Title = Take(name, value, args, ref i, out error);
                        break;
                    case "process" or "exe" or "p":
                        request.Process = Take(name, value, args, ref i, out error);
                        break;
                    case "handle" or "hwnd" or "h":
                        request.Handle = Take(name, value, args, ref i, out error);
                        break;
                    case "rank" or "r":
                        if (!TryTakeInt(name, value, args, ref i, out int rank, out error))
                        {
                            return false;
                        }

                        request.Rank = rank;
                        break;
                    case "opacity" or "alpha" or "o":
                        if (!TryTakeInt(name, value, args, ref i, out int opacity, out error))
                        {
                            return false;
                        }

                        request.Opacity = opacity;
                        break;
                    case "click-through" or "through" or "clickthrough":
                        request.ClickThrough = value is null || IsTrue(value);
                        break;
                    case "no-click-through":
                        request.ClickThrough = false;
                        break;
                    case "all":
                        request.All = value is null || IsTrue(value);
                        break;
                    case "save":
                        request.Save = value is null || IsTrue(value);
                        break;
                    case "scene" or "workspace":
                        request.Save = false;
                        request.Scene = Take(name, value, args, ref i, out error);
                        break;
                    default:
                        error = $"看不懂的参数：{arg}";
                        return false;
                }

                if (error.Length > 0)
                {
                    return false;
                }

                continue;
            }

            positional.Add(arg);
        }

        // scene 的位置参数：第一个是 save（可选），其余拼成场景名（允许带空格）。
        if (command is "scene" or "workspace")
        {
            if (positional.Count > 0 && positional[0].Equals("save", StringComparison.OrdinalIgnoreCase))
            {
                request.Save = true;
                positional.RemoveAt(0);
            }
            else if (positional.Count > 0 && positional[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                positional.RemoveAt(0);
            }

            if (request.Scene is null && positional.Count > 0)
            {
                request.Scene = string.Join(' ', positional);
            }
        }
        else if (command is "list" or "status" or "ping" or "toggle" or "exit" or "quit" or "restore")
        {
            if (positional.Count > 0)
            {
                error = $"命令 {command} 不接受参数：{string.Join(' ', positional)}";
                return false;
            }
        }
        else if (positional.Count > 0)
        {
            error = $"看不懂的参数：{string.Join(' ', positional)}（匹配窗口请用 --title / --process / --handle）";
            return false;
        }

        if (command is "pin" && request.Handle is null && request.Title is null && request.Process is null)
        {
            error = "pin 需要至少给出 --title / --process / --handle 之一";
            return false;
        }

        if (command is "unpin" or "unpinall")
        {
            request.Command = "unpin";
            if (!request.All && request.Handle is null && request.Title is null && request.Process is null)
            {
                error = "unpin 需要 --all，或 --title / --process / --handle 之一";
                return false;
            }
        }

        return true;
    }

    private static string Take(string name, string? inline, string[] args, ref int index, out string error)
    {
        error = string.Empty;
        if (inline is not null)
        {
            return inline;
        }

        if (index + 1 >= args.Length)
        {
            error = $"参数 --{name} 缺少取值";
            return string.Empty;
        }

        index++;
        return args[index];
    }

    private static bool TryTakeInt(string name, string? inline, string[] args, ref int index, out int value, out string error)
    {
        value = 0;
        error = string.Empty;
        string raw = Take(name, inline, args, ref index, out error);
        if (error.Length > 0)
        {
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"参数 --{name} 需要一个整数，收到「{raw}」";
            return false;
        }

        return true;
    }

    private static bool IsTrue(string value)
    {
        string text = value.Trim().ToLowerInvariant();
        return text is not ("0" or "false" or "no" or "off");
    }

    private static int Send(IpcRequest request, bool rawJson)
    {
        string payload = JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest);

        try
        {
            using var client = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut);
            client.Connect(IpcProtocol.ConnectTimeoutMs);

            var utf8 = new UTF8Encoding(false);
            using var writer = new StreamWriter(client, utf8, bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            writer.WriteLine(payload);

            // 读也要有超时：界面被模态对话框挡住时，服务端会等满自己的 10 秒，
            // 这里若不设限，脚本就会永远停在这一行。
            Task<string?> pending = reader.ReadLineAsync();
            if (!pending.Wait(TimeSpan.FromSeconds(15)))
            {
                Console.Error.WriteLine("Shadow 没有在 15 秒内回应（界面可能正被对话框挡住）。");
                return 1;
            }

            string? line = pending.Result;
            if (line is null)
            {
                Console.Error.WriteLine("Shadow 没有返回结果（连接被关闭）。");
                return 1;
            }

            if (rawJson)
            {
                Console.WriteLine(line);
            }

            IpcResponse? response = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcResponse);
            if (response is null)
            {
                Console.Error.WriteLine($"收到的响应无法解析：{line}");
                return 1;
            }

            if (!rawJson)
            {
                Print(response);
            }

            return response.Ok ? 0 : 1;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("连不上 Shadow：它没有在运行，或者在主面板「更多 ▾」里关掉了命令行接口。");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("被系统拒绝了：Shadow 正在以管理员身份运行，而当前用户与它不同。请用同一个账号登录后重试。");
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"与 Shadow 通信失败：{ex.Message}");
            return 1;
        }
    }

    private static void Print(IpcResponse response)
    {
        if (response.Items.Count > 0)
        {
            PrintTable(response.Items);
            Console.WriteLine();
        }

        TextWriter target = response.Ok ? Console.Out : Console.Error;
        target.WriteLine(response.Message);
    }

    private static void PrintTable(List<IpcItem> items)
    {
        const int rankWidth = 4;
        int titleWidth = Math.Max(DisplayWidth("窗口"), items.Max(item => DisplayWidth(item.Title)));
        int processWidth = Math.Max(DisplayWidth("进程"), items.Max(item => DisplayWidth($"{item.Process} ({item.Pid})")));
        int handleWidth = Math.Max(DisplayWidth("句柄"), items.Max(item => DisplayWidth(item.Handle)));
        titleWidth = Math.Min(titleWidth, 60);

        Console.WriteLine(
            Pad("层级", rankWidth) + ' ' +
            Pad("窗口", titleWidth) + ' ' +
            Pad("进程", processWidth) + ' ' +
            Pad("句柄", handleWidth) + "  外观");
        Console.WriteLine(new string('-', rankWidth + titleWidth + processWidth + handleWidth + 12));

        foreach (IpcItem item in items)
        {
            string appearance = item.Opacity >= 100
                ? (item.ClickThrough ? "不透明 · 点击穿透" : "不透明")
                : $"{item.Opacity}%" + (item.ClickThrough ? " · 点击穿透" : string.Empty);

            string handle = item.Handle + (item.Topmost ? string.Empty : " (未生效)");
            Console.WriteLine(
                Pad("#" + item.Rank.ToString(CultureInfo.InvariantCulture), rankWidth) + ' ' +
                Pad(Truncate(item.Title, titleWidth), titleWidth) + ' ' +
                Pad(Truncate($"{item.Process} ({item.Pid})", processWidth), processWidth) + ' ' +
                Pad(handle, handleWidth) + "  " + appearance);
        }
    }

    /// <summary>
    /// 中日韩字符在等宽字体里占两列，按字符数补空格会错位，所以自己算显示宽度。
    /// </summary>
    private static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (char ch in text)
        {
            width += ch >= 0x1100 && (ch <= 0x115F || ch == 0x2329 || ch == 0x232A || (ch >= 0x2E80 && ch <= 0xA4CF) || (ch >= 0xAC00 && ch <= 0xD7A3) || (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFE30 && ch <= 0xFE6F) || (ch >= 0xFF00 && ch <= 0xFF60) || (ch >= 0xFFE0 && ch <= 0xFFE6))
                ? 2
                : 1;
        }

        return width;
    }

    private static string Pad(string text, int width)
    {
        int padding = width - DisplayWidth(text);
        return padding > 0 ? text + new string(' ', padding) : text;
    }

    private static string Truncate(string text, int width)
    {
        if (DisplayWidth(text) <= width)
        {
            return text;
        }

        var builder = new StringBuilder();
        int used = 0;
        foreach (char ch in text)
        {
            int w = DisplayWidth(ch.ToString());
            if (used + w > width - 1)
            {
                break;
            }

            builder.Append(ch);
            used += w;
        }

        return builder.Append('…').ToString();
    }

    private static void PrintHelp()
    {
        string exe = Process.GetCurrentProcess().ProcessName;
        Console.WriteLine($"Shadow 命令行接口（用法：{exe} <命令> [参数]）");
        Console.WriteLine();
        Console.WriteLine("命令：");
        Console.WriteLine("  status                              查看 Shadow 是否在运行、置顶了几个窗口");
        Console.WriteLine("  list                                按层级列出当前置顶的窗口");
        Console.WriteLine("  pin     --title <正则> [--rank N]   置顶匹配的窗口；--rank 指定层级（1 = 最上层）");
        Console.WriteLine("  pin     --process <正则>            按进程名匹配（也可给 --handle 0x1234 精确指定）");
        Console.WriteLine("  unpin   --all                       取消全部置顶");
        Console.WriteLine("  unpin   --title <正则>              取消匹配窗口的置顶");
        Console.WriteLine("  toggle                              置顶 / 取消当前前台窗口");
        Console.WriteLine("  scene                               列出所有场景");
        Console.WriteLine("  scene   <名称>                      切换到场景");
        Console.WriteLine("  scene   save <名称>                 把当前置顶列表保存成场景");
        Console.WriteLine("  appearance --title <正则> --opacity <10-100> [--click-through]");
        Console.WriteLine("                                      改透明度 / 点击穿透（窗口必须已在置顶列表里）");
        Console.WriteLine("  appearance --opacity <10-100>       不给选择器 / 给 --all 时改全部置顶窗口");
        Console.WriteLine("  restore                             按上次的清单恢复置顶（等价于主面板同名按钮）");
        Console.WriteLine("  panel                               打开主面板（等价于双击托盘图标）");
        Console.WriteLine("  exit                                让 Shadow 正常退出（会先把所有窗口还原）");
        Console.WriteLine();
        Console.WriteLine("通用参数：");
        Console.WriteLine("  --title / --process 是 .NET 正则，不区分大小写，分别匹配窗口标题与进程名");
        Console.WriteLine("  --handle 0x1234     直接指定窗口句柄（优先于正则）");
        Console.WriteLine("  --json              输出原始 JSON（给脚本用）");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine($"  {exe} pin --process notepad --rank 1");
        Console.WriteLine($"  {exe} pin --title \"^Visual Studio Code$\" --opacity 85");
        Console.WriteLine($"  {exe} scene save 写代码");
        Console.WriteLine($"  {exe} scene 写代码");
    }
}
