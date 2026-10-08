param([string]$ArchivePath,[string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workspaceTemp=Join-Path $root 'build\tmp'; New-Item -ItemType Directory -Force -Path $workspaceTemp | Out-Null
$env:TEMP=$workspaceTemp;$env:TMP=$workspaceTemp;$env:TMPDIR=$workspaceTemp
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$workspaceTemp+'"'
$zip = if($ArchivePath) { [IO.Path]::GetFullPath($ArchivePath) } else { Join-Path $root 'build\archives\飞Q-0.3.17-portable.zip' }
if(!$ExecutablePath) { $ExecutablePath=Join-Path $root 'dist\飞Q.exe' }
$sandbox = Join-Path $env:TEMP ('FeiqLight-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$oldTestRoot=$env:FEIQ_TEST_DATA_ROOT
$env:FEIQ_TEST_DATA_ROOT=Join-Path $root 'build\test-data'
try {
    Expand-Archive -LiteralPath $zip -DestinationPath $sandbox
    $extracted = Join-Path $sandbox '飞Q.exe'
    $expected = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash
    if($actual -ne $expected){throw '绿色包中的 EXE 与构建结果不一致'}
    if(!(Test-Path -LiteralPath "$sandbox\README.md")){throw '绿色包缺少使用说明'}
    # 直接引用解压包里的 EXE，而非重新编译 src，防止源码通过但交付包仍带旧问题。
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $testExe = Join-Path $sandbox 'FeiqLight.PackageTests.exe'
    & $compiler /nologo /utf8output /warn:4 /warnaserror+ /target:exe /main:FeiqLight.Tests.SelfTests "/out:$testExe" "/r:$extracted" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/win32manifest:$root\src\app.manifest" "$PSScriptRoot\SelfTests.cs" "$PSScriptRoot\UiSmokeTests.cs" "$root\src\AssemblyInfo.cs"
    if ($LASTEXITCODE -ne 0) { throw '包内程序的回归测试入口编译失败' }
    & $testExe
    $testCode = $LASTEXITCODE
    if(Test-Path -LiteralPath "$sandbox\test-results.txt") { Copy-Item -LiteralPath "$sandbox\test-results.txt" -Destination "$root\build\package-regression-results.txt" -Force }
    if ($testCode -ne 0) { throw '包内程序回归测试失败' }
    $startupTest = Join-Path $sandbox 'StartupSmoke.exe'
    & $compiler /nologo /utf8output /warn:4 /warnaserror+ /target:exe "/out:$startupTest" "/r:$extracted" /r:System.dll /r:System.Core.dll "/win32manifest:$root\src\app.manifest" "$PSScriptRoot\StartupSmoke.cs"
    if ($LASTEXITCODE -ne 0) { throw '自启动烟测入口编译失败' }
    & $startupTest $extracted (Join-Path $root 'build\test-data')
    if ($LASTEXITCODE -ne 0) { throw '包内 EXE 自启动烟测失败' }
    & "$PSScriptRoot\benchmark.ps1" -ExecutablePath $extracted -Seconds 20
    "PACKAGE PASS: archive extracted; SHA256 matched; packaged EXE answered real UDP discovery."
    "SHA256: $actual"
} finally {
    $env:FEIQ_TEST_DATA_ROOT=$oldTestRoot
    $resolved=[IO.Path]::GetFullPath($sandbox);$temp=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
    if(!$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^FeiqLight-package-[a-f0-9]{32}$'){throw '拒绝清理越界解压目录'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
