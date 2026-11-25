param([string]$TemporaryDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temporary = if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $root 'build/tmp/connection-status'}
$output = if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'build/connection-status-validation'}
if(!$temporary.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
if(!$output.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '验证记录必须位于项目 build 内'}
New-Item -ItemType Directory -Force -Path $temporary,$output | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$arguments = @('/nologo','/utf8output','/optimize+','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.ConnectionStatusTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/win32manifest:$root/src/app.manifest","/out:$temporary/ConnectionStatusTests.exe")
$arguments += @("/resource:$root/src/emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$root/src/emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$root/docs/Twemoji-LICENSE-GRAPHICS.txt,FeiqLight.Twemoji-LICENSE-GRAPHICS.txt","/resource:$root/assets/avatars/atlas.png,FeiqLight.Avatars.png","/resource:$root/assets/avatars/catalog.txt,FeiqLight.Avatars.txt")
foreach ($asset in @('index.html','app.css','app.js','guest.html','guest.css','guest.js','qrcodegen.js')) { $arguments += "/resource:$root/assets/tools/$asset,FeiqLight.Tools.$asset" }
$testSource = Join-Path $root 'tests\ConnectionStatusTests.cs'
& $compiler @arguments @sources $testSource
if($LASTEXITCODE -ne 0){throw '连接状态定向回归编译失败'}
$data = Join-Path $temporary ('data-' + [Guid]::NewGuid().ToString('N'))
Write-Output "DATA $data"
& "$temporary/ConnectionStatusTests.exe" $data $output | Tee-Object -FilePath (Join-Path $output 'connection-status.log')
if($LASTEXITCODE -ne 0){throw '连接状态定向回归失败'}
