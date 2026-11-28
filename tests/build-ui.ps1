$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& "$root\build.ps1"
. "$root/tools/windows-package.ps1"
$package=Get-WindowsPackage -Directory (Join-Path $root 'dist')
if(!$package){throw '未找到 Windows 安装包'}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path "$root\build" | Out-Null
Copy-Item -LiteralPath $package.Path -Destination "$root\build\飞Q.exe" -Force
$common = @('/nologo','/utf8output','/optimize+','/warnaserror+','/target:winexe',"/win32manifest:$root\src\app.manifest",'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',"/r:$($package.Path)")
& $compiler @common '/define:CLIENT_A' "/out:$root\build\FeiqLight.UiA.exe" "$PSScriptRoot\UiHarness.cs"
if ($LASTEXITCODE -ne 0) { throw 'A 客户端烟测入口编译失败' }
& $compiler @common "/out:$root\build\FeiqLight.UiB.exe" "$PSScriptRoot\UiHarness.cs"
if ($LASTEXITCODE -ne 0) { throw 'B 客户端烟测入口编译失败' }
