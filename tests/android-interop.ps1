param([string]$JavaHome = $env:JAVA_HOME, [switch]$LargeFiles)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workspaceTemp=Join-Path $root 'build\tmp'; New-Item -ItemType Directory -Force -Path $workspaceTemp | Out-Null
$env:TEMP=$workspaceTemp;$env:TMP=$workspaceTemp;$env:TMPDIR=$workspaceTemp
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$workspaceTemp+'"'
if (!$JavaHome) { $JavaHome = 'C:\Program Files\Java\jdk-17' }
$javac = Join-Path $JavaHome 'bin\javac.exe'
$java = Join-Path $JavaHome 'bin\java.exe'
if (!(Test-Path -LiteralPath $javac)) { throw '请使用 -JavaHome 指定 JDK 17 或更高版本。' }
$out = Join-Path $root 'build\android-interop'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$core = @(Get-ChildItem -LiteralPath "$root\android\app\src\main\java\org\feiqlight\android\core" -Filter '*.java' | ForEach-Object FullName)
& $javac -encoding UTF-8 -d $out @core "$PSScriptRoot\AndroidInterop.java"
if ($LASTEXITCODE -ne 0) { throw '安卓通信核心编译失败' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /utf8output /target:exe /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "/out:$out\InteropHost.exe" "$root\src\Protocol.cs" "$root\src\Models.cs" "$root\src\LanService.cs" "$PSScriptRoot\AndroidInterop.cs"
if ($LASTEXITCODE -ne 0) { throw '桌面互通测试入口编译失败' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('FeiqLight-interop-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
function FreePort { $socket = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback,0)); try { $socket.Client.LocalEndPoint.Port } finally { $socket.Dispose() } }
$a=FreePort; do { $b=FreePort } while ($a -eq $b)
$process=$null
$size = if ($LargeFiles) { 273L*1024*1024+7 } else { 3L*1024*1024+7 }
try {
    $process=Start-Process -FilePath "$out\InteropHost.exe" -ArgumentList @($a,('"'+$temp+'"'),$size) -PassThru -WindowStyle Hidden -RedirectStandardOutput "$out\windows.log" -RedirectStandardError "$out\windows-errors.log"
    # PowerShell 5.1 必须在进程退出前保留句柄，否则已退出进程的 ExitCode 可能为 null，误判真实互通失败。
    $null=$process.Handle
    $ready=$false
    for($i=0;$i -lt 100;$i++) { if ((Test-Path -LiteralPath "$out\windows.log") -and ((Get-Content -LiteralPath "$out\windows.log" -Raw) -match 'READY')) { $ready=$true; break }; if($process.HasExited) { break }; Start-Sleep -Milliseconds 50 }
    if(!$ready) { throw '桌面测试节点未就绪' }
    & $java '-Dfile.encoding=UTF-8' -cp $out AndroidInterop $b $a $temp $size
    if ($LASTEXITCODE -ne 0) { throw '安卓与 Windows 互通失败' }
    if(!$process.WaitForExit(10000)) { throw '桌面测试节点未退出' }
    Get-Content -LiteralPath "$out\windows.log"
    if ($process.ExitCode -ne 0) { throw '桌面互通校验失败' }
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    if($process) { $process.Dispose() }
    $resolved=[IO.Path]::GetFullPath($temp)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\FeiqLight-interop-'
    if (!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理意外目录' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
