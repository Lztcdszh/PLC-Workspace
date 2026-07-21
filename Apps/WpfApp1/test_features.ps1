$log = "C:\Users\Administrator\source\repos\WpfApp1\test_log.txt"
try { [System.IO.File]::WriteAllText($log, "") } catch {}
function Log($m) { try { [System.IO.File]::AppendAllText($log, $m + [Environment]::NewLine) } catch {} }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [Windows.Automation.AutomationElement]
$root = $AE::RootElement

function Find-ByPid($p) { $c = New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p); return $root.FindAll([Windows.Automation.TreeScope]::Children, $c) }
function Find-ById($parent, $id) { $c = New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id); return $parent.FindFirst([Windows.Automation.TreeScope]::Descendants, $c) }
function Find-ByName($parent, $name) { $c = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name); return $parent.FindFirst([Windows.Automation.TreeScope]::Descendants, $c) }
function Invoke-El($el) { if ($el) { $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() } }
function SetVal($el, $v) { if (-not $el) { return $false }; try { $el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($v); return $true } catch { try { $el.GetCurrentPattern([Windows.Automation.LegacyIAccessiblePattern]::Pattern).SetValue($v); return $true } catch { return $false } } }
function Text($el) { if ($el) { return $el.Current.Name } return "<null>" }
function Check($id, $el) { if ($el) { Log ("  found $id OK") } else { Log ("  !! $id NOT FOUND") } }

function Expand-ByName($parent, $name) {
    $el = Find-ByName $parent $name
    if (-not $el) { Log ("  expander [$name] NOT FOUND"); return }
    try { $el.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { Log ("  expand [$name] failed") }
    Start-Sleep -Milliseconds 400
}

function SelectComboIndex($combo, $idx) {
    if (-not $combo) { Log "  SelectComboIndex: combo is NULL"; return $false }
    $p = $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    try { $p.Expand() } catch { Log "  expand failed" }
    Start-Sleep -Milliseconds 400
    $cond = New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $items = $combo.FindAll([Windows.Automation.TreeScope]::Descendants, $cond)
    Log ("  combo items=" + $items.Count + " target=" + $idx)
    if ($idx -lt $items.Count) { $items[$idx].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300 }
    try { $p.Collapse() } catch {}
    return $true
}
function SelectListIndex($list, $idx) {
    if (-not $list) { Log "  SelectListIndex: list is NULL"; return 0 }
    $cond = New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll([Windows.Automation.TreeScope]::Descendants, $cond)
    if ($idx -lt $items.Count) { $items[$idx].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300 }
    return $items.Count
}

$exe = "C:\Users\Administrator\source\repos\WpfApp1\bin\Debug\net8.0-windows\WpfApp1.exe"
$crashErr = "C:\Users\Administrator\source\repos\WpfApp1\bin\Debug\net8.0-windows\crash_err.txt"

try {
    if (Test-Path $crashErr) { Remove-Item $crashErr -Force }

    $proc = Start-Process -FilePath $exe -PassThru
    Log ("launched pid=" + $proc.Id)
    Start-Sleep -Seconds 7

    $win = (Find-ByPid $proc.Id)[0]
    if (-not $win) { Log "NO MAIN WINDOW"; throw "no window" }
    Log "main window found"

    Expand-ByName $win "点云裁剪"
    Expand-ByName $win "数据库（SQLite）"
    Expand-ByName $win "MQTT（发布状态）"
    Start-Sleep -Milliseconds 500

    Invoke-El (Find-ById $win "BtnGenerate"); Start-Sleep -Seconds 3
    Log ("after sphere: " + (Text (Find-ById $win "CloudCountText")))

    SelectComboIndex (Find-ById $win "SampleShapeCombo") 1
    Invoke-El (Find-ById $win "BtnGenerate"); Start-Sleep -Seconds 3
    Log ("after cube: " + (Text (Find-ById $win "CloudCountText")))

    $nList = SelectListIndex (Find-ById $win "CloudListBox") 0
    Log ("list items=" + $nList + "  active source=" + (Text (Find-ById $win "InfoSource")))

    Check "VoxelSizeBox" (Find-ById $win "VoxelSizeBox")
    SetVal (Find-ById $win "VoxelSizeBox") "0.5"
    Invoke-El (Find-ById $win "BtnComputeVolume"); Start-Sleep -Seconds 4
    Log ("volume result: " + (Text (Find-ById $win "VolumeResult")))

    # Box clip: select box mode FIRST to reveal the box panel, then set bounds
    Check "ClipModeCombo" (Find-ById $win "ClipModeCombo")
    SelectComboIndex (Find-ById $win "ClipModeCombo") 1; Start-Sleep -Milliseconds 600
    Check "ClipXmin" (Find-ById $win "ClipXmin")
    SetVal (Find-ById $win "ClipXmin") "0"; SetVal (Find-ById $win "ClipXmax") "50"
    Invoke-El (Find-ById $win "BtnApplyClip"); Start-Sleep -Seconds 2
    Log ("after clip boundsX: " + (Text (Find-ById $win "InfoBoundsX")))
    Invoke-El (Find-ById $win "BtnClearClip"); Start-Sleep -Seconds 1
    Log ("after clearclip boundsX: " + (Text (Find-ById $win "InfoBoundsX")))

    Invoke-El (Find-ById $win "BtnDbRefresh"); Start-Sleep -Seconds 1
    Log ("DB status: " + (Text (Find-ById $win "DbStatus")))
    Check "DbListBox" (Find-ById $win "DbListBox")
    $dbEl = (Find-ById $win "DbListBox")
    if ($dbEl) { $dbItems = $dbEl.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))); Log ("DB list items=" + $dbItems.Count) }

    Check "MqttConnect" (Find-ById $win "BtnMqttConnect")
    Invoke-El (Find-ById $win "BtnMqttConnect"); Start-Sleep -Seconds 10
    $ms = (Text (Find-ById $win "MqttStatus"))
    Log ("MQTT after connect: " + $ms)
    if ($ms -like "*connected*" -or $ms -like "*已连接*") {
        Invoke-El (Find-ById $win "BtnMqttPublish"); Start-Sleep -Seconds 3
        Log ("MQTT after publish: " + (Text (Find-ById $win "MqttStatus")))
    } else { Log "MQTT not connected - skipping publish" }

    # 新增：全部显示 / 全部隐藏 按钮（注：WPF CheckBox 在本环境不向 UIA 暴露 TogglePattern，
    # 故复选框勾选状态无法用 UIA 读取；此处仅验证按钮存在、点击不崩溃、且隐藏不影响列表计数）
    Check "BtnShowAll" (Find-ById $win "BtnShowAll")
    Check "BtnHideAll" (Find-ById $win "BtnHideAll")
    Invoke-El (Find-ById $win "BtnHideAll"); Start-Sleep -Seconds 1
    Log ("after HideAll count: " + (Text (Find-ById $win "CloudCountText")) + " (隐藏不删列表项，仍应为 2)")
    Invoke-El (Find-ById $win "BtnShowAll"); Start-Sleep -Seconds 1
    Log ("after ShowAll count: " + (Text (Find-ById $win "CloudCountText")))

    # ===== 切片渐显工作流（核心需求：导入后按切片显示，不直接显示完整点云）=====
    Expand-ByName $win "切片显示（导入后渐显）"
    Check "SliceAxisCombo" (Find-ById $win "SliceAxisCombo")
    Check "SliceCountBox" (Find-ById $win "SliceCountBox")
    Check "BtnSlicePlay" (Find-ById $win "BtnSlicePlay")
    Check "BtnSliceRestart" (Find-ById $win "BtnSliceRestart")
    Check "BtnSliceFull" (Find-ById $win "BtnSliceFull")
    Check "SliceProgress" (Find-ById $win "SliceProgress")
    Check "SliceProgressText" (Find-ById $win "SliceProgressText")

    $spBefore = (Text (Find-ById $win "SliceProgressText"))
    Log ("slice state before (应处于切片模式, 不应含'完整显示'): " + $spBefore)
    if ($spBefore -like "*完整显示*") { Log "  !! 未切即显示完整点云，违背需求" } else { Log "  OK 处于切片渐显模式" }

    Invoke-El (Find-ById $win "BtnSliceRestart"); Start-Sleep -Milliseconds 500
    $spRestart = (Text (Find-ById $win "SliceProgressText"))
    Log ("after restart (应 已显示 0/.. 播放中): " + $spRestart)
    Start-Sleep -Seconds 1
    $spProgress = (Text (Find-ById $win "SliceProgressText"))
    Log ("after 1s (进度应增大): " + $spProgress)

    Invoke-El (Find-ById $win "BtnSliceFull"); Start-Sleep -Milliseconds 500
    $spFull = (Text (Find-ById $win "SliceProgressText"))
    Log ("after full (应含'完整显示'): " + $spFull)
    if ($spFull -like "*完整显示*") { Log "  OK 显示完整点云" } else { Log "  !! 未切换到完整显示" }

    SelectComboIndex (Find-ById $win "SliceAxisCombo") 0; Start-Sleep -Milliseconds 800
    $spAxis = (Text (Find-ById $win "SliceProgressText"))
    Log ("after axis=X (应回到切片模式, 不含'完整显示'): " + $spAxis)
    if ($spAxis -like "*完整显示*") { Log "  !! 切换轴后仍为完整显示" } else { Log "  OK 切换轴回到切片模式" }

    SetVal (Find-ById $win "SliceCountBox") "20"; Start-Sleep -Seconds 1
    $spCount = (Text (Find-ById $win "SliceProgressText"))
    Log ("after count=20 (应仍在切片模式): " + $spCount)
    if ($spCount -like "*完整显示*") { Log "  !! 调整切片数后变为完整显示" } else { Log "  OK 调整切片数保持切片模式" }

    SelectComboIndex (Find-ById $win "SliceAxisCombo") 2; Start-Sleep -Milliseconds 600

    # 切片动画导出按钮（存在性校验；不实际落盘）
    Check "GifDelayBox" (Find-ById $win "GifDelayBox")
    Check "BtnExportGif" (Find-ById $win "BtnExportGif")
    Check "BtnExportVideo" (Find-ById $win "BtnExportVideo")
    Log "OK 切片动画导出按钮已就位（GIF / OGV）"

    Invoke-El (Find-ById $win "BtnClearAll"); Start-Sleep -Seconds 1
    Log ("after clear all: " + (Text (Find-ById $win "CloudCountText")))

    Log ("crash_err exists during test: " + (Test-Path $crashErr))
    if (Test-Path $crashErr) { Log ("crash content: " + [System.IO.File]::ReadAllText($crashErr)) }

    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Log "app closed"
} catch {
    Log ("EXCEPTION: " + $_.Exception.Message)
    try { if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } } catch {}
}
