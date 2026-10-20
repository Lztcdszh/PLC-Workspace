<#
    PLCTesting —— MQTT 连接诊断工具
    用途：在一台“连不上 MQTT”的电脑上运行，逐层定位故障点。
    层次：配置 → hosts → DNS → TCP 连通 → MQTT 握手 → 代理 → 防火墙/安全软件 → 时钟
    用法：双击同目录下的 诊断MQTT.bat（推荐），或
          powershell -NoProfile -ExecutionPolicy Bypass -File 诊断MQTT.ps1
#>

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

# ── 报告收集 ────────────────────────────────────────────────
$script:Pass = 0
$script:Fail = 0
$script:Warn = 0
$script:Report = New-Object System.Collections.ArrayList

function Line([string]$t) { Write-Host $t; [void]$script:Report.Add($t) }
function Head([string]$t) { Line ''; Line ('=' * 70); Line $t; Line ('=' * 70) }
function OK([string]$t)   { $script:Pass++; Line ('[ OK ] ' + $t) }
function BAD([string]$t)  { $script:Fail++; Line ('[FAIL] ' + $t) }
function WRN([string]$t)  { $script:Warn++; Line ('[WARN] ' + $t) }
function INFO([string]$t) { Line ('       ' + $t) }

# ── 工具函数 ────────────────────────────────────────────────
function Test-TcpPort {
    param([string]$TargetHost, [int]$Port, [int]$TimeoutMs = 4000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $c  = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($TargetHost, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
            throw ('连接超时（超过 {0} ms 无响应，典型特征＝被防火墙/运营商静默丢包）' -f $TimeoutMs)
        }
        $c.EndConnect($iar)
        $sw.Stop()
        return @{ Ok = $true; Ms = [int]$sw.ElapsedMilliseconds; Err = '' }
    } catch {
        $sw.Stop()
        return @{ Ok = $false; Ms = [int]$sw.ElapsedMilliseconds; Err = $_.Exception.Message }
    } finally {
        try { $c.Close() } catch { }
    }
}

function Get-ConnAckText {
    param([int]$Code)
    switch ($Code) {
        0  { return '连接被接受（正常）' }
        1  { return '服务端拒绝：不支持的协议版本' }
        2  { return '服务端拒绝：ClientId 不合格' }
        3  { return '服务端拒绝：服务端不可用' }
        4  { return '服务端拒绝：用户名或密码错误' }
        5  { return '服务端拒绝：未授权（需要账号）' }
        default { return ('服务端拒绝：未知返回码 ' + $Code) }
    }
}

function Test-MqttHandshake {
    param([string]$TargetHost, [int]$Port, [string]$ClientId, [int]$TimeoutMs = 6000)
    $res = @{ Ok = $false; Rc = -1; Ms = 0; Err = '' }
    $sw  = [System.Diagnostics.Stopwatch]::StartNew()
    $c   = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($TargetHost, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { throw 'TCP 连接超时' }
        $c.EndConnect($iar)
        $stream = $c.GetStream()
        $stream.ReadTimeout  = $TimeoutMs
        $stream.WriteTimeout = $TimeoutMs

        $cid = [System.Text.Encoding]::ASCII.GetBytes($ClientId)
        $vh  = New-Object System.Collections.ArrayList
        [void]$vh.Add([byte]0x00); [void]$vh.Add([byte]0x04)
        $vh.AddRange([System.Text.Encoding]::ASCII.GetBytes('MQTT'))
        [void]$vh.Add([byte]0x04)   # 协议级别 4 = 3.1.1
        [void]$vh.Add([byte]0x02)   # 连接标志：CleanSession
        [void]$vh.Add([byte]0x00); [void]$vh.Add([byte]0x3C)  # KeepAlive 60s
        [void]$vh.Add([byte](($cid.Length -shr 8) -band 0xFF))
        [void]$vh.Add([byte]($cid.Length -band 0xFF))
        $vh.AddRange($cid)

        $len = $vh.Count
        $rl  = New-Object System.Collections.ArrayList
        do {
            $d   = $len % 128
            $len = [math]::Floor($len / 128)
            if ($len -gt 0) { $d = $d -bor 0x80 }
            [void]$rl.Add([byte]$d)
        } while ($len -gt 0)

        $pktList = New-Object System.Collections.ArrayList
        [void]$pktList.Add([byte]0x10)
        $pktList.AddRange($rl)
        $pktList.AddRange($vh)
        $pkt = $pktList.ToArray()

        $stream.Write($pkt, 0, $pkt.Length)
        $stream.Flush()

        $buf = New-Object byte[] 4
        $got = 0
        while ($got -lt 4) {
            $k = $stream.Read($buf, $got, 4 - $got)
            if ($k -le 0) { throw '服务端未返回数据就关闭了连接（端口可能不是 MQTT 服务，或被中间设备重置）' }
            $got += $k
        }
        $sw.Stop()
        if ($buf[0] -ne 0x20) { throw ('返回的不是 CONNACK，首字节 0x{0:X2}（该端口不是 MQTT 服务）' -f $buf[0]) }
        $res.Rc  = [int]$buf[3]
        $res.Ok  = ($buf[3] -eq 0)
        $res.Ms  = [int]$sw.ElapsedMilliseconds
        return $res
    } catch {
        $sw.Stop()
        $res.Ms  = [int]$sw.ElapsedMilliseconds
        $res.Err = $_.Exception.Message
        return $res
    } finally {
        try { $c.Close() } catch { }
    }
}

# ═════════════════════════════════════════════════════════════
Head 'PLCTesting MQTT 连接诊断报告'
Line ('生成时间   : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Line ('计算机名   : ' + $env:COMPUTERNAME + '    ← ClientId 里就是这个名字，请记下来')
Line ('当前用户   : ' + $env:USERNAME)
Line ('操作系统   : ' + [System.Environment]::OSVersion.VersionString)
Line ('系统时区   : ' + (Get-TimeZone).DisplayName)
Line ('PS 版本    : ' + $PSVersionTable.PSVersion)
Line ('脚本位置   : ' + $PSScriptRoot)

# ── 1. 定位并解析 mqttconfig.json ───────────────────────────
Head '1) 配置文件 mqttconfig.json'

$candidates = New-Object System.Collections.ArrayList
if ($PSScriptRoot) { [void]$candidates.Add((Join-Path $PSScriptRoot 'mqttconfig.json')) }
[void]$candidates.Add('D:\PLCTesting\mqttconfig.json')
[void]$candidates.Add('C:\PLCTesting\mqttconfig.json')
if ($env:ProgramFiles)        { [void]$candidates.Add((Join-Path $env:ProgramFiles 'PLCTesting\mqttconfig.json')) }
if (${env:ProgramFiles(x86)}) { [void]$candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'PLCTesting\mqttconfig.json')) }
[void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'PLCTesting\mqttconfig.json'))

# 顺带从卸载注册表里找真实安装目录
$uninstRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($root in $uninstRoots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($p -and $p.DisplayName -and $p.DisplayName -like '*PLCTesting*') {
            INFO ('注册表已安装：' + $p.DisplayName + '  版本 ' + $p.DisplayVersion + '  位置 ' + $p.InstallLocation)
            if ($p.InstallLocation) { [void]$candidates.Add((Join-Path $p.InstallLocation 'mqttconfig.json')) }
        }
    }
}

$configPath = $null
foreach ($c in $candidates) {
    if ($c -and (Test-Path $c)) { $configPath = $c; break }
}

$BrokerHost  = '127.0.0.1'
$BrokerPort  = 1883
$MqttEnabled = $false

if ($configPath) {
    OK ('找到配置文件：' + $configPath)
    $raw = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
    Line '---- 文件原文 ------------------------------------------'
    foreach ($l in ($raw -split "`r?`n")) { Line ('  ' + $l) }
    Line '-------------------------------------------------------'
    try {
        $cfg = $raw | ConvertFrom-Json
        if ($cfg.BrokerHost)     { $BrokerHost  = [string]$cfg.BrokerHost }
        if ($cfg.BrokerPort)     { $BrokerPort  = [int]$cfg.BrokerPort }
        if ($null -ne $cfg.Enabled) { $MqttEnabled = [bool]$cfg.Enabled }
        OK  ('解析成功：BrokerHost=' + $BrokerHost + '  BrokerPort=' + $BrokerPort + '  Enabled=' + $MqttEnabled)
        if ($cfg.BaseTopic)         { INFO ('BaseTopic=' + $cfg.BaseTopic) }
        if ($cfg.PublishMode)       { INFO ('PublishMode=' + $cfg.PublishMode) }
        if ($cfg.PublishIntervalMs) { INFO ('PublishIntervalMs=' + $cfg.PublishIntervalMs) }
        if (-not $MqttEnabled) { WRN 'Enabled=false —— 软件根本不会去连 MQTT！这就是“连不上”的直接原因。' }
        if ($BrokerHost -eq '127.0.0.1' -or $BrokerHost -eq 'localhost') {
            WRN ('BrokerHost 是 ' + $BrokerHost + '（本机回环）。只有 broker 也跑在这台电脑上才成立；' +
                 '如果 broker 在别的机器上，这里必须是公网/隧道域名。')
        }
    } catch {
        BAD ('配置文件不是合法 JSON：' + $_.Exception.Message)
    }
} else {
    BAD '在下列位置都没有找到 mqttconfig.json'
    foreach ($c in $candidates) { INFO ('  查找过：' + $c) }
    INFO '将使用程序内置默认值 127.0.0.1:1883 继续探测'
}

# ── 2. hosts 文件 ──────────────────────────────────────────
Head '2) hosts 文件是否被改写'
$hostsFile = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$hostHits  = @()
if (Test-Path $hostsFile) {
    $hostHits = Get-Content $hostsFile -ErrorAction SilentlyContinue |
                Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*#' -and $_ -match [regex]::Escape($BrokerHost) }
}
if ($BrokerHost -match '^[0-9\.]+$') {
    INFO 'BrokerHost 本身是 IP，跳过 hosts 检查'
} elseif ($hostHits.Count -gt 0) {
    WRN 'hosts 文件里有针对该域名的条目（可能导致解析到错误地址）：'
    foreach ($h in $hostHits) { INFO ('  ' + $h) }
} else {
    OK 'hosts 文件里没有该域名的劫持条目'
}

# ── 3. DNS 解析 ────────────────────────────────────────────
Head '3) DNS 解析 ' 
$resolvedIps = @()
if ($BrokerHost -match '^[0-9\.]+$') {
    $resolvedIps = @($BrokerHost)
    INFO ('BrokerHost 是 IP，无需解析：' + $BrokerHost)
} else {
    try {
        $dnsTask = [System.Net.Dns]::GetHostAddressesAsync($BrokerHost)
        if ($dnsTask.Wait(6000)) {
            foreach ($a in $dnsTask.Result) { $resolvedIps += $a.IPAddressToString }
        } else {
            throw 'DNS 解析超过 6 秒未返回'
        }
    } catch { }
    if ($resolvedIps.Count -gt 0) {
        OK ('解析成功：' + $BrokerHost + ' → ' + ($resolvedIps -join ', '))
        foreach ($ip in $resolvedIps) {
            if ($ip -match ':') { WRN ('解析结果含 IPv6 地址 ' + $ip + '：若本机没有可用 IPv6 出口，程序可能优先走它导致超时') }
        }
    } else {
        BAD ('DNS 解析失败：' + $BrokerHost + ' 无法解析为 IP')
        INFO '常见原因：该机 DNS 屏蔽了 .vicp.fun / .oray 类动态域名；或断网；或 DNS 服务器不可达'
        INFO '对比测试：手动执行 nslookup ' + $BrokerHost + ' 8.8.8.8 看是否只有默认 DNS 失败'
    }
}

# ── 4. TCP 连通性 ──────────────────────────────────────────
Head '4) TCP 连通性（3 次）'
if ($resolvedIps.Count -eq 0) {
    BAD '无可用 IP，跳过 TCP 测试'
} else {
    $tcpTarget = $resolvedIps[0]
    $tcpOkCount = 0
    for ($i = 1; $i -le 3; $i++) {
        $r = Test-TcpPort -TargetHost $tcpTarget -Port $BrokerPort -TimeoutMs 4000
        if ($r.Ok) {
            $tcpOkCount++
            OK ('第 ' + $i + ' 次：TCP ' + $tcpTarget + ':' + $BrokerPort + ' 连接成功，耗时 ' + $r.Ms + ' ms')
        } else {
            BAD ('第 ' + $i + ' 次：TCP ' + $tcpTarget + ':' + $BrokerPort + ' 失败（' + $r.Ms + ' ms）：' + $r.Err)
        }
        Start-Sleep -Milliseconds 300
    }
    if ($tcpOkCount -eq 0) {
        INFO '结论方向：网络层就不通，还没到 MQTT 协议层面。优先查：'
        INFO '  - 该机出站防火墙/安全软件是否只放行 80/443，拦截了非常规端口 ' + $BrokerPort
        INFO '  - 该机所在网络（不同运营商/4G/酒店网/公司网）是否封了该端口'
        INFO '  - 域名解析出的 IP 是否与本机（正常电脑）解析到的一致'
    } elseif ($tcpOkCount -lt 3) {
        WRN 'TCP 时通时不通 —— 链路不稳定或存在限速/限连接数'
    }
}

# ── 5. MQTT 协议握手 ──────────────────────────────────────
Head '5) MQTT 协议握手（CONNECT → CONNACK）'
$diagClientId = 'diag_' + $env:COMPUTERNAME + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 16)
INFO ('使用 ClientId：' + $diagClientId)
$hsTarget = if ($resolvedIps.Count -gt 0) { $resolvedIps[0] } else { $BrokerHost }
$hs = $null
for ($i = 1; $i -le 2; $i++) {
    $hs = Test-MqttHandshake -TargetHost $hsTarget -Port $BrokerPort -ClientId $diagClientId
    if ($hs.Ok) {
        OK ('第 ' + $i + ' 次握手成功：CONNACK rc=' + $hs.Rc + '（' + (Get-ConnAckText $hs.Rc) + '），耗时 ' + $hs.Ms + ' ms')
        break
    } else {
        BAD ('第 ' + $i + ' 次握手失败（' + $hs.Ms + ' ms）：' + $hs.Err)
        if ($hs.Rc -ge 0) { INFO ('返回码说明：' + (Get-ConnAckText $hs.Rc)) }
    }
    Start-Sleep -Milliseconds 500
}
if ($hs -and $hs.Ok) {
    INFO '本机网络与 broker 完全正常 —— 说明问题在 PLCTesting 自身（配置未生效 / 旧版本 / 未重启软件）'
}

# ── 6. 系统代理 ────────────────────────────────────────────
Head '6) 系统代理设置（MQTT 走裸 TCP，代理会被忽略，但代理软件常抢占/拦截连接）'
$inetKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$proxyEnable = $null
$proxyServer = $null
try {
    $proxyEnable = (Get-ItemProperty -Path $inetKey -Name ProxyEnable -ErrorAction SilentlyContinue).ProxyEnable
    $proxyServer = (Get-ItemProperty -Path $inetKey -Name ProxyServer -ErrorAction SilentlyContinue).ProxyServer
} catch { }
if ($proxyEnable -eq 1) {
    WRN ('系统代理已启用：' + $proxyServer + ' —— 若代理软件开了“全局/透明代理”，可能拦截非 80/443 端口')
} else {
    OK '系统代理未启用'
}
$envProxy = @()
foreach ($n in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy')) {
    $v = [System.Environment]::GetEnvironmentVariable($n)
    if ($v) { $envProxy += ($n + '=' + $v) }
}
if ($envProxy.Count -gt 0) { WRN ('环境变量代理：' + ($envProxy -join '; ')) } else { OK '无代理环境变量' }

# ── 7. 防火墙 / 安全软件 ──────────────────────────────────
Head '7) 防火墙与安全软件'
try {
    $fws = Get-NetFirewallProfile -ErrorAction Stop
    foreach ($f in $fws) {
        $msg = ('防火墙配置档 ' + $f.Name + '：启用=' + $f.Enabled + '，默认出站=' + $f.DefaultOutboundAction)
        # NotConfigured 表示沿用系统默认（允许出站），不算风险
        if ($f.Enabled -and $f.DefaultOutboundAction -eq 'Block') { WRN ($msg + '  ← 默认阻止出站，会拦掉 MQTT') }
        else { INFO $msg }
    }
} catch {
    WRN ('无法读取防火墙配置：' + $_.Exception.Message)
}
$avPatterns = '360tray|360safe|360sd|ZhuDongFangYu|huorong|hrsword|sysdiag|qqpcmgr|QQPCTray|kxetray|kav|avp|mcafee|symantec|eset|nod32|vamFlt|Vamtoo|dlp|Sophos|Trend'
$avProcs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match $avPatterns }
if ($avProcs) {
    WRN '发现可能拦截网络的安全软件进程：'
    foreach ($p in ($avProcs | Select-Object -Unique ProcessName)) { INFO ('  ' + $p.ProcessName) }
    INFO '若怀疑被拦：把 D:\PLCTesting\PLCTesting.exe 加入白名单后重试'
} else {
    OK '未发现常见安全软件/终端管控进程'
}

# ── 8. 时钟偏差 ───────────────────────────────────────────
Head '8) 时钟偏差（MQTT 不用 TLS，影响小，仅作记录）'
try {
    $resp = Invoke-WebRequest -Uri 'http://www.baidu.com' -Method Head -UseBasicParsing -TimeoutSec 6
    $dateHeader = $resp.Headers['Date']
    if ($dateHeader) {
        $serverTime = [DateTime]::Parse($dateHeader.ToString()).ToUniversalTime()
        $localTime  = [DateTime]::UtcNow
        $skewSec    = [math]::Round(($localTime - $serverTime).TotalSeconds)
        if ([math]::Abs($skewSec) -gt 300) {
            WRN ('本机时钟与网络时间相差 ' + $skewSec + ' 秒（超过 5 分钟）')
        } else {
            OK ('时钟偏差 ' + $skewSec + ' 秒，正常')
        }
    } else {
        INFO '未能从响应头取到服务器时间'
    }
} catch {
    WRN ('外网 HTTP 访问失败：' + $_.Exception.Message + ' —— 该机可能整体无法访问外网')
}

# ── 9. PLCTesting 运行实例与日志 ──────────────────────────
Head '9) PLCTesting 进程与最近日志'
$procs = Get-Process -Name 'PLCTesting' -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        $path = ''
        try { $path = $p.MainModule.FileName } catch { }
        OK ('正在运行：PID ' + $p.Id + '  启动于 ' + $p.StartTime.ToString('yyyy-MM-dd HH:mm:ss') + '  路径 ' + $path)
        if ($path) {
            try {
                $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
                INFO ('程序版本：' + $fv.FileVersion + '  （产品版本 ' + $fv.ProductVersion + '）')
                if ($fv.FileVersion -and ([version]($fv.FileVersion) -lt [version]'1.1.5')) {
                    WRN ('程序版本低于 1.1.5，请先升级到 1.1.5 再测 —— 旧版本的 MQTT 出错信息不写日志，无法定位')
                }
            } catch { }
        }
    }
} else {
    WRN 'PLCTesting 当前没有运行'
}
$logDir = Join-Path $env:LOCALAPPDATA 'PLCTesting\Logs'
if (Test-Path $logDir) {
    $latestLog = Get-ChildItem $logDir -Filter 'plctesting-*.log' -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latestLog) {
        INFO ('最新日志：' + $latestLog.FullName + '（最后写入 ' + $latestLog.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + '）')
        Line '---- 日志最后 40 行 ------------------------------------'
        # 日志是 UTF-8 编码，必须显式指定，否则中文全变乱码
        $logTail = Get-Content -LiteralPath $latestLog.FullName -Tail 40 -Encoding UTF8 -ErrorAction SilentlyContinue
        foreach ($l in $logTail) { Line ('  ' + $l) }
        Line '-------------------------------------------------------'
        $mqttLines = Get-Content -LiteralPath $latestLog.FullName -Encoding UTF8 -ErrorAction SilentlyContinue |
                     Where-Object { $_ -match 'MQTT|Mqtt|mqtt' } | Select-Object -Last 15
        if ($mqttLines) {
            Line '---- 日志中与 MQTT 有关的行（最后 15 条）---------------'
            foreach ($l in $mqttLines) { Line ('  ' + $l) }
            Line '-------------------------------------------------------'
        } else {
            INFO '日志里没有任何 MQTT 相关记录 —— 说明当前版本把 MQTT 异常吞掉了（只显示在界面状态栏）'
        }
    } else {
        INFO ('日志目录存在但没有 plctesting-*.log 文件：' + $logDir)
    }
} else {
    WRN ('没有日志目录：' + $logDir)
}

# ── 10. 汇总 ─────────────────────────────────────────────
Head '诊断汇总'
Line ('通过 ' + $script:Pass + ' 项，失败 ' + $script:Fail + ' 项，警告 ' + $script:Warn + ' 项')
Line ''
if ($script:Fail -eq 0 -and $script:Warn -eq 0) {
    Line '本机网络到 broker 的链路完全正常。若软件仍显示连不上，排查方向：'
    Line '  1. 软件是不是旧版本（看上面第 9 节的程序版本），旧版本 MQTT 逻辑不同'
    Line '  2. 改完 mqttconfig.json 后软件是否已重启（配置只在启动时读取）'
    Line '  3. 把上面第 9 节的日志连同本报告一起发回'
} else {
    Line '按上面的 [FAIL] / [WARN] 逐条处理即可。把本报告完整发回，可据此定位。'
}

# ── 保存报告 ────────────────────────────────────────────
$desktop = [Environment]::GetFolderPath('Desktop')
if (-not $desktop) { $desktop = $env:TEMP }
$reportFile = Join-Path $desktop ('MQTT诊断报告_' + $env:COMPUTERNAME + '_' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
try {
    $script:Report | Out-File -FilePath $reportFile -Encoding UTF8
    Write-Host ''
    Write-Host ('报告已保存：' + $reportFile) -ForegroundColor Green
} catch {
    Write-Host ('报告保存失败：' + $_.Exception.Message) -ForegroundColor Red
}
