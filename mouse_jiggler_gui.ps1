# 鼠标防锁屏工具 (PowerShell + WinForms 版)
# 零依赖：不需要 Python，不需要安装任何东西，Windows 自带 PowerShell 即可运行。
# 用法：双击 "启动鼠标防锁屏.bat"

# 加载 WinForms / Drawing 程序集（.NET 自带，安全）
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# ---------- 全局异常捕获：任何错误都弹窗 + 写日志，绝不静默消失 ----------
trap {
    $msg = "发生错误: " + $_.Exception.Message
    try { [System.Windows.Forms.MessageBox]::Show($msg, "鼠标防锁屏 - 错误", "OK", "Error") } catch {}
    try { $msg | Out-File -FilePath "$PSScriptRoot\mouse_jiggler_error.log" -Append -Encoding utf8 } catch {}
    exit 1
}

# ---------- 阻止系统休眠/锁屏 (可选；失败不影响鼠标功能) ----------
$script:haveKA = $false
try {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class KA {
  [DllImport("kernel32.dll")]
  public static extern int SetThreadExecutionState(int es);
}
"@
    $script:haveKA = $true
} catch {
    $script:haveKA = $false
}

function Set-Awake([bool]$on) {
    if (-not $script:haveKA) { return }
    try {
        if ($on) {
            [KA]::SetThreadExecutionState(0x80000000 -bor 0x00000001 -bor 0x00000002) | Out-Null
        } else {
            [KA]::SetThreadExecutionState(0x80000000) | Out-Null
        }
    } catch {
        # 阻止休眠失败不影响鼠标移动功能
    }
}

# ---------- 运行时状态 ----------
$script:running   = $false
$script:tick      = 0
$script:direction = 1
$script:acc       = 0.0
$script:step      = 5
$script:interval  = 2.0
$script:lastPos   = "n/a"

# ---------- 构建窗口 ----------
$form = New-Object System.Windows.Forms.Form
$form.Text = "鼠标防锁屏工具"
$form.Size = New-Object System.Drawing.Size(360, 290)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.TopMost = $true

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "鼠标防锁屏 (原地来回抖动)"
$lblTitle.Font = New-Object System.Drawing.Font("Microsoft YaHei", 11, [System.Drawing.FontStyle]::Bold)
$lblTitle.Location = New-Object System.Drawing.Point(20, 12)
$lblTitle.Size = New-Object System.Drawing.Size(320, 24)
$form.Controls.Add($lblTitle)

$lblStep = New-Object System.Windows.Forms.Label
$lblStep.Text = "抖动幅度(像素):"
$lblStep.Location = New-Object System.Drawing.Point(20, 52)
$lblStep.Size = New-Object System.Drawing.Size(110, 22)
$form.Controls.Add($lblStep)

$txtStep = New-Object System.Windows.Forms.TextBox
$txtStep.Text = "5"
$txtStep.Location = New-Object System.Drawing.Point(140, 50)
$txtStep.Size = New-Object System.Drawing.Size(70, 22)
$form.Controls.Add($txtStep)

$lblInt = New-Object System.Windows.Forms.Label
$lblInt.Text = "间隔(秒):"
$lblInt.Location = New-Object System.Drawing.Point(20, 82)
$lblInt.Size = New-Object System.Drawing.Size(110, 22)
$form.Controls.Add($lblInt)

$txtInt = New-Object System.Windows.Forms.TextBox
$txtInt.Text = "2.0"
$txtInt.Location = New-Object System.Drawing.Point(140, 80)
$txtInt.Size = New-Object System.Drawing.Size(70, 22)
$form.Controls.Add($txtInt)

$chkAwake = New-Object System.Windows.Forms.CheckBox
$chkAwake.Text = "同时阻止系统休眠/锁屏"
$chkAwake.Checked = $true
$chkAwake.Location = New-Object System.Drawing.Point(20, 112)
$chkAwake.Size = New-Object System.Drawing.Size(220, 22)
$form.Controls.Add($chkAwake)

$btnStart = New-Object System.Windows.Forms.Button
$btnStart.Text = "▶ 启动"
$btnStart.Location = New-Object System.Drawing.Point(30, 148)
$btnStart.Size = New-Object System.Drawing.Size(120, 36)
$form.Controls.Add($btnStart)

$btnStop = New-Object System.Windows.Forms.Button
$btnStop.Text = "■ 停止"
$btnStop.Location = New-Object System.Drawing.Point(170, 148)
$btnStop.Size = New-Object System.Drawing.Size(120, 36)
$btnStop.Enabled = $false
$form.Controls.Add($btnStop)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = "○ 已停止"
$lblStatus.ForeColor = [System.Drawing.Color]::Gray
$lblStatus.Location = New-Object System.Drawing.Point(20, 198)
$lblStatus.Size = New-Object System.Drawing.Size(200, 22)
$form.Controls.Add($lblStatus)

$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Text = "次数: 0   位置: n/a"
$lblInfo.Location = New-Object System.Drawing.Point(20, 224)
$lblInfo.Size = New-Object System.Drawing.Size(300, 22)
$form.Controls.Add($lblInfo)

# ---------- 定时器（100ms 轮询，按设定的间隔抖动） ----------
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 100
$timer.Add_Tick({
    if (-not $script:running) { return }
    $script:acc += $timer.Interval / 1000.0
    if ($script:acc -ge $script:interval) {
        $script:acc = 0
        $p = [System.Windows.Forms.Cursor]::Position
        $nx = $p.X + $script:direction * $script:step
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($nx, $p.Y)
        $script:direction *= -1
        $script:tick++
        $script:lastPos = "$nx,$($p.Y)"
        $lblInfo.Text = "次数: $($script:tick)   位置: $($script:lastPos)"
    }
})

$btnStart.Add_Click({
    try {
        $script:step = [int]$txtStep.Text
        if ($script:step -lt 1) { $script:step = 1 }
        $script:interval = [double]$txtInt.Text
        if ($script:interval -lt 0.1) { $script:interval = 0.1 }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("幅度/间隔请输入数字", "输入错误")
        return
    }
    $script:running = $true
    $script:tick = 0
    $script:acc = 0.0
    $script:direction = 1
    $btnStart.Enabled = $false
    $btnStop.Enabled = $true
    $lblStatus.Text = "● 运行中"
    $lblStatus.ForeColor = [System.Drawing.Color]::Green
    if ($chkAwake.Checked) { Set-Awake $true }
    $timer.Start()
})

$btnStop.Add_Click({
    $script:running = $false
    $timer.Stop()
    $btnStart.Enabled = $true
    $btnStop.Enabled = $false
    $lblStatus.Text = "○ 已停止"
    $lblStatus.ForeColor = [System.Drawing.Color]::Gray
    Set-Awake $false
})

$form.Add_FormClosing({
    $script:running = $false
    $timer.Stop()
    Set-Awake $false
})

# 启动消息循环（定时器先 Start 但 running=false，不移动）
$timer.Start()
[System.Windows.Forms.Application]::Run($form)
