# 鼠标防锁屏小工具（原地来回抖动版） —— Windows PowerShell，零依赖
# 运行：powershell -ExecutionPolicy Bypass -File mouse_jiggler.ps1
# 停止：按任意键

$step = 5          # 每次抖动的像素数
$intervalMs = 2000 # 抖动间隔（毫秒）

# 加载 user32 / kernel32 函数
$winApi = @'
using System;
using System.Runtime.InteropServices;
public class WinApi {
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint esFlags);
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
}
'@
Add-Type -TypeDefinition $winApi

# 阻止系统休眠 / 锁屏（ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED）
[WinApi]::SetThreadExecutionState(0x80000000 -bor 0x00000001 -bor 0x00000002) | Out-Null

Write-Host "鼠标防锁屏工具已启动（原地来回抖动）。按任意键停止。" -ForegroundColor Cyan
Write-Host "  抖动幅度 = $step 像素，间隔 = $(($intervalMs)/1000) 秒，已阻止系统休眠/锁屏"

$direction = 1
$ticks = 0
try {
    while (-not [Console]::KeyAvailable) {
        $p = New-Object WinApi+POINT
        [WinApi]::GetCursorPos([ref]$p) | Out-Null
        [WinApi]::SetCursorPos($p.X + $direction * $step, $p.Y) | Out-Null
        $direction = -$direction
        $ticks++
        Write-Host "  [$('{0:D4}' -f $ticks)] 鼠标在 ($($p.X), $($p.Y)) 附近抖动"
        Start-Sleep -Milliseconds $intervalMs
    }
} finally {
    # 恢复系统默认电源策略
    [WinApi]::SetThreadExecutionState(0x80000000) | Out-Null
    Write-Host "已停止，系统电源策略已恢复。" -ForegroundColor Yellow
    # 消费掉按键，避免回显
    [Console]::ReadKey($true) | Out-Null
}
