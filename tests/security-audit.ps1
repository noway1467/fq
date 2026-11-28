param(
    [ValidateSet('Windows','Android','Both')][string]$Platform = 'Both',
    [switch]$Baseline,
    [switch]$ContractsOnly,
    [string]$JavaHome = 'C:\Program Files\Java\jdk-17',
    [string]$TemporaryDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if($Baseline -and $ContractsOnly){throw '历史基线与协议契约不能同时指定。'}
$mode = if ($ContractsOnly) { 'contracts' } elseif ($Baseline) { 'baseline' } else { 'fixed' }
$out = if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $projectRoot "build/tmp/security-audit/$mode"}
if(!$out.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
$data = if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $projectRoot ('build/test-data/security-audit-' + [Guid]::NewGuid().ToString('N'))}
if(!$data.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'build')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '测试输出必须位于项目 build 内'}
New-Item -ItemType Directory -Force -Path $out,$data | Out-Null
$env:TEMP=$out; $env:TMP=$out; $env:TMPDIR=$out
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$out+'"'
Write-Output "DATA $data"
if ($Platform -ne 'Android') {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
    $executable = Join-Path $out 'SecurityAuditTests.exe'
    $auditSource = Join-Path $projectRoot 'tests\SecurityAuditTests.cs'
    $arguments = @('/nologo','/utf8output','/optimize+','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.SecurityAuditTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/out:$executable")
    & $compiler @arguments @sources $auditSource (Join-Path $projectRoot 'tests\AndroidInterop.cs') (Join-Path $projectRoot 'tests\LoopbackPorts.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Windows 漏洞复现入口编译失败' }
    & $executable (Join-Path $data 'windows') $mode
    if ($LASTEXITCODE -ne 0) { throw 'Windows 漏洞验证失败' }
}
if ($Platform -ne 'Windows') {
    $core = @(Get-ChildItem -LiteralPath "$projectRoot/android/app/src/main/java/org/feiqlight/android/core" -Filter '*.java' | ForEach-Object FullName)
    & "$JavaHome/bin/javac.exe" -encoding UTF-8 -d $out @core "$projectRoot/tests/SecurityAudit.java" "$PSScriptRoot/AndroidInterop.java" "$projectRoot/android/app/src/test/java/org/feiqlight/android/TestPorts.java"
    if ($LASTEXITCODE -ne 0) { throw 'Android 漏洞复现入口编译失败' }
    & "$JavaHome/bin/java.exe" '-Dfile.encoding=UTF-8' -cp $out SecurityAudit (Join-Path $data 'android') $mode
    if ($LASTEXITCODE -ne 0) { throw 'Android 漏洞验证失败' }
}
