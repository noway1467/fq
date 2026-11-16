param([string]$Label='fixed', [switch]$Preview)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$temporary=Join-Path $projectRoot 'build/tmp/ui-interaction-20261117'
$validation=Join-Path $projectRoot 'build/ui-interaction-validation-20261117'
$testData=Join-Path $projectRoot ('build/test-data/ui-interaction-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temporary,$validation,$testData | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$binary=Join-Path $temporary 'UiInteractionTests.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments=@('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.UiInteractionTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/out:$binary",('/win32manifest:'+(Join-Path $projectRoot 'src/app.manifest')))
foreach($asset in @(@('src/emoji-atlas.png','FeiqLight.EmojiAtlas.png'),@('src/emoji-atlas.txt','FeiqLight.EmojiAtlas.txt'),@('assets/avatars/atlas.png','FeiqLight.Avatars.png'),@('assets/avatars/catalog.txt','FeiqLight.Avatars.txt'),@('docs/Twemoji-LICENSE-GRAPHICS.txt','FeiqLight.Twemoji-LICENSE-GRAPHICS.txt'))) {$arguments+='/resource:'+(Join-Path $projectRoot $asset[0])+','+$asset[1]}
foreach($asset in @('index.html','app.css','app.js')) {$arguments+='/resource:'+(Join-Path $projectRoot "assets/tools/$asset")+",FeiqLight.Tools.$asset"}
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" @arguments @sources (Join-Path $PSScriptRoot 'UiInteractionTests.cs')
if($LASTEXITCODE -ne 0){throw '界面交互回归编译失败'}
& $binary $testData $validation $(if($Preview){'preview'}else{'test'}) | Tee-Object -FilePath (Join-Path $validation "$Label.log")
$result=$LASTEXITCODE
Write-Output "隔离测试资料：$testData"
if($result -ne 0){throw "界面交互回归失败，日志：$validation/$Label.log"}
