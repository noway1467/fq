param(
    [ValidateRange(1,5000)][int]$Count = 300,
    [ValidateRange(0,1048576)][int]$Size = 4096,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Label = 'current',
    [string]$OutputDirectory,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$WindowsRevision,
    [string]$AndroidRevision,
    [string]$BaselineReport
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path -Parent $PSScriptRoot
$fixtures = Join-Path $root 'tests'
if (!$JavaHome) { $JavaHome = 'C:/Program Files/Java/jdk-17' }
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'build/tmp/wp5/bench' }
$temporaryPrefix = [IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if (!$out.StartsWith($temporaryPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw '基准输出目录必须位于当前项目 build/tmp 内。' }
$run = Join-Path $out $Label
$bin = Join-Path $run 'bin'
$data = Join-Path $run 'd'
if (Test-Path -LiteralPath $run) { throw "标签输出已存在，请指定新 -Label：$run" }
New-Item -ItemType Directory -Path $run,$bin,$data | Out-Null
$env:TEMP=$run; $env:TMP=$run; $env:TMPDIR=$run
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$run+'"'
$encoding = [Text.UTF8Encoding]::new($false)

function Read-BenchSource([string]$RelativePath,[string]$Revision) {
    if (!$Revision) { return [IO.File]::ReadAllText((Join-Path $root $RelativePath)) }
    $lines = & git -C $root show "${Revision}:$RelativePath"
    if ($LASTEXITCODE -ne 0) { throw "读取基准版本源码失败：$Revision $RelativePath" }
    return ($lines -join "`n") + "`n"
}
function Replace-BenchPoint([string]$Text,[string]$Needle,[string]$Replacement) {
    $matches = [regex]::Matches($Text,[regex]::Escape($Needle)).Count
    if ($matches -ne 1) { throw "基准计时点必须唯一（实际 $matches）：$Needle" }
    return $Text.Replace($Needle,$Replacement)
}
function Write-BenchSource([string]$RelativePath,[string]$Revision) {
    $text = (Read-BenchSource $RelativePath $Revision).Replace("`r`n","`n")
    switch ([IO.Path]::GetFileName($RelativePath)) {
        'SecureIdentity.cs' {
            $text=Replace-BenchPoint $text 'SslStream stream = new SslStream(client.GetStream(), false, (sender, remote, chain, errors) => Matches(remote, expectedFingerprint));' 'long benchTls=SmallFileBenchMetrics.Stamp();SslStream stream = new SslStream(client.GetStream(), false, (sender, remote, chain, errors) => Matches(remote, expectedFingerprint));'
            $text=Replace-BenchPoint $text 'stream.AuthenticateAsClient("Feiq", new X509CertificateCollection { certificate }, SslProtocols.Tls12, false); return stream; }' 'stream.AuthenticateAsClient("Feiq", new X509CertificateCollection { certificate }, SslProtocols.Tls12, false); SmallFileBenchMetrics.Add("tlsClient",benchTls); return stream; }'
        }
        'LanService.Offers.cs' {
            $text=Replace-BenchPoint $text 'if(disposed)return;' 'if(disposed)return; long benchSnapshot=SmallFileBenchMetrics.Stamp();'
            $text=Replace-BenchPoint $text 'try{SecureIdentity.AtomicBytes(path,bytes);break;}' 'try{long benchDisk=SmallFileBenchMetrics.Stamp();SecureIdentity.AtomicBytes(path,bytes);SmallFileBenchMetrics.Add("snapshotDisk",benchDisk);SmallFileBenchMetrics.Add("snapshot",benchSnapshot);break;}'
            $text=Replace-BenchPoint $text 'if(SecureIdentity.ReadText(stream,128)=="STORED\n"+offer.Sha256){' 'if(SecureIdentity.ReadText(stream,128)=="STORED\n"+offer.Sha256){SmallFileBenchMetrics.Stored(key);'
            if ($text.Contains('output.Write(entry, 0, entry.Length); output.Flush(true);')) {
                $text=Replace-BenchPoint $text 'output.Write(entry, 0, entry.Length); output.Flush(true);' 'long benchJournal=SmallFileBenchMetrics.Stamp();output.Write(entry, 0, entry.Length); output.Flush(true);SmallFileBenchMetrics.Add("completionJournal",benchJournal);'
            }
        }
        'LanService.Receive.cs' {
            $text=Replace-BenchPoint $text 'connection=SecureConnect(peer.Endpoint,c=>activePd.DirectClient=c);' 'long benchConnect=SmallFileBenchMetrics.Stamp();connection=SecureConnect(peer.Endpoint,c=>activePd.DirectClient=c);'
            $text=Replace-BenchPoint $text 'directStream=Security.Client(connection,peer.Fingerprint);' 'directStream=Security.Client(connection,peer.Fingerprint);SmallFileBenchMetrics.Add("fileTls",benchConnect);long benchAuth=SmallFileBenchMetrics.Stamp();'
            $text=Replace-BenchPoint $text 'Packet remote=AuthenticateSecureClient(directStream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);' 'Packet remote=AuthenticateSecureClient(directStream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);SmallFileBenchMetrics.Add("fileAuth",benchAuth);'
            if ($text.Contains('TcpClient retryClient=SecureConnect(peer.Endpoint,c=>activePd.DirectClient=c);')) {
                $text=Replace-BenchPoint $text 'TcpClient retryClient=SecureConnect(peer.Endpoint,c=>activePd.DirectClient=c);' 'long benchRetry=SmallFileBenchMetrics.Stamp();TcpClient retryClient=SecureConnect(peer.Endpoint,c=>activePd.DirectClient=c);'
                $text=Replace-BenchPoint $text 'SslStream retryStream=Security.Client(retryClient,peer.Fingerprint);' 'SslStream retryStream=Security.Client(retryClient,peer.Fingerprint);SmallFileBenchMetrics.Add("fileTls",benchRetry);long benchRetryAuth=SmallFileBenchMetrics.Stamp();'
                $text=Replace-BenchPoint $text 'Packet retryRemote=AuthenticateSecureClient(retryStream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);' 'Packet retryRemote=AuthenticateSecureClient(retryStream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);SmallFileBenchMetrics.Add("fileAuth",benchRetryAuth);'
            }
            $text=Replace-BenchPoint $text 'output.Write(buffer,0,count);' 'long benchWrite=SmallFileBenchMetrics.Stamp();output.Write(buffer,0,count);SmallFileBenchMetrics.Add("receiveWrite",benchWrite);'
            $text=Replace-BenchPoint $text 'output.Flush(true);output.Position=0;' 'long benchFlush=SmallFileBenchMetrics.Stamp();output.Flush(true);SmallFileBenchMetrics.Add("receiveFlush",benchFlush);long benchHash=SmallFileBenchMetrics.Stamp();output.Position=0;'
            $text=Replace-BenchPoint $text 'if(hash!=file.Sha256)' 'SmallFileBenchMetrics.Add("receiveHash",benchHash);if(hash!=file.Sha256)'
            $text=Replace-BenchPoint $text 'File.Move(part,path);' 'long benchMove=SmallFileBenchMetrics.Stamp();File.Move(part,path);SmallFileBenchMetrics.Add("receiveCommit",benchMove);'
        }
        'SecureIdentity.java' {
            $text=Replace-BenchPoint $text 'try{SSLSocket tls=(SSLSocket)context(expected)' 'long benchTls=SmallFileBenchMetrics.stamp();try{SSLSocket tls=(SSLSocket)context(expected)'
            $text=Replace-BenchPoint $text 'tls.setUseClientMode(true);handshake(tls);return tls;' 'tls.setUseClientMode(true);handshake(tls);SmallFileBenchMetrics.add("tlsClient",benchTls);return tls;'
        }
        'StoredOffers.java' {
            if ($text.Contains('output.write(entry);output.getFD().sync();')) {
                $text=Replace-BenchPoint $text 'output.write(entry);output.getFD().sync();' 'long benchJournal=SmallFileBenchMetrics.stamp();output.write(entry);output.getFD().sync();SmallFileBenchMetrics.add("completionJournal",benchJournal);'
            }
        }
        'LanNode.java' {
            $text=Replace-BenchPoint $text 'if(dataRoot==null)return;' 'if(dataRoot==null)return;long benchSnapshot=SmallFileBenchMetrics.stamp();'
            $text=Replace-BenchPoint $text 'StoredOffers.write(new File(dataRoot,"transfer-offers.dat"),buffer.toByteArray());' 'long benchDisk=SmallFileBenchMetrics.stamp();StoredOffers.write(new File(dataRoot,"transfer-offers.dat"),buffer.toByteArray());SmallFileBenchMetrics.add("snapshotDisk",benchDisk);SmallFileBenchMetrics.add("snapshot",benchSnapshot);'
            $text=Replace-BenchPoint $text 'if(SecureIdentity.readText(socket.getInputStream(),128).equals("STORED\n"+offer.sha256)){' 'if(SecureIdentity.readText(socket.getInputStream(),128).equals("STORED\n"+offer.sha256)){SmallFileBenchMetrics.stored(key);'
            $text=Replace-BenchPoint $text 'raw=secureConnect(peer.endpoint,s->{' 'long benchConnect=SmallFileBenchMetrics.stamp();raw=secureConnect(peer.endpoint,s->{'
            $text=Replace-BenchPoint $text 'directTls=security.client(raw,peer.fingerprint);' 'directTls=security.client(raw,peer.fingerprint);SmallFileBenchMetrics.add("fileTls",benchConnect);long benchAuth=SmallFileBenchMetrics.stamp();'
            $text=Replace-BenchPoint $text 'Protocol.Packet remote=authenticateSecureClient(directTls,peer.deviceId,peer.fingerprint,peer.name);' 'Protocol.Packet remote=authenticateSecureClient(directTls,peer.deviceId,peer.fingerprint,peer.name);SmallFileBenchMetrics.add("fileAuth",benchAuth);'
            if ($text.Contains('Socket retryRaw=secureConnect(peer.endpoint,s->{')) {
                $text=Replace-BenchPoint $text 'Socket retryRaw=secureConnect(peer.endpoint,s->{' 'long benchRetry=SmallFileBenchMetrics.stamp();Socket retryRaw=secureConnect(peer.endpoint,s->{'
                $text=Replace-BenchPoint $text 'SSLSocket retryTls=security.client(retryRaw,peer.fingerprint);' 'SSLSocket retryTls=security.client(retryRaw,peer.fingerprint);SmallFileBenchMetrics.add("fileTls",benchRetry);long benchRetryAuth=SmallFileBenchMetrics.stamp();'
                $text=Replace-BenchPoint $text 'Protocol.Packet retryRemote=authenticateSecureClient(retryTls,peer.deviceId,peer.fingerprint,peer.name);' 'Protocol.Packet retryRemote=authenticateSecureClient(retryTls,peer.deviceId,peer.fingerprint,peer.name);SmallFileBenchMetrics.add("fileAuth",benchRetryAuth);'
            }
            $text=Replace-BenchPoint $text 'while(bytes.hasRemaining())output.write(bytes);' 'long benchWrite=SmallFileBenchMetrics.stamp();while(bytes.hasRemaining())output.write(bytes);SmallFileBenchMetrics.add("receiveWrite",benchWrite);'
            $text=Replace-BenchPoint $text 'output.force(true);output.position(0);' 'long benchFlush=SmallFileBenchMetrics.stamp();output.force(true);SmallFileBenchMetrics.add("receiveFlush",benchFlush);long benchHash=SmallFileBenchMetrics.stamp();output.position(0);'
            $text=Replace-BenchPoint $text 'if(!hash.toString().equals(file.sha256))' 'SmallFileBenchMetrics.add("receiveHash",benchHash);if(!hash.toString().equals(file.sha256))'
            $text=Replace-BenchPoint $text 'Files.move(part.toPath(),destination.toPath());' 'long benchMove=SmallFileBenchMetrics.stamp();Files.move(part.toPath(),destination.toPath());SmallFileBenchMetrics.add("receiveCommit",benchMove);'
        }
    }
    $destination=Join-Path $bin ([IO.Path]::GetFileName($RelativePath))
    [IO.File]::WriteAllText($destination,$text,$encoding)
    return $destination
}

$windowsFiles=@('src/Protocol.cs','src/Models.cs','src/MessageText.cs','src/RequestInput.cs','src/SecureIdentity.cs','src/LanNetwork.cs','src/SendBatch.cs')
$windowsFiles+=@(Get-ChildItem -LiteralPath "$root/src" -Filter 'LanService*.cs' -File | ForEach-Object { 'src/'+$_.Name })
$windowsSources=@($windowsFiles | ForEach-Object { Write-BenchSource $_ $WindowsRevision })
$androidSources=@(Get-ChildItem -LiteralPath "$root/android/app/src/main/java/org/feiqlight/android/core" -Filter '*.java' -File | ForEach-Object { Write-BenchSource ('android/app/src/main/java/org/feiqlight/android/core/'+$_.Name) $AndroidRevision })
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /utf8output /optimize+ /warn:4 /warnaserror+ /target:exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$bin/SmallFileBench.exe" @windowsSources (Join-Path $fixtures 'SmallFileBenchMetrics.cs') (Join-Path $fixtures 'SmallFileBench.cs')
if ($LASTEXITCODE -ne 0) { throw 'Windows 基准夹具编译失败。' }
& "$JavaHome/bin/javac.exe" -encoding UTF-8 -d $bin @androidSources (Join-Path $fixtures 'SmallFileBenchMetrics.java') (Join-Path $fixtures 'SmallFileBench.java')
if ($LASTEXITCODE -ne 0) { throw 'Android 核心基准夹具编译失败。' }
Add-Type -Path (Join-Path $fixtures 'LoopbackPorts.cs')
$windowsPort=[FeiqLight.Tests.LoopbackPorts]::Chat()
do { $androidPort=[FeiqLight.Tests.LoopbackPorts]::Chat() } while ($androidPort -eq $windowsPort)
$windowsProcess=$null; $androidProcess=$null
$resources=@{windows=@{workingSetBytes=0L;privateBytes=0L;handles=0;threads=0;peakCpuPercent=0.0};android=@{workingSetBytes=0L;privateBytes=0L;handles=0;threads=0;peakCpuPercent=0.0}}
$cpuSamples=@{}
try {
    $windowsProcess=Start-Process -FilePath "$bin/SmallFileBench.exe" -ArgumentList @($windowsPort,$androidPort,('"'+$data+'"'),$Count,$Size) -PassThru -WindowStyle Hidden -RedirectStandardOutput "$run/windows.log" -RedirectStandardError "$run/windows-errors.log"
    $null=$windowsProcess.Handle
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath "$data/windows-ready")) {
        if ($windowsProcess.HasExited -or $deadline.Elapsed.TotalSeconds -gt 30) { throw 'Windows 基准节点未就绪。' }
        Start-Sleep -Milliseconds 25
    }
    $androidProcess=Start-Process -FilePath "$JavaHome/bin/java.exe" -ArgumentList @('-Dfile.encoding=UTF-8','-cp',('"'+$bin+'"'),'SmallFileBench',$androidPort,$windowsPort,('"'+$data+'"'),$Count,$Size) -PassThru -WindowStyle Hidden -RedirectStandardOutput "$run/android.log" -RedirectStandardError "$run/android-errors.log"
    $null=$androidProcess.Handle
    while (!$windowsProcess.HasExited -or !$androidProcess.HasExited) {
        foreach ($entry in @(@{name='windows';process=$windowsProcess},@{name='android';process=$androidProcess})) {
            if ($entry.process.HasExited) { continue }
            $entry.process.Refresh(); $sample=$resources[$entry.name]
            $timestamp=[Diagnostics.Stopwatch]::GetTimestamp();$cpu=$entry.process.TotalProcessorTime.TotalMilliseconds
            if($cpuSamples.ContainsKey($entry.name)) {
                $previous=$cpuSamples[$entry.name];$milliseconds=1000.0*($timestamp-$previous.timestamp)/[Diagnostics.Stopwatch]::Frequency
                $sample.peakCpuPercent=[Math]::Max($sample.peakCpuPercent,100*($cpu-$previous.cpu)/$milliseconds/[Environment]::ProcessorCount)
            }
            $cpuSamples[$entry.name]=@{timestamp=$timestamp;cpu=$cpu}
            $sample.workingSetBytes=[Math]::Max($sample.workingSetBytes,$entry.process.WorkingSet64)
            $sample.privateBytes=[Math]::Max($sample.privateBytes,$entry.process.PrivateMemorySize64)
            $sample.handles=[Math]::Max($sample.handles,$entry.process.HandleCount)
            $sample.threads=[Math]::Max($sample.threads,$entry.process.Threads.Count)
        }
        if ((Test-Path -LiteralPath "$data/failed") -or $deadline.Elapsed.TotalMinutes -gt 20) { throw '基准失败或超时，请查看双端日志。' }
        Start-Sleep -Milliseconds 25
    }
    if ($windowsProcess.ExitCode -ne 0 -or $androidProcess.ExitCode -ne 0) { throw '基准程序校验失败。' }
    $results=@()
    foreach ($direction in @(@{key='wa';name='Windows→Android';receiver='android';sender='windows'},@{key='aw';name='Android→Windows';receiver='windows';sender='android'})) {
        $receiver=Get-Content -LiteralPath "$data/$($direction.key)-$($direction.receiver).json" -Raw | ConvertFrom-Json
        $sender=Get-Content -LiteralPath "$data/$($direction.key)-$($direction.sender).json" -Raw | ConvertFrom-Json
        $latencies=@($receiver.latenciesMs | Sort-Object)
        $results+=[pscustomobject][ordered]@{
            direction=$direction.name;count=$Count;size=$Size
            firstMs=$receiver.latenciesMs[0];totalMs=$receiver.elapsedMs;filesPerSecond=1000*$Count/$receiver.elapsedMs
            p50Ms=$latencies[[Math]::Ceiling($Count*0.50)-1];p95Ms=$latencies[[Math]::Ceiling($Count*0.95)-1]
            fileTlsHandshakes=$receiver.counts.fileTls;allTlsHandshakes=([int]$receiver.counts.tlsClient+[int]$sender.counts.tlsClient)
            snapshotWrites=$sender.counts.snapshot;completionJournalWrites=[int]$sender.counts.completionJournal;completionJournalMs=[double]$sender.segmentsMs.completionJournal
            tlsConnectMs=$receiver.segmentsMs.fileTls;authenticationMs=$receiver.segmentsMs.fileAuth
            snapshotMs=$sender.segmentsMs.snapshot;snapshotDiskMs=$sender.segmentsMs.snapshotDisk
            receiveDiskMs=([double]$receiver.segmentsMs.receiveWrite+[double]$receiver.segmentsMs.receiveFlush+[double]$receiver.segmentsMs.receiveCommit)
            receiveHashMs=$receiver.segmentsMs.receiveHash;receiver=$receiver;sender=$sender
        }
    }
    $report=[ordered]@{
        label=$Label;timestamp=(Get-Date).ToString('o');revision=(& git -C $root rev-parse HEAD);windowsRevision=$WindowsRevision;androidRevision=$AndroidRevision
        environment=[ordered]@{os=[Environment]::OSVersion.VersionString;processorCount=[Environment]::ProcessorCount;powerShell=$PSVersionTable.PSVersion.ToString();java=(& "$JavaHome/bin/java.exe" -version 2>&1 | Out-String).Trim();transport='TLS 1.2；双端生产核心；全局串行；配对/邀请准备不计时；Android 为 JVM 核心，未计 SAF 导出'}
        resources=$resources;results=$results
    }
    [IO.File]::WriteAllText((Join-Path $run 'result.json'),($report | ConvertTo-Json -Depth 10),$encoding)
    $markdown=@("# 小文件基准 $Label","","$Count 件 × $Size 字节，真实 TLS 1.2 回环，双向串行。",'', '| 方向 | 首件 ms | 总耗时 ms | 件/秒 | P50 ms | P95 ms | 文件/全部握手 | 邀请写盘 |','| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
    foreach ($row in $results) { $markdown+='| {0} | {1:F2} | {2:F2} | {3:F2} | {4:F2} | {5:F2} | {6}/{7} | {8} |' -f $row.direction,$row.firstMs,$row.totalMs,$row.filesPerSecond,$row.p50Ms,$row.p95Ms,$row.fileTlsHandshakes,$row.allTlsHandshakes,$row.snapshotWrites }
    $markdown+=@('', '| 方向 | 建连+TLS ms（占总耗时） | 设备认证 ms | 快照 ms（占总耗时） | 其中物理写盘 ms | 接收写/刷/提交 ms（占总耗时） | SHA-256 ms |','| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
    foreach ($row in $results) { $markdown+='| {0} | {1:F2}（{2:F1}%） | {3:F2} | {4:F2}（{5:F1}%） | {6:F2} | {7:F2}（{8:F1}%） | {9:F2} |' -f $row.direction,$row.tlsConnectMs,(100*$row.tlsConnectMs/$row.totalMs),$row.authenticationMs,$row.snapshotMs,(100*$row.snapshotMs/$row.totalMs),$row.snapshotDiskMs,$row.receiveDiskMs,(100*$row.receiveDiskMs/$row.totalMs),$row.receiveHashMs }
    $markdown+=@('', '分段在两端分别计时，可并行重叠，百分比不能相加；设备认证单列。首件/P50/P95 包含每件建连、认证、接收校验与提交。快照包含序列化、原子替换与必要重试。', '', '资源峰值以 25 ms 采样，含节点启动和邀请准备；CPU 峰值按全部逻辑核心归一，CPU 总耗时与托管内存见 JSON 各方向的 sender/receiver。Android JVM 的数据不能替代真机 SAF 性能。', '', '| 进程 | 工作集 MiB | 私有内存 MiB | CPU 峰值 % | 句柄峰值 | 线程峰值 |','| --- | ---: | ---: | ---: | ---: | ---: |')
    foreach ($name in @('windows','android')) { $sample=$resources[$name];$markdown+='| {0} | {1:F2} | {2:F2} | {3:F2} | {4} | {5} |' -f $name,($sample.workingSetBytes/1MB),($sample.privateBytes/1MB),$sample.peakCpuPercent,$sample.handles,$sample.threads }
    [IO.File]::WriteAllText((Join-Path $run 'result.md'),($markdown -join "`n")+"`n",$encoding)
    if ($BaselineReport) {
        $baseline=Get-Content -LiteralPath $BaselineReport -Raw | ConvertFrom-Json
        $comparison=@('# 小文件优化前后对照','', '| 方向/版本 | 总耗时 ms | 件/秒 | 文件握手 | 快照写盘 | P50 ms | P95 ms |','| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
        foreach ($row in $results) {
            $before=@($baseline.results | Where-Object direction -eq $row.direction)[0]
            if (!$before -or $before.count -ne $row.count -or $before.size -ne $row.size) { throw '前后基准方向、件数或文件大小不一致。' }
            foreach ($version in @(@{label=$baseline.label;row=$before},@{label=$Label;row=$row})) { $entry=$version.row;$comparison+='| {0} / {1} | {2:F2} | {3:F2} | {4} | {5} | {6:F2} | {7:F2} |' -f $entry.direction,$version.label,$entry.totalMs,$entry.filesPerSecond,$entry.fileTlsHandshakes,$entry.snapshotWrites,$entry.p50Ms,$entry.p95Ms }
        }
        [IO.File]::WriteAllText((Join-Path $out 'comparison.md'),($comparison -join "`n")+"`n",$encoding)
    }
    $markdown -join "`n"
} finally {
    foreach ($process in @($windowsProcess,$androidProcess)) {
        if (!$process) { continue }
        if (!$process.HasExited) {
            $actual=Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
            $expected=if ($process.Id -eq $windowsProcess.Id) { "$bin/SmallFileBench.exe" } else { "$JavaHome/bin/java.exe" }
            if ($actual -and [String]::Equals([IO.Path]::GetFullPath($actual.ExecutablePath),[IO.Path]::GetFullPath($expected),[StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $process.Id }
        }
        $process.Dispose()
    }
}
