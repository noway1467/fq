param([switch]$Server,[string]$TemporaryDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temporary=if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $root 'build/tmp/transfer-features'}
if(!$temporary.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'build/transfer-feature/validation'}
if(!$output.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '测试输出必须位于项目 build 内'}
$data=Join-Path $temporary ('data-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $temporary,$output,$data | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments=@('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.TransferFeatureTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/win32manifest:$root\src\app.manifest","/out:$temporary\TransferFeatureTests.exe")
foreach($asset in @('index.html','app.css','app.js','guest.html','guest.css','guest.js','qrcodegen.js')){$arguments+="/resource:$root\assets\tools\$asset,FeiqLight.Tools.$asset"}
$arguments+=@("/resource:$root\src\emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$root\src\emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$root\assets\avatars\atlas.png,FeiqLight.Avatars.png","/resource:$root\assets\avatars\catalog.txt,FeiqLight.Avatars.txt")
& $compiler @arguments @sources "$root\tests\TransferFeatureTests.cs"
if($LASTEXITCODE -ne 0){throw '传输功能测试编译失败'}
if($Server){& "$temporary/TransferFeatureTests.exe" $data $output server; if($LASTEXITCODE -ne 0){throw '传输功能测试失败'}}
else {
    & "$temporary/TransferFeatureTests.exe" $data $output | Tee-Object -FilePath "$output/windows-tests.log"
    if($LASTEXITCODE -ne 0){throw '传输功能测试失败'}
    & node "$root\tests\guest-queue.cjs" | Tee-Object -FilePath "$output/guest-queue.log"
    if($LASTEXITCODE -ne 0){throw '访客网页队列测试失败'}
}
