param([ValidateRange(1,3600)][int]$Seconds = 20, [string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workspaceTemp=Join-Path $root 'build\tmp'; New-Item -ItemType Directory -Force -Path $workspaceTemp | Out-Null
$env:TEMP=$workspaceTemp;$env:TMP=$workspaceTemp;$env:TMPDIR=$workspaceTemp
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$workspaceTemp+'"'
$exe = if($ExecutablePath){[IO.Path]::GetFullPath($ExecutablePath)}else{Join-Path $root 'dist\飞Q.exe'}
$profile = Join-Path $env:TEMP ('FeiqLight-benchmark-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profile | Out-Null
$probe = [System.Net.Sockets.UdpClient]::new([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, 0))
$portProbe = [System.Net.Sockets.UdpClient]::new([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, 0))
$port = $portProbe.Client.LocalEndPoint.Port
$portProbe.Dispose()
$process = $null
try {
    @{Nickname='性能测试';Group='回环验证';Signature='测试';CloseToTray=$true;Notifications=$false} | ConvertTo-Json | Set-Content -LiteralPath "$profile\settings.json" -Encoding utf8
    $process = Start-Process -FilePath $exe -ArgumentList @('--loopback','--port',$port,'--profile',('"'+$profile+'"')) -WindowStyle Hidden -PassThru
    $probe.Client.ReceiveTimeout = 300
    $ready = $false
    for($attempt=0; $attempt -lt 60 -and !$ready; $attempt++) {
        $wire = [Text.Encoding]::UTF8.GetBytes("1:123:benchmark:local:16777217:benchmark`0benchmark`0")
        $null = $probe.Send($wire,$wire.Length,'127.0.0.1',$port)
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
        try { $reply=$probe.Receive([ref]$remote); $ready=$remote.Port -eq $port -and $reply.Length -gt 0 } catch [Net.Sockets.SocketException] { }
        # 未绑定端口可能立即返回 ICMP 错误，不能把这种情况当成已等待一个超时周期。
        if(!$ready){Start-Sleep -Milliseconds 100}
        $process.Refresh(); if($process.HasExited){throw ('正式 EXE 提前退出：'+$process.ExitCode)}
    }
    if(!$ready){throw '正式 EXE 未在规定时间内回应真实 UDP 探测'}
    Start-Sleep -Seconds 3
    $process.Refresh(); $cpuStart=$process.TotalProcessorTime.TotalMilliseconds
    $clock=[Diagnostics.Stopwatch]::StartNew(); $samples=@()
    for($i=0;$i -lt $Seconds;$i++) {
        Start-Sleep -Seconds 1; $process.Refresh()
        $samples += [pscustomobject]@{WorkingSetMB=[math]::Round($process.WorkingSet64/1MB,2);PrivateMB=[math]::Round($process.PrivateMemorySize64/1MB,2);Handles=$process.HandleCount}
    }
    $cpu=$process.TotalProcessorTime.TotalMilliseconds-$cpuStart
    $report=[ordered]@{Timestamp=(Get-Date).ToString('o');Scenario='正式 EXE；隐藏主面板；一个回环联系人；没有聊天窗口/文件传输；不含渲染测试工具';ExecutableBytes=(Get-Item -LiteralPath $exe).Length;Samples=$Seconds;WorkingSetMB=@{Min=($samples.WorkingSetMB|Measure-Object -Minimum).Minimum;Max=($samples.WorkingSetMB|Measure-Object -Maximum).Maximum;Mean=[math]::Round(($samples.WorkingSetMB|Measure-Object -Average).Average,2)};PrivateMB=@{Min=($samples.PrivateMB|Measure-Object -Minimum).Minimum;Max=($samples.PrivateMB|Measure-Object -Maximum).Maximum;Mean=[math]::Round(($samples.PrivateMB|Measure-Object -Average).Average,2)};CpuMilliseconds=[math]::Round($cpu,2);CpuPercentOfOneCore=[math]::Round(100*$cpu/$clock.Elapsed.TotalMilliseconds,3);Handles=$samples[-1].Handles}
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$root\build\benchmark.json" -Encoding utf8
    $report | ConvertTo-Json -Depth 5
} finally {
    $probe.Dispose()
    if($process){$process.Refresh();if(!$process.HasExited -and $process.Path -eq $exe){Stop-Process -Id $process.Id}}
    $resolved=[IO.Path]::GetFullPath($profile);$temp=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
    if(!$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^FeiqLight-benchmark-[a-f0-9]{32}$'){throw '拒绝清理越界测试目录'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
