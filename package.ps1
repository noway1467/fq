$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& "$root\build.ps1" -Test
. "$root/tools/windows-package.ps1"
$package=Get-WindowsPackage -Directory (Join-Path $root 'dist')
if(!$package){throw '未找到 Windows 安装包'}
New-Item -ItemType Directory -Force -Path "$root\build\archives" | Out-Null
$version = $package.Version
$zip = Join-Path $root "build\archives\飞Q-$version-portable.zip"
Compress-Archive -LiteralPath $package.Path,"$root\README.md" -DestinationPath $zip -Force
Get-Item -LiteralPath $package.Path,$zip | Select-Object FullName,Length
Get-FileHash -LiteralPath $package.Path -Algorithm SHA256
