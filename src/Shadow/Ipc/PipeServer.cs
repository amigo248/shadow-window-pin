using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Shadow.Core;

namespace Shadow.Ipc;

/// <summary>
/// 命名管道服务端：让普通权限的 ShadowCli.exe 能给这个提权进程下命令。
///
/// 为什么不给 CLI 也加 requireAdministrator：那样每次脚本调用都会弹一次 UAC，
/// 完全不能用于自动化。为什么不用窗口消息：UIPI 会拦掉低完整性进程发往高完整性
/// 进程的 SendMessage / WM_COPYDATA，命名管道不受这条限制。
/// </summary>
internal sealed class PipeServer : IDisposable
{
    /// <summary>UI 线程锚点：命令最终要在 UI 线程上执行（托盘、主面板都线程亲和）。</summary>
    private readonly Control _anchor;

    private readonly CancellationTokenSource _cts = new();

    private Thread? _thread;

    private bool _disposed;

    private int _servedCount;

    internal PipeServer(Control anchor)
    {
        _anchor = anchor;
    }

    internal bool Running => _thread is { IsAlive: true };

    internal int ServedCount => Volatile.Read(ref _servedCount);

    internal bool Start(out string detail)
    {
        try
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Shadow.PipeServer",
            };
            _thread.Start();
            detail = $"命令行接口已就绪：{IpcProtocol.PipeName}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"命令行接口启动失败：{ex.GetType().Name} {ex.Message}";
            Log.Warn(detail);
            return false;
        }
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                IAsyncResult pending = server.BeginWaitForConnection(null, null);

                // 用可中断的等待代替阻塞式 WaitForConnection，退出时才能干净地停下来。
                while (!pending.AsyncWaitHandle.WaitOne(250))
                {
                    if (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                }

                server.EndWaitForConnection(pending);
                HandleConnection(server);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested)
                {
                    return;
                }

                Log.Warn($"命令行接口连接异常：{ex.GetType().Name} {ex.Message}");
                Thread.Sleep(200);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream server)
    {
        var utf8 = new UTF8Encoding(false);

        // 显式 using 块 + 外层 try：客户端说断就断（Ctrl+C 掉脚本）时，释放读写器本身
        // 会抛「Pipe is broken」。那是客户端的正常行为，不该在日志里伪装成服务端故障。
        try
        {
            using (var reader = new StreamReader(server, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
            using (var writer = new StreamWriter(server, utf8, bufferSize: 4096, leaveOpen: true) { AutoFlush = true })
            {
                // 客户端连上却不发请求时不能把服务端线程永远占住（一次只服务一个实例）。
                Task<string?> read = reader.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(5)) || read.Result is null)
                {
                    Log.Warn("命令行接口：客户端连接后没有发送请求，已断开");
                    return;
                }

                string request = read.Result;
                if (request.Length > IpcProtocol.MaxMessageBytes)
                {
                    Write(writer, IpcResponse.Fail("请求过大，已拒绝"));
                    return;
                }

                IpcResponse response = Dispatch(request);
                Write(writer, response);
                Interlocked.Increment(ref _servedCount);
                Log.Info($"命令行接口：{Describe(request)} → {(response.Ok ? "OK" : "FAIL")} {response.Message}");
            }
        }
        catch (IOException ex)
        {
            Log.Warn($"命令行接口：客户端提前断开（{ex.Message}）");
        }
        catch (Exception ex)
        {
            Log.Warn($"命令行接口处理失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void Write(StreamWriter writer, IpcResponse response)
    {
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse));
        }
        catch (Exception ex)
        {
            Log.Warn($"命令行接口回写响应失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private IpcResponse Dispatch(string json)
    {
        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcRequest);
        }
        catch (JsonException ex)
        {
            return IpcResponse.Fail($"请求不是合法 JSON：{ex.Message}");
        }

        if (request is null)
        {
            return IpcResponse.Fail("请求为空");
        }

        // 编送到 UI 线程执行：命令会动置顶列表、托盘气泡、主面板列表，这些都不许跨线程碰。
        // 用 BeginInvoke + 轮询而不是 Invoke：界面上只要弹出一个模态对话框（比如「关于」），
        // Invoke 就会把管道线程永久挂住，而 CLI 那边看起来只是「没反应」。
        if (_anchor.IsHandleCreated && _anchor.InvokeRequired)
        {
            IpcResponse? result = null;
            try
            {
                _anchor.BeginInvoke(new Action(() => result = CommandProcessor.Execute(request)));
            }
            catch (Exception ex)
            {
                return IpcResponse.Fail($"界面线程不可用：{ex.GetType().Name} {ex.Message}");
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (result is null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            return result ?? IpcResponse.Fail("界面线程在 10 秒内没有响应，命令未执行（界面可能正被对话框挡住）");
        }

        return CommandProcessor.Execute(request);
    }

    private static string Describe(string json)
    {
        try
        {
            IpcRequest? request = JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcRequest);
            return request is null ? "(空)" : $"{request.Command}";
        }
        catch
        {
            return "(无法解析)";
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        PipeSecurity? security = TryBuildSecurity();
        if (security is null)
        {
            // 拿不到显式 ACL 就用默认 DACL：同用户（只是不提升）的 CLI 依然连得上。
            return new NamedPipeServerStream(IpcProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096);
        }

        return NamedPipeServerStreamAcl.Create(IpcProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
    }

    /// <summary>
    /// 显式放宽 DACL：当前用户可读写，管理员完全控制。
    /// 提权进程创建的管道若沿用令牌默认 DACL，某些机器上会只允许高完整性客户端连接，
    /// 于是「CLI 说 Shadow 没在运行」——那是本项目最容易被误诊的故障之一。
    /// </summary>
    private static PipeSecurity? TryBuildSecurity()
    {
        try
        {
            var security = new PipeSecurity();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier? user = identity.User;
            if (user is not null)
            {
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            }

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return security;
        }
        catch (Exception ex)
        {
            Log.Warn($"构造管道安全描述符失败，改用默认 DACL：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经取消过，忽略。
        }

        // 后台线程是 IsBackground，不 Join 也不会拦住退出；这里给一点时间让它自己收尾。
        _thread?.Join(500);
        _cts.Dispose();
    }
}
