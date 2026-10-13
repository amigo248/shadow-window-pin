namespace Shadow.App;

/// <summary>进程启动模式。</summary>
internal enum AppMode
{
    /// <summary>正常托盘常驻运行。</summary>
    Normal,

    /// <summary>核心功能自检（无人值守，退出码 0 = 全部通过）。</summary>
    SelfTest,

    /// <summary>自检用的靶窗口，被 --selftest 启动的子进程使用。</summary>
    TestWindow,
}

internal static class AppModeParser
{
    internal const string SelfTestSwitch = "--selftest";
    internal const string TestWindowSwitch = "--testwindow";

    /// <summary>由开机自启任务加上，用来区分「无人值守的登录启动」和「用户自己双击」。</summary>
    internal const string AutostartSwitch = "--autostart";

    /// <summary>
    /// 本次是否由开机自启任务启动。
    /// 自启是无人的：只该安静地待在托盘里，不能把主面板弹到用户脸上；
    /// 用户自己双击（无参数）启动则相反 —— 他就是要看到界面，否则会以为「打不开」。
    /// </summary>
    internal static bool IsAutostart(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, AutostartSwitch, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static AppMode Parse(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, SelfTestSwitch, StringComparison.OrdinalIgnoreCase))
                return AppMode.SelfTest;

            if (string.Equals(arg, TestWindowSwitch, StringComparison.OrdinalIgnoreCase))
                return AppMode.TestWindow;
        }

        return AppMode.Normal;
    }
}
