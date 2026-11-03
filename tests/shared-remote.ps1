param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build/tmp/shared-remote'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:TEMP=$out; $env:TMP=$out; $env:TMPDIR=$out
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments = @('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.ToolsTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/win32manifest:$root\src\app.manifest","/out:$out\ToolsTests.exe")
foreach ($asset in @('index.html','app.css','app.js')) { $arguments += "/resource:$root\assets\tools\$asset,FeiqLight.Tools.$asset" }
& $compiler @arguments @sources "$root\tests\ToolsTests.cs"
if ($LASTEXITCODE -ne 0) { throw '共享与远控测试编译失败' }
$data = Join-Path $root 'build/test-data/shared-remote'
$mode = if ($Desktop) { 'desktop' } else { 'test' }
& "$out/ToolsTests.exe" $mode $data
if ($LASTEXITCODE -ne 0) { throw '共享与远控测试失败' }
