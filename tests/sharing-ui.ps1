param([string]$RunId = [Guid]::NewGuid().ToString('N'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path $root 'build/tmp/sharing-ui'
$output = Join-Path $root 'build/sharing-ui'
$data = Join-Path $root ('build/test-data/sharing-ui-' + $RunId)
New-Item -ItemType Directory -Force -Path $temporary,$output,$data | Out-Null
$env:TEMP = $temporary; $env:TMP = $temporary; $env:TMPDIR = $temporary
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments = @('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.SharingUiTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/win32manifest:$root\src\app.manifest","/out:$temporary\SharingUiTests.exe")
$arguments += @("/resource:$root\src\emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$root\src\emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$root\assets\avatars\atlas.png,FeiqLight.Avatars.png","/resource:$root\assets\avatars\catalog.txt,FeiqLight.Avatars.txt")
foreach ($asset in @('index.html','app.css','app.js')) { $arguments += "/resource:$root\assets\tools\$asset,FeiqLight.Tools.$asset" }
& $compiler @arguments @sources "$root\tests\SharingUiTests.cs"
if ($LASTEXITCODE -ne 0) { throw '共享与弹窗 UI 测试编译失败' }
& "$temporary/SharingUiTests.exe" $output $data | Tee-Object -FilePath "$output/results.log"
if ($LASTEXITCODE -ne 0) { throw '共享与弹窗 UI 测试失败' }
