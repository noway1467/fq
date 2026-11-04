param(
    [Parameter(Mandatory=$true)][string]$ExecutablePath,
    [Parameter(Mandatory=$true)][string]$PreviousExecutablePath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build/tmp/upgrade-settings'
$data = Join-Path $root ('build/test-data/upgrade-settings-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:TEMP=$out; $env:TMP=$out; $env:TMPDIR=$out
$next = (Get-Item -LiteralPath $ExecutablePath).FullName
$previous = (Get-Item -LiteralPath $PreviousExecutablePath).FullName
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments = @('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.ToolsTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/win32manifest:$root\src\app.manifest","/out:$out\UpgradeTests.exe")
& $compiler @arguments @sources "$root\tests\ToolsTests.cs"
if ($LASTEXITCODE -ne 0) { throw '升级配置测试编译失败' }
& "$out/UpgradeTests.exe" upgrade $data $next $previous
if ($LASTEXITCODE -ne 0) { throw "升级配置保留失败，隔离资料保留于 $data" }
# 只清理由本次调用生成且已核对边界的隔离资料；失败时保留现场。
$resolved=[IO.Path]::GetFullPath($data)
$expected=[IO.Path]::GetFullPath((Join-Path $root 'build/test-data'))+'\'
if (!$resolved.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理越界测试目录' }
Write-Output "清理隔离升级资料：$resolved"
Remove-Item -LiteralPath $resolved -Recurse -Force
