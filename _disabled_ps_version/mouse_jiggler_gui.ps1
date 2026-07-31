# 鼠标防锁屏工具（WinForms 可视化界面，启动/停止按钮）
# 零依赖：仅用 Windows 自带的 PowerShell + .NET，不需要安装 Python
# 运行：直接双击同目录的「启动鼠标防锁屏.bat」即可
# 停止：点击「■ 停止」或关闭窗口

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

try {
    # 加载 user32 / kernel32 函数
    $winApi = @'
using System;
using System.Runtime.InteropServices;
public class WinApi {
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
'@
    Add-Type -TypeDefinition $winApi

    # 状态变量（脚本作用域，供定时器回调使用）
    $script:step = 5
    $script:dir = 1
    $script:count = 0

    # ---------- 界面 ----------
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "鼠标防锁屏工具"
    $form.Size = New-Object System.Drawing.Size(380, 300)
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.StartPosition = "CenterScreen"

    $title = New-Object System.Windows.Forms.Label
    $title.Text = "鼠标防锁屏工具"
    $title.Font = New-Object System.Drawing.Font("Microsoft YaHei", 14, [System.Drawing.FontStyle]::Bold)
    $title.AutoSize = $true
    $title.Location = New-Object System.Drawing.Point(90, 14)
    $form.Controls.Add($title)

    $sub = New-Object System.Windows.Forms.Label
    $sub.Text = "启动后鼠标在原地左右轻微抖动，阻止锁屏/休眠"
    $sub.ForeColor = [System.Drawing.Color]::FromArgb(102, 102, 102)
    $sub.AutoSize = $true
    $sub.Location = New-Object System.Drawing.Point(40, 44)
    $form.Controls.Add($sub)

    $lblStep = New-Object System.Windows.Forms.Label
    $lblStep.Text = "抖动幅度(像素):"; $lblStep.AutoSize = $true
    $lblStep.Location = New-Object System.Drawing.Point(30, 80)
    $form.Controls.Add($lblStep)

    $txtStep = New-Object System.Windows.Forms.TextBox
    $txtStep.Text = "5"; $txtStep.Width = 70
    $txtStep.Location = New-Object System.Drawing.Point(140, 77)
    $form.Controls.Add($txtStep)

    $lblInt = New-Object System.Windows.Forms.Label
    $lblInt.Text = "间隔(秒):"; $lblInt.AutoSize = $true
    $lblInt.Location = New-Object System.Drawing.Point(30, 110)
    $form.Controls.Add($lblInt)

    $txtInt = New-Object System.Windows.Forms.TextBox
    $txtInt.Text = "2.0"; $txtInt.Width = 70
    $txtInt.Location = New-Object System.Drawing.Point(140, 107)
    $form.Controls.Add($txtInt)

    $chkAwake = New-Object System.Windows.Forms.CheckBox
    $chkAwake.Text = "同时阻止系统休眠/锁屏"
    $chkAwake.Checked = $true
    $chkAwake.AutoSize = $true
    $chkAwake.Location = New-Object System.Drawing.Point(30, 138)
    $form.Controls.Add($chkAwake)

    $lblStatus = New-Object System.Windows.Forms.Label
    $lblStatus.Text = "○ 已停止"
    $lblStatus.Font = New-Object System.Drawing.Font("Microsoft YaHei", 11, [System.Drawing.FontStyle]::Bold)
    $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(136, 136, 136)
    $lblStatus.AutoSize = $true
    $lblStatus.Location = New-Object System.Drawing.Point(120, 170)
    $form.Controls.Add($lblStatus)

    $btnStart = New-Object System.Windows.Forms.Button
    $btnStart.Text = "▶ 启动"; $btnStart.Width = 110
    $btnStart.Location = New-Object System.Drawing.Point(60, 210)
    $form.Controls.Add($btnStart)

    $btnStop = New-Object System.Windows.Forms.Button
    $btnStop.Text = "■ 停止"; $btnStop.Width = 110
    $btnStop.Location = New-Object System.Drawing.Point(200, 210)
    $btnStop.Enabled = $false
    $form.Controls.Add($btnStop)

    # ---------- 定时器（后台节拍，不卡界面）----------
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 2000
    $timer.Add_Tick({
        $p = New-Object WinApi+POINT
        [WinApi]::GetCursorPos([ref]$p) | Out-Null
        [WinApi]::SetCursorPos($p.X + $script:dir * $script:step, $p.Y) | Out-Null
        $script:dir = -$script:dir
        $script:count++
        $p2 = New-Object WinApi+POINT
        [WinApi]::GetCursorPos([ref]$p2) | Out-Null
        $lblStatus.Text = "● 运行中   已抖动 $($script:count) 次   位置 ($($p2.X), $($p2.Y))"
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(46, 139, 87)
    })

    # ---------- 按钮事件 ----------
    $btnStart.Add_Click({
        try { $script:step = [Math]::Max(1, [int]$txtStep.Text) } catch { $script:step = 5 }
        try { $iv = [Math]::Max(0.1, [double]$txtInt.Text) } catch { $iv = 2.0 }
        $timer.Interval = [int]($iv * 1000)
        if ($chkAwake.Checked) {
            [WinApi]::SetThreadExecutionState(0x80000000 -bor 0x00000001 -bor 0x00000002) | Out-Null
        }
        $timer.Start()
        $btnStart.Enabled = $false
        $btnStop.Enabled = $true
    })

    $btnStop.Add_Click({
        $timer.Stop()
        if ($chkAwake.Checked) { [WinApi]::SetThreadExecutionState(0x80000000) | Out-Null }
        $btnStart.Enabled = $true
        $btnStop.Enabled = $false
        $lblStatus.Text = "○ 已停止"
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(136, 136, 136)
    })

    $form.Add_FormClosing({
        $timer.Stop()
        [WinApi]::SetThreadExecutionState(0x80000000) | Out-Null
    })

    [void]$form.ShowDialog()
}
catch {
    $err = $_.ToString()
    try { [System.Windows.Forms.MessageBox]::Show($err, "鼠标防锁屏 - 错误", "OK", "Error") } catch {}
    try {
        $log = Join-Path $PSScriptRoot "mouse_jiggler_error.log"
        $err | Out-File -FilePath $log -Encoding utf8
    } catch {}
}
