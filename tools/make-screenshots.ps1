# 抓取 README 用的界面截图。
#
# 为什么要写脚本而不是手动截图：
#   1) 截图要能复现 —— 布局、层级、徽标位置都是确定值，改配色/字号后重跑一次就能更新文档；
#   2) 手动截的图会把桌面壁纸、别的窗口一起拍进去，不适合放进仓库。
#
# 用法（本机执行策略禁止直接运行 .ps1，必须用 Invoke-Expression 包一层；脚本会自己定位仓库根）：
#   Set-Location <仓库根>
#   $p = '.\tools\make-screenshots.ps1'
#   Invoke-Expression ([System.IO.File]::ReadAllText($p,[Text.Encoding]::UTF8))
#   跨机器时可以先把 $repo 设成仓库根，脚本就不再靠当前目录猜：
#   $repo = 'D:\code\shadow-window-pin'; Invoke-Expression (...)
#
# 产物：<仓库根>\docs\img\{overview,panel,picker}.png
#
# 血泪教训（选示例窗口时踩过的坑）：
#   * Win11 的记事本是「多标签单窗口」，连开两个文件只会得到**一个**窗口（标题显示最后打开的标签）；
#   * 更要命的是 **Win11 新版记事本（MSIX）会静默忽略置顶请求**：SetWindowPos 返回成功，
#     但 WS_EX_TOPMOST 位永远不生效（直接绕开 Shadow 用 PowerShell 调 SetWindowPos 也一样），
#     所以拿它当示例窗口会得到「已置顶却显示未生效」的截图；
#   * mspaint 在这台机器上起不来。
#   现在改用**三个资源管理器窗口**：CabinetWClass 实测能稳定置顶，标题就是文件夹名，
#   而且同一进程连开三个会得到三个独立窗口。
#   * 置顶别的进程的窗口需要「前台权限」：Windows 只允许当前拥有前台权限的进程去重排别人的窗口
#     —— 官方文档《SetWindowPos》的 Remarks 明写这条前提（实测：不在前台时 SetWindowPos 返回 True
#     但 topmost 位根本不置位）。程序自己会在失败后尝试把目标切到前台，但那是破坏性的（会抢走你的
#     焦点），所以脚本改用 AttachThreadInput + SetForegroundWindow 把**主面板**变成前台，
#     全程不碰任何示例窗口、也不用合成点击（合成点击是真按在屏幕坐标上，会点进你正在看的视频）。
#
# 抓图方式：**不截屏**，用 PrintWindow 把每个窗口单独渲染后按真实位置拼一张 ——
# 这样屏幕上压着别的窗口（你正在看的视频、开发者的对话窗口、桌面壁纸）都不会进画面，
# 也不需要把窗口抬到最前。只有拾取器那张仍用屏幕截图（拾取框是独立 topmost 分层窗，PrintWindow
# 渲不出边框），因此那张默认跳过，需要时先设 $allowInputInjection = $true。
#
# 置顶一律用 --handle（先按类名 + 标题找窗口），避免标题正则被别人同名的窗口命中。

$ErrorActionPreference = 'Stop'

# 仓库根：用 Invoke-Expression 包一层运行时拿不到 $PSScriptRoot，所以支持调用方先设好 $repo；
# 没设就按当前目录猜（从仓库根跑、或从仓库的上级目录跑都能用）。
if (-not $repo) {
    if (Test-Path (Join-Path $PWD 'tools\make-screenshots.ps1')) { $repo = $PWD.Path }
    elseif (Test-Path (Join-Path $PWD 'Shadow\tools\make-screenshots.ps1')) { $repo = Join-Path $PWD 'Shadow' }
    else { throw '定位不到仓库根：请在仓库根目录运行，或先设置 $repo 指向仓库根' }
}
$repo = (Resolve-Path $repo).Path

# 示例程序放在中性目录：面板状态栏会显示实际数据目录，公开截图里不该出现开发机自己的路径。
$appDir = 'C:\ShadowDemo'
$exe    = Join-Path $appDir 'Shadow.exe'
$cli    = Join-Path $appDir 'ShadowCli.exe'
$data   = Join-Path $appDir 'Shadow'
$outDir = Join-Path $repo 'docs\img'
$temp   = Join-Path $repo '_diag\temp'
$bundle = Join-Path $repo '_diag\bundle'
$demoRoot = Join-Path $repo '_diag\demo'

# 拾取器那张图必须真实点击面板按钮 + 移动鼠标（会打断你正在做的事），所以默认跳过。
# 想重拍时：先 $allowInputInjection = $true，再运行本脚本。
if (-not $allowInputInjection) { $allowInputInjection = $false }

New-Item -ItemType Directory -Force $outDir, $temp, $bundle | Out-Null

# 把发布产物复制到中性目录（只复制程序本体，几百 KB）
$srcDist = Join-Path $repo 'dist\Shadow'
if (-not (Test-Path (Join-Path $srcDist 'Shadow.exe'))) {
    throw "找不到发布产物 $srcDist\Shadow.exe：先按 README《构建与打包》跑一次 dotnet publish"
}
New-Item -ItemType Directory -Force $appDir | Out-Null
Get-ChildItem $srcDist -File | Copy-Item -Destination $appDir -Force
# 一定要把数据目录清掉：里面存着上次的置顶记录，留着会让程序认为「不是第一次运行」，
# 于是只留托盘、不弹主面板 —— 截出来的图就没有主面板（这张踩过一次）。
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.Drawing | Out-Null
Add-Type -AssemblyName System.Windows.Forms | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Native {
    // SetWindowPos 的常用标志。
    // 注意 SWP_NOSIZE/SWP_NOMOVE 一定要定义：PowerShell 里漏定义的常量不会报错，
    // `$null -bor 0x10` 会静默变成 0x10，于是「只抬层级」的那次调用会顺手把窗口挪到 0,0 并缩到最小
    // —— 实测就是这样把摆好的主面板挪到左上角，把示例窗口整个盖住，截图全废。
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint WM_CLOSE = 0x0010;
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr param);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    public delegate bool EnumProc(IntPtr h, IntPtr param);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        return sb.ToString();
    }

    public static string ClassOf(IntPtr h) {
        var sb = new StringBuilder(512);
        GetClassNameW(h, sb, 512);
        return sb.ToString();
    }

    // 按标题片段找顶层窗口：程序标题常带后缀（「xxx - 资源管理器」之类），精确匹配不可靠
    public static IntPtr FindByTitlePart(string part, bool requireVisible) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            if (requireVisible && !IsWindowVisible(h)) return true;
            if (TitleOf(h).Contains(part)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // 类名 + 标题片段一起匹配：资源管理器的 CabinetWClass 不会和别的窗口撞车
    public static IntPtr FindByClassAndTitle(string cls, string titlePart, bool requireVisible) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            if (requireVisible && !IsWindowVisible(h)) return true;
            if (ClassOf(h) != cls) return true;
            if (TitleOf(h).Contains(titlePart)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // 关掉上一次抓图留下的示例窗口（只关这些窗口，绝不杀 explorer.exe 本身）
    public static int CloseByClassAndTitle(string cls, string titlePart) {
        int n = 0;
        EnumWindows((h, p) => {
            if (ClassOf(h) == cls && TitleOf(h).Contains(titlePart)) { PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); n++; }
            return true;
        }, IntPtr.Zero);
        return n;
    }

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // 把 h 变成前台窗口。
    // 为什么必须抢前台：置顶别人的窗口要求调用方（Shadow）拥有前台权限，否则置顶会被系统拒绝
    // （实测报「目标窗口不在前台」）。而且这台机器的「前台锁超时」是无穷大
    // （SPI_GETFOREGROUNDLOCKTIMEOUT = 2147483647），单靠 AttachThreadInput + SetForegroundWindow
    // 必定失败 —— 实测 7 种组合里只有带「轻敲 ALT」或 SwitchToThisWindow 的才成功。
    // 这里刻意**不用合成点击**：合成点击是真按在屏幕坐标上，你正在看视频时那一击就点进播放器了。
    public static bool ForceForeground(IntPtr h) {
        if (h == IntPtr.Zero) return false;
        for (int i = 0; i < 4; i++) {
            if (GetForegroundWindow() == h) return true;
            SwitchToThisWindow(h, true);
            System.Threading.Thread.Sleep(250);
            if (GetForegroundWindow() == h) return true;

            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint myThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(fgThread, myThread, true);
            keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            SetForegroundWindow(h);
            BringWindowToTop(h);
            SetActiveWindow(h);
            if (attached) AttachThreadInput(fgThread, myThread, false);
            System.Threading.Thread.Sleep(300);
            if (GetForegroundWindow() == h) return true;
        }
        return false;
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // 左键按下
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // 左键抬起
    }
}
'@

# ---------------- 抓图用的辅助函数 ----------------
# 抓图**不截屏**：改为用 PrintWindow 把每个窗口单独渲染到位图，再拼成一张。
# 好处是屏幕上压着别的窗口（你正在看的视频、开发者的对话窗口、桌面壁纸）都不会被拍进去，
# 也不需要把窗口抬到最前面，更不需要合成点击 —— 合成点击是真的点在屏幕坐标上，会打扰你正在做的事。
function Pump([int]$ms) {
    $deadline = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $deadline) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 40
    }
}

function Start-Hidden([string]$file, [string]$arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $file; $psi.Arguments = $arguments; $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['TEMP'] = $temp
    $psi.EnvironmentVariables['TMP'] = $temp
    $psi.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $bundle
    [System.Diagnostics.Process]::Start($psi)
}

function Invoke-Cli([string[]]$a) {
    $q = $a | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $cli; $psi.Arguments = ($q -join ' '); $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['TEMP'] = $temp
    $psi.EnvironmentVariables['TMP'] = $temp
    $psi.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $bundle
    $p = [System.Diagnostics.Process]::Start($psi)
    # 先读干净输出再等退出：报告比管道缓冲区大时，顺序反了会两边一起卡住
    $text = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
    $p.WaitForExit(20000) | Out-Null
    $text.Trim()
}

function Wait-Explorer([string]$title, [int]$timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $h = [Native]::FindByClassAndTitle('CabinetWClass', $title, $true)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Wait-Panel([int]$timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $h = [Native]::FindByTitlePart('Shadow · 窗口置顶', $false)
        # 必须等到它真的显示出来再摆位：面板句柄在启动早期就存在，那时 Windows 还没按 StartPosition
        # 摆好位置，摆早了会被随后的显示动作覆盖（实测：移完又回到 30,30，和左上角示例窗口重叠拍花）
        if ($h -ne [IntPtr]::Zero -and [Native]::IsWindowVisible($h)) { return $h }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Move-Window([IntPtr]$h, [int]$x, [int]$y, [int]$w, [int]$hh) {
    # 既要摆位置，又绝不抢焦点（抢焦点会触发严格的 Z 序维护，把窗口层级挪走）。
    # 摆完一定回读校验：面板/资源管理器都可能按自己的记忆重新摆一次，没对上就再摆（最多 8 次）。
    for ($i = 1; $i -le 8; $i++) {
        [Native]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, $w, $hh,
            ([Native]::SWP_NOZORDER -bor [Native]::SWP_NOACTIVATE)) | Out-Null
        Pump 250
        $r = New-Object Native+RECT
        [Native]::GetWindowRect($h, [ref]$r) | Out-Null
        if ([math]::Abs($r.Left - $x) -le 2 -and [math]::Abs($r.Top - $y) -le 2) { return $true }
    }
    Write-Output ("警告：0x{0:X} 摆位没生效（想要 {1},{2}，实际 {3},{4}）—— 图里可能重叠" -f $h.ToInt64(), $x, $y, $r.Left, $r.Top)
    return $false
}

function Save-Composite([string]$name, [IntPtr[]]$handles, [int]$scaleTo, [int]$pad) {
    # 逐个窗口 PrintWindow 到位图，再按它们的真实屏幕位置拼到一张浅色画布上
    $shots = @()
    $minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = [int]::MinValue; $maxY = [int]::MinValue
    foreach ($h in $handles) {
        $r = New-Object Native+RECT
        [Native]::GetWindowRect($h, [ref]$r) | Out-Null
        $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
        if ($w -le 0 -or $hh -le 0) { Write-Output ("警告：0x{0:X} 的矩形是空的，跳过" -f $h.ToInt64()); continue }
        $bmp = New-Object System.Drawing.Bitmap($w, $hh)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        # PW_RENDERFULLCONTENT(2)：连 DWM 合成的窗口也能渲染（0 只对老式窗口有效）
        $ok = [Native]::PrintWindow($h, $hdc, 2)
        $g.ReleaseHdc($hdc); $g.Dispose()
        if (-not $ok) { Write-Output ("警告：PrintWindow 渲染 0x{0:X} 返回失败，图可能是黑的" -f $h.ToInt64()) }
        $shots += ,@{ Bmp = $bmp; X = $r.Left; Y = $r.Top; W = $w; H = $hh }
        if ($r.Left -lt $minX) { $minX = $r.Left }
        if ($r.Top -lt $minY) { $minY = $r.Top }
        if ($r.Right -gt $maxX) { $maxX = $r.Right }
        if ($r.Bottom -gt $maxY) { $maxY = $r.Bottom }
    }
    if ($shots.Count -eq 0) { Write-Output "警告：没有可拼的窗口，$name 未生成"; return }

    $cw = ($maxX - $minX) + 2 * $pad; $ch = ($maxY - $minY) + 2 * $pad
    $canvas = New-Object System.Drawing.Bitmap($cw, $ch)
    $gc = [System.Drawing.Graphics]::FromImage($canvas)
    $gc.Clear([System.Drawing.Color]::FromArgb(243, 244, 246))
    foreach ($s in $shots) {
        $gc.DrawImage($s.Bmp, ($s.X - $minX + $pad), ($s.Y - $minY + $pad), $s.W, $s.H)
        $s.Bmp.Dispose()
    }
    $gc.Dispose()

    if ($scaleTo -gt 0 -and $cw -gt $scaleTo) {
        $nh = [int]($ch * $scaleTo / $cw)
        $small = New-Object System.Drawing.Bitmap($scaleTo, $nh)
        $g2 = [System.Drawing.Graphics]::FromImage($small)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.DrawImage($canvas, 0, 0, $scaleTo, $nh)
        $g2.Dispose()
        $canvas.Dispose()
        $canvas = $small
    }
    $path = Join-Path $outDir $name
    $canvas.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
    Write-Output ("已保存 {0}（{1} 字节，{2} 个窗口拼成）" -f $name, (Get-Item $path).Length, $shots.Count)
}

function Save-Crop([string]$name, [int]$x, [int]$y, [int]$w, [int]$h, [int]$scaleTo) {
    # 只有拾取器那张图需要屏幕截图（拾取框是独立的 topmost 分层窗口，PrintWindow 渲不出它的边框），
    # 所以这条路径要求屏幕上没有别的窗口压着示例窗口。
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    if ($scaleTo -gt 0 -and $w -gt $scaleTo) {
        $nh = [int]($h * $scaleTo / $w)
        $small = New-Object System.Drawing.Bitmap($scaleTo, $nh)
        $g2 = [System.Drawing.Graphics]::FromImage($small)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.DrawImage($bmp, 0, 0, $scaleTo, $nh)
        $g2.Dispose()
        $bmp.Dispose()
        $bmp = $small
    }
    $path = Join-Path $outDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("已保存 {0}（{1} 字节）" -f $name, (Get-Item $path).Length)
}

# ---------------- 清场 ----------------
# 只结束 Shadow 自己和上一次抓图留下的示例资源管理器窗口（按类名 + 标题关窗，不杀 explorer.exe）
Get-Process Shadow, ShadowCli -ErrorAction SilentlyContinue | Stop-Process -Force
$demoNames = @('窗口置顶需求草稿', '窗口置顶窗口清单', '窗口置顶层级效果')
foreach ($n in $demoNames) {
    $closed = [Native]::CloseByClassAndTitle('CabinetWClass', $n)
    Write-Output ("关闭上次残留示例窗口「{0}」：{1} 个" -f $n, $closed)
}
Start-Sleep -Milliseconds 800
Remove-Item (Join-Path $data 'config.json'), (Join-Path $data 'running.marker'), (Join-Path $data 'shadow.log') -ErrorAction SilentlyContinue

# ---------------- 让示例程序改用「便携」数据目录，截图里才不会出现 Windows 用户名 ----------------
# 面板状态栏会显示真实数据目录，默认是 C:\Users\<你的用户名>\AppData\Roaming\Shadow —— 公开的
# README 截图不该带用户名。程序选目录的规则是「%APPDATA%\Shadow 可写就用它，否则回退 exe 同级」，
# 所以这里把真实目录临时改名，并在原位置放一个同名**文件**（创建目录会失败）→ 程序回退到
# C:\ShadowDemo\Shadow，状态栏就只显示这条中性路径。收工时会原样改回来。
$realData = Join-Path $env:APPDATA 'Shadow'
$realBackup = Join-Path $env:APPDATA 'Shadow.截图临时备份'
$dataHidden = $false
if (Test-Path $realData -PathType Container) {
    Remove-Item $realBackup -Recurse -Force -ErrorAction SilentlyContinue
    Rename-Item $realData $realBackup
    Set-Content -Path $realData -Value 'make-screenshots.ps1 临时占位：抓图结束后会删除本文件并恢复同名目录' -Encoding ASCII
    $dataHidden = $true
    Write-Output ("已把真实数据目录临时改名：{0} → {1}" -f $realData, $realBackup)
} elseif (Test-Path $realData) {
    Write-Output ("注意：{0} 已经是一个文件（多半是上次抓图没跑完），本次直接沿用" -f $realData)
    $dataHidden = $false
}

# ---------------- 三个示例目录 + 每个目录里放几个小文件，窗口里才不是空的 ----------------
$utf8 = New-Object System.Text.UTF8Encoding($false)
$demos = @(
    @{ Name = '窗口置顶需求草稿'; Files = @{
        '置顶需求.txt' = "窗口置顶需求`r`n1. 单个窗口置顶`r`n2. 多个窗口同时置顶`r`n3. 自定义先后层级`r`n"
        '验收清单.txt' = "1. 层级徽标可见`r`n2. 快捷键置顶`r`n3. 退出后全部还原`r`n" } },
    @{ Name = '窗口置顶窗口清单'; Files = @{
        '待办窗口.txt' = "编辑器 - 主窗口`r`n资源管理器 - 项目目录`r`n终端 - 构建输出`r`n"
        '分组方案.txt' = "第 1 层：文档`r`n第 2 层：参考`r`n第 3 层：日志`r`n" } },
    @{ Name = '窗口置顶层级效果'; Files = @{
        '层级说明.txt' = "1 = 最高层（最先看到）`r`n9 = 最低层`r`n徽标颜色越亮层级越高`r`n" } }
)
foreach ($d in $demos) {
    $dir = Join-Path $demoRoot $d.Name
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($k in $d.Files.Keys) {
        [System.IO.File]::WriteAllText((Join-Path $dir $k), $d.Files[$k], $utf8)
    }
}

# ---------------- 起示例窗口与 Shadow ----------------
foreach ($d in $demos) {
    $dir = Join-Path $demoRoot $d.Name
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:SystemRoot 'explorer.exe'
    $psi.Arguments = '"' + $dir + '"'
    $psi.UseShellExecute = $false
    [System.Diagnostics.Process]::Start($psi) | Out-Null
}
Start-Hidden $exe '' | Out-Null
Pump 5000

$hA = Wait-Explorer '窗口置顶需求草稿' 10000
$hB = Wait-Explorer '窗口置顶窗口清单' 10000
$hC = Wait-Explorer '窗口置顶层级效果' 10000
$hP = Wait-Panel 10000
Write-Output ("窗口句柄：需求草稿=0x{0:X} 窗口清单=0x{1:X} 层级效果=0x{2:X} 主面板=0x{3:X}" -f $hA.ToInt64(), $hB.ToInt64(), $hC.ToInt64(), $hP.ToInt64())
if ($hA -eq [IntPtr]::Zero -or $hB -eq [IntPtr]::Zero -or $hC -eq [IntPtr]::Zero -or $hP -eq [IntPtr]::Zero) { throw '有窗口没找到，先看上面的句柄再决定' }

# ---------------- 先摆位置，再逐个置顶 ----------------
# 顺序：先把四个窗口摆成互不重叠的九宫格（摆位用 SWP_NOZORDER|SWP_NOACTIVATE，本身不改焦点与层级），
# 再用 --handle 逐个置顶。
Move-Window $hA 30 30 700 400
Move-Window $hB 30 470 700 400
Move-Window $hC 770 30 700 400
# 主面板的最小尺寸是脚本算出来的（721x440），所以这里按 721 摆 —— 请求更小的尺寸只会被它夹回去，
# 而截图区域是按摆位算的，摆位和实际尺寸不一致就会把面板右边/下边裁掉。
Move-Window $hP 770 470 721 440
Pump 500

# 置顶要求调用方（Shadow）拥有前台权限，所以这里先把主面板抬到普通层带最前。
# 这只是为了拿前台，和抓图无关 —— 抓图走 PrintWindow，窗口被别的窗口压着也照样能截。
[Native]::SetWindowPos($hP, [IntPtr]::Zero, 0, 0, 0, 0,
    ([Native]::SWP_NOMOVE -bor [Native]::SWP_NOSIZE -bor [Native]::SWP_NOACTIVATE)) | Out-Null
Pump 300

function Focus-Panel([IntPtr]$h) {
    # Windows 只允许「当前拥有前台权限的进程」重排别人的窗口（官方文档 SetWindowPos 的
    # 「the process that owns the window must have SetForegroundWindow permission」）：
    # Shadow 是调用方，它得先成为前台进程，否则置顶会被系统全部拒绝（实测报「目标窗口不在前台」）。
    # ForceForeground 内部会先 SwitchToThisWindow，失败再轻敲 ALT + AttachThreadInput 抢前台。
    return [Native]::ForceForeground($h)
}

# 目标层级：层级效果第 1 层，需求草稿第 2 层，窗口清单第 3 层。
# 新置顶的窗口排在最前，所以按 3→2→1 的顺序钉，最后得到的层级正好是 1/2/3。
# 每一次置顶前都重新确认主面板是前台：中途要是被别的窗口抢走焦点，这一钉就会被系统拒绝（重试一次）。
function Pin-Window([IntPtr]$h, [int]$rank, [string]$label) {
    for ($i = 1; $i -le 2; $i++) {
        $fg = [Native]::GetForegroundWindow()
        if ($fg -ne $hP) {
            $ok = [Native]::ForceForeground($hP)
            Write-Output ("{0}：抢前台 {1}（前台=0x{2:X}，主面板=0x{3:X}）" -f $label, $(if ($ok) { '成功' } else { '失败' }), [Native]::GetForegroundWindow().ToInt64(), $hP.ToInt64())
        }
        $text = Invoke-Cli @('pin', '--handle', ('0x{0:X}' -f $h.ToInt64()), '--rank', $rank)
        Write-Output $text
        if ($text -notmatch '无法置顶|不在前台') { return $true }
        Pump 600
    }
    return $false
}

$pinned = @()
$pinned += (Pin-Window $hB 3 '窗口清单')
$pinned += (Pin-Window $hA 2 '需求草稿')
$pinned += (Pin-Window $hC 1 '层级效果')
if ($pinned -contains $false) { Write-Output '警告：有窗口没能置顶（这张图会缺层级，不能用）' }
Invoke-Cli @('list') | Write-Output

Start-Sleep -Milliseconds 1400   # 等徽标兜底定时器把徽标摆回各自窗口的左上角

Save-Composite 'overview.png' @($hA, $hB, $hC, $hP) 1180 20
Save-Composite 'panel.png' @($hP) 880 20

# ---------------- 拾取器：点主面板「拾取窗口」按钮，再把鼠标移到一个示例窗口上 ----------------
# 这一段必须真实点击 + 移动鼠标，也就是会打断你正在做的事（比如全屏看视频时鼠标会被抢走）。
# 所以默认**跳过**；需要重拍这张图时，在调用前设 $allowInputInjection = $true。
if (-not $allowInputInjection) {
    Write-Output '跳过拾取器截图（需要真实点击与移动鼠标，避免打扰你正在用的程序）；需要时用 $allowInputInjection = $true 重跑'
} else {
    $rect = New-Object Native+RECT
    [Native]::GetWindowRect($hP, [ref]$rect) | Out-Null
    Write-Output ("主面板矩形：{0},{1} 尺寸 {2}x{3}" -f $rect.Left, $rect.Top, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))

    # 按钮位置按布局估算，点不中就试下一个偏移；判据是「拾取器启动后主面板会自己隐藏」
    $offsets = @(@(46, 48), @(46, 62), @(46, 36), @(80, 48))
    $started = $false
    foreach ($off in $offsets) {
        [Native]::Click(($rect.Left + $off[0]), ($rect.Top + $off[1]))
        Start-Sleep -Milliseconds 700
        if (-not [Native]::IsWindowVisible($hP)) {
            $started = $true
            Write-Output ("拾取器已启动（按钮偏移 {0},{1}）" -f $off[0], $off[1])
            break
        }
    }
    if (-not $started) { Write-Output '警告：没能点到「拾取窗口」按钮，picker.png 会缺失' }
    else {
        [Native]::SetCursorPos(380, 250) | Out-Null   # 鼠标停在上方示例窗口上，拾取框跟随光标
        Start-Sleep -Milliseconds 900
        # 拾取框是独立的 topmost 分层窗口，PrintWindow 渲不出它的边框，所以这一张只能屏幕截图；
        # 裁剪范围正好框住示例窗口，屏幕上有别的窗口压着时才需要重拍。
        Save-Crop 'picker.png' 20 20 720 450 720
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 700
    }
}

# ---------------- 收尾：让 Shadow 自己正常退出（会把置顶全部还原），再关掉示例窗口 ----------------
Invoke-Cli @('exit') | Write-Output
Start-Sleep -Milliseconds 2000
Get-Process Shadow, ShadowCli -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($n in $demoNames) { [Native]::CloseByClassAndTitle('CabinetWClass', $n) | Out-Null }
# 把数据目录改回原样（这一步绝不能漏：留着同名文件的话，程序会一直以「便携模式」运行）
if ($dataHidden) {
    Remove-Item $realData -Force -ErrorAction SilentlyContinue
    Rename-Item $realBackup $realData
    Write-Output ("已恢复真实数据目录：{0} → {1}" -f $realBackup, $realData)
}
Write-Output '--- 抓图结束，示例资源管理器窗口已关闭 ---'
