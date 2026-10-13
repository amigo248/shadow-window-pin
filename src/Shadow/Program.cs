using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using Shadow.App;
using Shadow.Core;
using Shadow.Diagnostics;
using Shadow.Ipc;

namespace Shadow;

internal static class Program
{
    /// <summary>Local\ 前缀 = 每用户每会话单实例，不会跨用户会话互相影响。</summary>
    private const string SingleInstanceMutexName = @"Local\Shadow.WindowPin.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        AppMode mode = AppModeParser.Parse(args);

        // 启动期一律写诊断信标：WinExe 没有控制台，早期故障（写 %TEMP% 被拒等）
        // 会让进程在写日志之前静默退出，只有落在 exe 同目录的 boot.log 能留下线索。
        // 正常启动成功后 RunNormal 会删掉它，所以它只在「启动途中死掉」时存在。
        Boot.Enabled = true;

        try
        {
            Boot.Trace($"Main 进入 pid={Environment.ProcessId} exe={Environment.ProcessPath} " +
                       $"args=[{string.Join(" ", args)}] 管理员={ElevationHelper.IsElevated}");
            Boot.Trace($"数据目录={ShadowPaths.DataDirectory} 日志={Log.FilePath}");

            ConfigureWinForms();
            Boot.Trace($"WinForms 配置完成 mode={mode}");

            if (mode == AppMode.SelfTest)
            {
                // 自检必须能与常驻实例共存，所以不参与单实例互斥
                int code = SelfTest.Run();
                Boot.Trace($"SelfTest.Run 返回 {code}");
                return code;
            }

            if (mode == AppMode.TestWindow)
            {
                int code = RunTestWindow();
                Boot.Clear();
                return code;
            }

            return RunNormal(autostart: AppModeParser.IsAutostart(args));
        }
        catch (Exception ex)
        {
            Boot.Trace($"启动失败：{ex}");
            if (mode != AppMode.SelfTest) ReportStartupFailure(ex);
            return 1;
        }
    }

    private static int RunNormal(bool autostart)
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // 用户再点一次图标的本意就是「把 Shadow 打开给我看」，所以先请已经在跑的实例
            // 把主面板摆出来；只有连管道都不通（界面卡死 / CLI 被关掉）才退回弹框。
            if (TryAskRunningInstanceToShowPanel(out string why))
            {
                Log.Info($"检测到已有实例，已请它打开主面板（{why}），本次启动退出");
                Boot.Clear();
                return 0;
            }

            MessageBox.Show(
                "Shadow 已经在运行，请查看系统托盘图标。" + Environment.NewLine + Environment.NewLine +
                $"（想让已运行的实例打开主面板，但没成功：{why}）",
                "Shadow",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Log.Info($"检测到已有实例，本次启动退出（{why}）");
            Boot.Clear();
            return 2;
        }

        InstallExceptionHandlers();

        Log.Info($"Shadow 启动 PID={Environment.ProcessId} 管理员={ElevationHelper.IsElevated} " +
                 $"日志={Log.FilePath} 自启启动={autostart}");

        using var app = new ShadowApp();
        app.Start(showPanelOnStart: !autostart);

        // 走到这里说明启动路径已经全部成功，信标可以撤了。
        Boot.Clear();

        Application.Run(app.MainWindow);

        Log.Info("消息循环已退出");
        return 0;
    }

    /// <summary>
    /// 把「打开主面板」这条请求交给已经在运行的实例。
    ///
    /// 为什么必须这么做：外部进程抬不动面板 —— 实测 SetForegroundWindow 返回 False，
    /// SetWindowPos(HWND_TOP) + SWP_NOACTIVATE 返回 True 却什么也没发生，SwitchToThisWindow 也无效。
    /// 只有 GUI 自己动手（临时置顶）才有效，所以走命名管道请它开。
    /// </summary>
    private static bool TryAskRunningInstanceToShowPanel(out string detail)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut);
            client.Connect(IpcProtocol.ConnectTimeoutMs);

            var utf8 = new UTF8Encoding(false);
            using var writer = new StreamWriter(client, utf8, bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            string payload = JsonSerializer.Serialize(
                new IpcRequest { Command = "panel" },
                IpcJsonContext.Default.IpcRequest);
            writer.WriteLine(payload);

            Task<string?> pending = reader.ReadLineAsync();
            if (!pending.Wait(TimeSpan.FromSeconds(5)))
            {
                detail = "已运行的实例 5 秒内没有回应";
                return false;
            }

            if (pending.Result is not { } line)
            {
                detail = "已运行的实例关闭了连接";
                return false;
            }

            IpcResponse? response = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcResponse);
            if (response is null)
            {
                detail = $"响应无法解析：{line}";
                return false;
            }

            detail = response.Message;
            return response.Ok;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    /// <summary>自检靶窗口模式：只弹一个能被 --selftest 检出的普通窗口。</summary>
    private static int RunTestWindow()
    {
        using var window = new TestWindow();
        Application.Run(window);
        return 0;
    }

    private static void ConfigureWinForms()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // 视觉样式（comctl32 v6）要把一份激活清单写到临时目录：
        // 单文件发布时 Assembly.Location 为空，清单会落到 %TEMP%。
        // 若这一步被拒绝（受限令牌 / 只读 TEMP），异常会发生在能写日志之前，
        // 表现就是「双击没反应」——所以这里降级成经典主题继续跑，并把原因记进信标。
        try
        {
            Application.EnableVisualStyles();
        }
        catch (Exception ex)
        {
            Boot.Trace($"EnableVisualStyles 失败（降级为经典主题继续运行）：{ex.GetType().Name} {ex.Message}");
        }

        Application.SetCompatibleTextRenderingDefault(false);
    }

    /// <summary>
    /// 早期故障的兜底播报：日志 + 弹框 + 信标三管齐下，绝不再「静默退出」。
    /// </summary>
    private static void ReportStartupFailure(Exception ex)
    {
        try
        {
            Log.Error("启动失败", ex);
        }
        catch
        {
            // 日志目录不可写时继续走弹框
        }

        try
        {
            MessageBox.Show(
                $"Shadow 启动失败：{Environment.NewLine}{Environment.NewLine}{ex.Message}" +
                $"{Environment.NewLine}{Environment.NewLine}诊断信息：{Boot.FilePath}" +
                $"{Environment.NewLine}日志：{Log.FilePath}",
                "Shadow",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 连弹框都不行，就只能靠 boot.log 了
        }
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            Log.Error("UI 线程未处理异常", e.Exception);

            // 有活动窗体就拿它当 owner（并顺手把错误框临时置顶）：
            // 主面板可能正临时置顶，无 owner 的错误框会被它盖住，用户只看到「没反应」。
            Dialogs.Message(
                Form.ActiveForm,
                $"Shadow 遇到一个错误：{Environment.NewLine}{Environment.NewLine}{e.Exception.Message}" +
                $"{Environment.NewLine}{Environment.NewLine}详情见日志：{Log.FilePath}",
                icon: MessageBoxIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error(
                "CLR 未处理异常",
                e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
            PinService.UnpinAll();
        };

        // 兜底：任何正常退出路径都不要留下钉住的窗口
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                PinService.UnpinAll();
            }
            catch
            {
                // 退出阶段不再报错
            }
        };
    }
}
