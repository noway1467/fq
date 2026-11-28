param([string]$TemporaryDirectory,[string]$JavaHome=$env:JAVA_HOME)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$JavaHome){$JavaHome='C:/Program Files/Java/jdk-17'}
$out=Join-Path $root 'build/tmp/reverse-interop'
$logs=Join-Path $root 'build/reverse-interop-validation'
$data=Join-Path $root ('build/test-data/reverse-interop-'+[Guid]::NewGuid().ToString('N'))
$dataRoot=Join-Path $root 'build/test-data'
if($TemporaryDirectory){
    . "$PSScriptRoot/test-workspace.ps1"
    $out=Resolve-TestTemporaryDirectory $TemporaryDirectory
    $logs=Join-Path $out 'logs';$dataRoot=$out;$data=Join-Path $out ('data-'+[Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Force -Path $out,$logs,$data | Out-Null
$env:TEMP=$out;$env:TMP=$out;$env:TMPDIR=$out;$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$out+'"'
$sources=@(Get-ChildItem -LiteralPath "$root/src" -Filter '*.cs' | ForEach-Object FullName)
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /utf8output /warn:4 /warnaserror+ /target:exe /main:ReverseInterop /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$out\ReverseInterop.exe" @sources "$root\tests\ReverseInterop.cs"
if($LASTEXITCODE -ne 0){throw 'Windows 回连互通编译失败'}
$core=@(Get-ChildItem -LiteralPath "$root/android/app/src/main/java/org/feiqlight/android/core" -Filter '*.java' | ForEach-Object FullName)
& "$JavaHome/bin/javac.exe" -encoding UTF-8 -d $out @core "$root/tests/ReverseInterop.java"
if($LASTEXITCODE -ne 0){throw 'Android 回连互通编译失败'}
Add-Type -Path "$PSScriptRoot/LoopbackPorts.cs"
$a=[FeiqLight.Tests.LoopbackPorts]::WithTools(24000)
$b=[FeiqLight.Tests.LoopbackPorts]::WithTools($a+3)
Write-Output "回环测试端口：Windows $a..$($a+2)，Android $b..$($b+2)"
$process=$null
try{
    $process=Start-Process -FilePath "$out/ReverseInterop.exe" -ArgumentList @(('"'+$data+'"'),$a) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logs/windows.log" -RedirectStandardError "$logs/windows-errors.log"
    $null=$process.Handle
    for($i=0;$i -lt 100 -and !(Test-Path -LiteralPath "$data/windows-ready");$i++){Start-Sleep -Milliseconds 100}
    if(!(Test-Path -LiteralPath "$data/windows-ready")){throw 'Windows 节点未就绪'}
    & "$JavaHome/bin/java.exe" '-Dfile.encoding=UTF-8' -cp $out ReverseInterop $data $b | Tee-Object -FilePath "$logs/android.log"
    if($LASTEXITCODE -ne 0){throw '双端回连互通失败'}
    if(!$process.WaitForExit(10000) -or $process.ExitCode -ne 0){throw 'Windows 回连互通失败'}
    Get-Content -LiteralPath "$logs/windows.log"
}finally{
    if($process -and !$process.HasExited){if($process.Path -ne "$out\ReverseInterop.exe"){throw '进程归属不符'};Stop-Process -Id $process.Id}
    $resolved=[IO.Path]::GetFullPath($data);$prefix=[IO.Path]::GetFullPath($dataRoot).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw '拒绝越界清理'}
    Write-Output "清理已核验测试资料：$resolved";Remove-Item -LiteralPath $resolved -Recurse -Force
}
