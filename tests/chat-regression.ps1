param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$testOutput=Join-Path $projectRoot 'build/tmp/chat-regression'
$validationOutput=Join-Path $projectRoot 'build/release-0.7.1'
$testData=Join-Path $projectRoot ('build/test-data/chat-regression-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testOutput,$validationOutput,$testData | Out-Null
$env:TEMP=$testOutput;$env:TMP=$testOutput;$env:TMPDIR=$testOutput
$testBinary=Join-Path $testOutput 'ChatRegressionTests.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments=@('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.ChatRegressionTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/out:$testBinary",('/win32manifest:'+(Join-Path $projectRoot 'src/app.manifest')))
foreach($asset in @(@('src/emoji-atlas.png','FeiqLight.EmojiAtlas.png'),@('src/emoji-atlas.txt','FeiqLight.EmojiAtlas.txt'),@('assets/avatars/atlas.png','FeiqLight.Avatars.png'),@('assets/avatars/catalog.txt','FeiqLight.Avatars.txt'),@('docs/Twemoji-LICENSE-GRAPHICS.txt','FeiqLight.Twemoji-LICENSE-GRAPHICS.txt'))) {$arguments+='/resource:'+(Join-Path $projectRoot $asset[0])+','+$asset[1]}
foreach($asset in @('index.html','app.css','app.js')) {$arguments+='/resource:'+(Join-Path $projectRoot "assets/tools/$asset")+",FeiqLight.Tools.$asset"}
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" @arguments @sources (Join-Path $PSScriptRoot 'ChatRegressionTests.cs')
if($LASTEXITCODE -ne 0){throw '聊天定向回归编译失败'}
Write-Output "DATA $testData"
& $testBinary $testData $validationOutput | Tee-Object -FilePath (Join-Path $validationOutput 'chat-regression.log')
if($LASTEXITCODE -ne 0){throw '聊天定向回归失败'}
$resolved=[IO.Path]::GetFullPath($testData)
if(!$resolved.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'build/test-data')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '拒绝越界清理'}
Write-Output "清理已验证测试资料：$resolved"
Remove-Item -LiteralPath $resolved -Recurse -Force
