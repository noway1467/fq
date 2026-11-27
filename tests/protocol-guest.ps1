param([string]$TemporaryDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temporary=if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $root 'build/tmp/pg'}
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'build/protocol-guest'}
if(!$temporary.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
if(!$output.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '输出必须位于项目 build 内'}
New-Item -ItemType Directory -Force -Path $temporary,$output | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$common=@('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/win32manifest:$root\src\app.manifest")
foreach($asset in @('guest.html','guest.css','guest.js','qrcodegen.js')){$common+="/resource:$root\assets\tools\$asset,FeiqLight.Tools.$asset"}
$common+=@("/resource:$root\src\emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$root\src\emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$root\assets\avatars\atlas.png,FeiqLight.Avatars.png","/resource:$root\assets\avatars\catalog.txt,FeiqLight.Avatars.txt")
foreach($entry in @('ProtocolVectorTests','GuestLifetimeTests')) {
    $exe=Join-Path $temporary ($entry+'.exe')
    & $compiler @common "/main:FeiqLight.Tests.$entry" "/out:$exe" @sources "$root\tests\$entry.cs"
    if($LASTEXITCODE -ne 0){throw "$entry 编译失败"}
    & $exe (Join-Path $temporary ('data-'+[Guid]::NewGuid().ToString('N').Substring(0,8))) $output | Tee-Object -FilePath (Join-Path $output ($entry+'.log'))
    if($LASTEXITCODE -ne 0){throw "$entry 验证失败"}
}
