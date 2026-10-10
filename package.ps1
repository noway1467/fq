$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& "$root\build.ps1" -Test
New-Item -ItemType Directory -Force -Path "$root\build\archives" | Out-Null
$zip = Join-Path $root 'build\archives\飞Q-0.3.20-portable.zip'
Compress-Archive -LiteralPath "$root\dist\飞Q.exe","$root\dist\README.md" -DestinationPath $zip -Force
Get-Item -LiteralPath "$root\dist\飞Q.exe",$zip | Select-Object FullName,Length
Get-FileHash -LiteralPath "$root\dist\飞Q.exe" -Algorithm SHA256
