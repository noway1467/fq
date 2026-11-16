param([string]$Label='fixed', [switch]$Preview)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$temporary=Join-Path $projectRoot 'build/tmp/resize-layout-20261116'
$validation=Join-Path $projectRoot 'build/resize-layout-validation-20261116'
$testData=Join-Path $projectRoot ('build/test-data/resize-layout-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temporary,$validation,$testData | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$binary=Join-Path $temporary 'ResizeLayoutTests.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments=@('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.ResizeLayoutTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/out:$binary",('/win32manifest:'+(Join-Path $projectRoot 'src/app.manifest')))
foreach($asset in @(@('src/emoji-atlas.png','FeiqLight.EmojiAtlas.png'),@('src/emoji-atlas.txt','FeiqLight.EmojiAtlas.txt'),@('assets/avatars/atlas.png','FeiqLight.Avatars.png'),@('assets/avatars/catalog.txt','FeiqLight.Avatars.txt'),@('docs/Twemoji-LICENSE-GRAPHICS.txt','FeiqLight.Twemoji-LICENSE-GRAPHICS.txt'))) {$arguments+='/resource:'+(Join-Path $projectRoot $asset[0])+','+$asset[1]}
foreach($asset in @('index.html','app.css','app.js')) {$arguments+='/resource:'+(Join-Path $projectRoot "assets/tools/$asset")+",FeiqLight.Tools.$asset"}
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" @arguments @sources (Join-Path $PSScriptRoot 'ResizeLayoutTests.cs')
if($LASTEXITCODE -ne 0){throw '缩放布局回归编译失败'}
& $binary $testData $validation $Label $(if($Preview){'preview'}else{'test'}) | Tee-Object -FilePath (Join-Path $validation "$Label.log")
if($LASTEXITCODE -ne 0){throw "缩放布局回归失败，隔离资料：$testData"}
Write-Output "隔离测试资料：$testData"
