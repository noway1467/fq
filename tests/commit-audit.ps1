param([string]$TemporaryDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$out = if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $projectRoot 'build/tmp/commit-audit'}
if(!$out.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:TEMP=$out; $env:TMP=$out; $env:TMPDIR=$out
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments = @('/nologo','/utf8output','/optimize+','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.CommitAuditTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',"/win32manifest:$projectRoot/src/app.manifest","/out:$out/CommitAuditTests.exe")
$arguments += @("/resource:$projectRoot/src/emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$projectRoot/src/emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$projectRoot/docs/Twemoji-LICENSE-GRAPHICS.txt,FeiqLight.Twemoji-LICENSE-GRAPHICS.txt")
$arguments += @("/resource:$projectRoot/assets/avatars/atlas.png,FeiqLight.Avatars.png","/resource:$projectRoot/assets/avatars/catalog.txt,FeiqLight.Avatars.txt")
foreach ($asset in @('index.html','app.css','app.js')) { $arguments += "/resource:$projectRoot/assets/tools/$asset,FeiqLight.Tools.$asset" }
$auditSource = Join-Path $projectRoot 'tests\CommitAuditTests.cs'
& $compiler @arguments @sources "$PSScriptRoot\LoopbackPorts.cs" $auditSource
if ($LASTEXITCODE -ne 0) { throw '提交审计定向回归编译失败' }
$data = if($TemporaryDirectory){Join-Path $out ('data-' + [Guid]::NewGuid().ToString('N'))}else{Join-Path $projectRoot ('build/test-data/commit-audit-' + [Guid]::NewGuid().ToString('N'))}
Write-Output "DATA $data"
& "$out/CommitAuditTests.exe" $data
if ($LASTEXITCODE -ne 0) { throw '提交审计定向回归失败' }
