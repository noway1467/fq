param([string]$TemporaryDirectory='build/tmp/p2slow')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=[IO.Path]::GetFullPath((Join-Path $root $TemporaryDirectory))
if(!$out.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build/tmp'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:TEMP=$out; $env:TMP=$out; $env:TMPDIR=$out
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /utf8output /warn:4 /warnaserror+ /target:exe /main:FeiqLight.Tests.Phase2ArchiveGatewayTests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll "/out:$out\ArchiveGatewayTests.exe" @sources "$root\tests\Phase2ArchiveGatewayTests.cs"
if($LASTEXITCODE){throw '归档网关测试编译失败'}
& "$out/ArchiveGatewayTests.exe" "$out/data"
if($LASTEXITCODE){throw '归档网关测试失败'}
