param([string]$JavaHome = $env:JAVA_HOME)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$javaTestSource = Join-Path $PSScriptRoot 'PartialInterop.java'
$windowsTestSource = Join-Path $PSScriptRoot 'PartialInterop.cs'
$temporary = Join-Path $root 'build/tmp/fix-0.7.19/interop'
$logs = Join-Path $root 'build/fix-0.7.19-20261122'
$data = Join-Path $root ('build/test-data/partial-interop-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temporary,$logs,$data | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$temporary+'"'
if (!$JavaHome) { $JavaHome = 'C:/Program Files/Java/jdk-17' }
$core = @(Get-ChildItem -LiteralPath "$root/android/app/src/main/java/org/feiqlight/android/core" -Filter '*.java' | ForEach-Object FullName)
& "$JavaHome/bin/javac.exe" -encoding UTF-8 -d $temporary @core $javaTestSource
if ($LASTEXITCODE -ne 0) { throw 'Android 断点互通入口编译失败' }
$sources = @('Protocol.cs','Models.cs','MessageText.cs','RequestInput.cs','LanService.cs','SecureIdentity.cs','LanNetwork.cs','SendBatch.cs') | ForEach-Object { Join-Path "$root/src" $_ }
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /utf8output /warn:4 /warnaserror+ /target:exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$temporary/PartialInterop.exe" @sources $windowsTestSource
if ($LASTEXITCODE -ne 0) { throw 'Windows 断点互通入口编译失败' }
function FreePort {
    $tcp = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $tcp.Start()
    try { $tcp.LocalEndpoint.Port } finally { $tcp.Stop() }
}
$windowsPort=FreePort; do { $androidPort=FreePort } while ($androidPort -eq $windowsPort)
$process=$null
try {
    $process=Start-Process -FilePath "$temporary/PartialInterop.exe" -ArgumentList @($windowsPort,('"'+$data+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logs/partial-interop-windows.log" -RedirectStandardError "$logs/partial-interop-windows-errors.log"
    $null=$process.Handle
    $ready=$false
    for($attempt=0;$attempt -lt 100;$attempt++) {
        if((Test-Path -LiteralPath "$logs/partial-interop-windows.log") -and (Get-Content -LiteralPath "$logs/partial-interop-windows.log" -Raw) -match 'READY') { $ready=$true; break }
        if($process.HasExited) { break }
        Start-Sleep -Milliseconds 50
    }
    if(!$ready) { throw 'Windows 断点互通节点未就绪' }
    Write-Output "DATA $data"
    & "$JavaHome/bin/java.exe" '-Dfile.encoding=UTF-8' -cp $temporary PartialInterop $androidPort $windowsPort $data | Tee-Object -FilePath "$logs/partial-interop-android.log"
    if($LASTEXITCODE -ne 0) { throw '双端断点定向互通失败' }
    if(!$process.WaitForExit(10000)) { throw 'Windows 互通节点未退出' }
    Get-Content -LiteralPath "$logs/partial-interop-windows.log"
    if($process.ExitCode -ne 0) { throw 'Windows 互通验证失败' }
    $receiverOffset = [regex]::Match((Get-Content -LiteralPath "$logs/partial-interop-windows.log" -Raw),'Android→Windows 续传偏移=(\d+)').Groups[1].Value
    $senderOffset = [regex]::Match((Get-Content -LiteralPath "$logs/partial-interop-android.log" -Raw),'Android→Windows 发送源续传偏移=(\d+)').Groups[1].Value
    if(!$receiverOffset -or $receiverOffset -ne $senderOffset) { throw 'Android 发送源实际续传偏移与 Windows 断点不一致' }
    Write-Output "PASS Android 发送源实际续传偏移与 Windows 断点一致：$receiverOffset"
} finally {
    if($process -and !$process.HasExited) {
        $actual=Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
        if($actual -and [String]::Equals([IO.Path]::GetFullPath($actual.ExecutablePath),[IO.Path]::GetFullPath("$temporary/PartialInterop.exe"),[StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $process.Id }
    }
    if($process) { $process.Dispose() }
}
