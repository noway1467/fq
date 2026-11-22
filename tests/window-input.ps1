param([switch]$Baseline,[string]$ValidationDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$temporary=Join-Path $projectRoot 'build/tmp/window-input'
$validation=if($ValidationDirectory){[IO.Path]::GetFullPath($ValidationDirectory)}else{Join-Path $projectRoot 'build/release-0.7.7'}
if(!$validation.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'build')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '验证输出必须位于项目 build 内'}
$testData=Join-Path $projectRoot ('build/test-data/window-input-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temporary,$validation,$testData | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$binary=Join-Path $temporary 'WindowInputTests.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments=@('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.WindowInputTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/out:$binary",('/win32manifest:'+(Join-Path $projectRoot 'src/app.manifest')))
foreach($asset in @(@('src/emoji-atlas.png','FeiqLight.EmojiAtlas.png'),@('src/emoji-atlas.txt','FeiqLight.EmojiAtlas.txt'),@('assets/avatars/atlas.png','FeiqLight.Avatars.png'),@('assets/avatars/catalog.txt','FeiqLight.Avatars.txt'),@('docs/Twemoji-LICENSE-GRAPHICS.txt','FeiqLight.Twemoji-LICENSE-GRAPHICS.txt'))) {$arguments+='/resource:'+(Join-Path $projectRoot $asset[0])+','+$asset[1]}
foreach($asset in @('index.html','app.css','app.js')) {$arguments+='/resource:'+(Join-Path $projectRoot "assets/tools/$asset")+",FeiqLight.Tools.$asset"}
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" @arguments @sources (Join-Path $PSScriptRoot 'WindowInputTests.cs')
if($LASTEXITCODE -ne 0){throw '窗口与输入回归编译失败'}
try {
    $label=if($Baseline){'baseline'}else{'fixed'}
    & $binary $testData $validation $label | Tee-Object -FilePath (Join-Path $validation "window-input-$label.log")
    if($LASTEXITCODE -ne 0){throw '窗口与输入回归失败'}
} finally {
    $resolved=[IO.Path]::GetFullPath($testData)
    $allowed=[IO.Path]::GetFullPath((Join-Path $projectRoot 'build/test-data')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw '拒绝越界清理'}
    Write-Output "清理测试资料：$resolved"
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
