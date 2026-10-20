<#
    PLCTesting —— MQTT 出站连接 进阶诊断（对照矩阵）
    用途：在"连不上 MQTT"的电脑上运行，用一组对照目标判断封锁到底发生在哪一层：
          A. 这个网络封了一切非常规出站端口？
          B. 只是到花生壳那个 IP/端口的路由被丢包？
          C. 这台机器其实和 broker 在同一局域网，可直接内网连接？
    用法：双击同目录下的 诊断MQTT-进阶.bat
#>

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$script:Report = New-Object System.Collections.ArrayList
function Line([string]$t) { Write-Host $t; [void]$script:Report.Add($t) }
function Head([string]$t) { Line ''; Line ('=' * 72); Line $t; Line ('=' * 72) }

# ── 从 mqttconfig.json 取真实目标，取不到就用现场已知值 ──────────
$BrokerHost = '709ce3nz4388.vicp.fun'
$BrokerPort = 57863
$cfgPath = $null
foreach ($c in @((Join-Path $PSScriptRoot 'mqttconfig.json'), 'D:\PLCTesting\mqttconfig.json')) {
    if ($c -and (Test-Path $c)) { $cfgPath = $c; break }
}
if ($cfgPath) {
    try {
        $cfg = (Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8) | ConvertFrom-Json
        if ($cfg.BrokerHost) { $BrokerHost = [string]$cfg.BrokerHost }
        if ($cfg.BrokerPort) { $BrokerPort = [int]$cfg.BrokerPort }
    } catch { }
}

function Test-Tcp([string]$h, [int]$p, [int]$TimeoutMs = 3000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($h, $p, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { throw 'TIMEOUT' }
        $c.EndConnect($iar)
        $sw.Stop()
        return @{ Ok = $true; Ms = [int]$sw.ElapsedMilliseconds; Kind = 'OK'; Err = '' }
    } catch {
        $sw.Stop()
        $m = $_.Exception.Message
        $kind = 'ERROR'
        if ($m -eq 'TIMEOUT') { $kind = 'TIMEOUT' }
        elseif ($m -match 'refused|拒绝|actively refused') { $kind = 'REFUSED' }
        elseif ($m -match 'No such host|找不到|not known') { $kind = 'DNSFAIL' }
        elseif ($m -match 'unreachable|无法访问|不可达') { $kind = 'UNREACH' }
        return @{ Ok = $false; Ms = [int]$sw.ElapsedMilliseconds; Kind = $kind; Err = $m }
    } finally { try { $c.Close() } catch { } }
}

function Resolve-Ip([string]$h) {
    try {
        $t = [System.Net.Dns]::GetHostAddressesAsync($h)
        if ($t.Wait(5000)) {
            return (($t.Result | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | ForEach-Object { $_.IPAddressToString }) -join ',')
        }
    } catch { }
    return ''
}

function Resolve-WithServer([string]$h, [string]$server) {
    try {
        $r = Resolve-DnsName -Name $h -Server $server -Type A -DnsOnly -ErrorAction Stop
        return (($r | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress }) -join ',')
    } catch {
        return ('查询失败：' + $_.Exception.Message)
    }
}

Head 'MQTT 出站连接 进阶诊断'
Line ('生成时间 : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Line ('计算机名 : ' + $env:COMPUTERNAME)
Line ('目标     : ' + $BrokerHost + ':' + $BrokerPort + '  （来自 ' + $cfgPath + '）')

# ── 本机网络 ──────────────────────────────────────────────
Head '0) 本机网络位置'
$lanIps = @()
try {
    $lanIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
              Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.PrefixOrigin -ne 'WellKnown' } |
              ForEach-Object { $_.IPAddress + '/' + $_.PrefixLength + ' (' + $_.InterfaceAlias + ')' }
} catch { }
if ($lanIps) { foreach ($i in $lanIps) { Line ('  本机地址：' + $i) } } else { Line '  未能枚举网卡地址' }
try {
    $gw = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
          Select-Object -First 1
    if ($gw) { Line ('  默认网关：' + $gw.NextHop + '  接口 ' + $gw.InterfaceAlias) }
} catch { }

# ── 隧道域名解析（多 DNS 对照）────────────────────────────
Head '1) 隧道域名解析（换 DNS 服务器对照）'
$defIp = Resolve-Ip $BrokerHost
Line ('  默认 DNS      → ' + $(if ($defIp) { $defIp } else { '解析失败' }))
$aliasIp = ''
if ($defIp) {
    $first = ($defIp -split ',')[0]
    try {
        $rev = [System.Net.Dns]::GetHostEntry($first)
        if ($rev -and $rev.HostName) { $aliasIp = $rev.HostName }
    } catch { }
}
Line ('  8.8.8.8       → ' + (Resolve-WithServer $BrokerHost '8.8.8.8'))
Line ('  223.5.5.5     → ' + (Resolve-WithServer $BrokerHost '223.5.5.5'))
Line ('  114.114.114.114 → ' + (Resolve-WithServer $BrokerHost '114.114.114.114'))
Line ''
Line '  说明：若三家结果都是同一个 IP，则"解析被运营商篡改/分流"这一项可排除；'
Line '        若正常机器解析到别的 IP，说明花生壳多线路给不同来源分了不同入口，需换线路。'

# ── 对照连接矩阵 ─────────────────────────────────────────
Head '2) 对照连接矩阵（关键：看哪一组通、哪一组不通）'
if ($defIp) { $tunnelIp = ($defIp -split ',')[0] } else { $tunnelIp = $BrokerHost }

$targets = @(
    @{ Name = '花生壳隧道(故障目标)'; Host = $tunnelIp;             Port = $BrokerPort; Note = '本次真正要连的地址，应通' },
    @{ Name = '隧道同IP';             Host = $tunnelIp;             Port = 80;          Note = '判断包能否到达该主机' },
    @{ Name = '隧道同IP';             Host = $tunnelIp;             Port = 443;         Note = '判断包能否到达该主机' },
    @{ Name = '公共MQTT hivemq';      Host = 'broker.hivemq.com';   Port = 1883;        Note = '非常规端口出站是否被整体封' },
    @{ Name = '公共MQTT mosquitto';   Host = 'test.mosquitto.org';  Port = 1883;        Note = '同上，第二个样本' },
    @{ Name = '公共MQTT hivemq(TLS)'; Host = 'broker.hivemq.com';   Port = 8883;        Note = 'TLS 端口对照' },
    @{ Name = 'broker局域网地址';     Host = '192.168.7.103';       Port = 1883;        Note = '若通＝同网段，可直连绕过隧道' },
    @{ Name = '百度';                 Host = 'www.baidu.com';       Port = 443;         Note = '基础外网是否正常' },
    @{ Name = '百度';                 Host = 'www.baidu.com';       Port = 80;          Note = '基础外网是否正常' }
)

$results = @()
$fmt = '  {0,-22} {1,-6} {2,-17} {3,-8} {4,-8} {5}'
Line ($fmt -f '目标', '端口', '解析到', '结果', '耗时', '判定')
Line ('  ' + ('-' * 92))
foreach ($t in $targets) {
    $ip = Resolve-Ip $t.Host
    $ipShow = if ($ip) { ($ip -split ',')[0] } else { '(解析失败)' }
    $r = Test-Tcp -h $t.Host -p $t.Port -TimeoutMs 3000
    $resText = switch ($r.Kind) {
        'OK'      { '通' }
        'TIMEOUT' { '超时' }
        'REFUSED' { '被拒绝' }
        'UNREACH' { '不可达' }
        'DNSFAIL' { '解析失败' }
        default   { '错误' }
    }
    $verdict = switch ($r.Kind) {
        'OK'      { '连通' }
        'TIMEOUT' { '包被丢弃' }
        'REFUSED' { '端口关闭(可达)' }
        'UNREACH' { '路由不可达' }
        default   { $r.Err }
    }
    Line ($fmt -f $t.Name, $t.Port, $ipShow, $resText, ($r.Ms.ToString() + 'ms'), $verdict)
    Line ('       ^ ' + $t.Note)
    $results += [pscustomobject]@{ Name = $t.Name; Port = $t.Port; Kind = $r.Kind; Note = $t.Note }
}

# ── 路由追踪 ─────────────────────────────────────────────
Head '3) 到隧道 IP 的路由追踪（前 12 跳，看在哪一跳断）'
try {
    $tr = & tracert -d -h 12 -w 800 $tunnelIp 2>&1
    foreach ($l in $tr) { Line ('  ' + $l) }
} catch {
    Line ('  tracert 执行失败：' + $_.Exception.Message)
}

# ── 结论 ────────────────────────────────────────────────
Head '4) 自动判定'

$tunnel  = $results | Where-Object { $_.Name -like '*隧道(故障目标)*' } | Select-Object -First 1
$pubMqtt = $results | Where-Object { $_.Name -like '*hivemq*' -and $_.Port -eq 1883 } | Select-Object -First 1
$pubMq2  = $results | Where-Object { $_.Name -like '*mosquitto*' -and $_.Port -eq 1883 } | Select-Object -First 1
$tun80   = $results | Where-Object { $_.Name -eq '隧道同IP' -and $_.Port -eq 80 } | Select-Object -First 1
$tun443  = $results | Where-Object { $_.Name -eq '隧道同IP' -and $_.Port -eq 443 } | Select-Object -First 1
$lan     = $results | Where-Object { $_.Name -like '*局域网*' } | Select-Object -First 1
$baidu   = $results | Where-Object { $_.Name -eq '百度' -and $_.Port -eq 443 } | Select-Object -First 1

if ($baidu -and $baidu.Kind -ne 'OK') {
    Line '  症状：连 百度:443 都不通 → 这台机器整体没外网（或极不稳定）。'
    Line '  处理：先修好外网，MQTT 问题会自动消失。'
}
if ($lan -and $lan.Kind -eq 'OK') {
    Line '  ★ 重要发现：这台机器能直连 broker 局域网地址 192.168.7.103:1883。'
    Line '    处理：把 D:\PLCTesting\mqttconfig.json 的 BrokerHost 改成 192.168.7.103、'
    Line '          BrokerPort 改成 1883，重启软件即可，完全绕开花生壳隧道。'
}
if ($tunnel -and $tunnel.Kind -eq 'OK') {
    Line '  花生壳隧道本身是通的：说明网络层没问题，故障在软件侧（旧版本 / 未重启 / 配置未生效）。'
}
if ($tunnel -and $tunnel.Kind -ne 'OK') {
    if (($pubMqtt -and $pubMqtt.Kind -eq 'OK') -or ($pubMq2 -and $pubMq2.Kind -eq 'OK')) {
        Line '  ★ 判定：这个网络并没有封"非常规端口"——它能连公共 MQTT broker 的 1883。'
        Line '    所以问题是"到花生壳这个 IP:端口 的路径被丢弃"，可能原因：'
        Line '      1) 花生壳把该来源 IP 临时拉黑（之前反复重连/异常断开容易被限流拉黑）'
        Line '         → 去花生壳控制台看该映射的访问记录，或直接删掉映射重建/换一个外网端口'
        Line ('      2) 该出口运营商到 ' + $tunnelIp + ' 的路由问题（看上面第 3 节断在哪一跳）')
        Line '      3) 该机所在网络的上层网关对特定目标 IP 做了拦截'
    } else {
        Line '  ★ 判定：这个网络封锁了非常规出站端口（连公共 MQTT 的 1883 都不通）。'
        Line '    处理：MQTT 不能走 1883/57863 这类端口了，必须换成常规端口：'
        Line '      - 花生壳把映射外网端口改成 443（或 8443），并在程序里启用 TLS'
        Line '      - 或者该机换一个网络（手机热点验证一下最快）'
    }
    if ($tun80 -or $tun443) {
        $k80 = if ($tun80) { $tun80.Kind } else { 'N/A' }
        $k443 = if ($tun443) { $tun443.Kind } else { 'N/A' }
        $anySameIpOk = (($tun80 -and $tun80.Kind -ne 'TIMEOUT') -or ($tun443 -and $tun443.Kind -ne 'TIMEOUT'))
        if ($anySameIpOk) {
            Line ('    补充：同一 IP 的 80 端口=' + $k80 + '、443 端口=' + $k443 + '（有响应），')
            Line '          说明数据包能到达该主机 → 属于"端口级过滤"，换一个外网端口即可解决。'
        } else {
            Line ('    补充：同一 IP 的 80 端口=' + $k80 + '、443 端口=' + $k443 + '，全部超时，')
            Line '          说明到该 IP 的整条路径都被丢包 → 更偏向目标侧（花生壳线路/来源黑名单），'
            Line '          而不是本地端口策略。'
        }
    }
}

# ── 保存 ────────────────────────────────────────────────
$desktop = [Environment]::GetFolderPath('Desktop')
if (-not $desktop) { $desktop = $env:TEMP }
$file = Join-Path $desktop ('MQTT进阶诊断_' + $env:COMPUTERNAME + '_' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
try {
    $script:Report | Out-File -FilePath $file -Encoding UTF8
    Write-Host ''
    Write-Host ('报告已保存：' + $file) -ForegroundColor Green
} catch {
    Write-Host ('报告保存失败：' + $_.Exception.Message) -ForegroundColor Red
}
