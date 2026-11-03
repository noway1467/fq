param([string]$Label='current')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build/tmp/video-phone'; New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:TEMP=$out; $env:TMP=$out; $env:TMPDIR=$out
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $csc /nologo /utf8output /optimize+ /warn:0 /target:exe "/out:$out\VideoHttpBench.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "$root\src\ToolsHttp.cs" "$root\src\ToolsService.cs" "$root\src\ToolsPairings.cs" "$root\src\ToolsPreferences.cs" "$root\tests\VideoHttpBench.cs"
if($LASTEXITCODE -ne 0){throw '基准编译失败'}
& "$out/VideoHttpBench.exe" "$root/build/test-data/video-phone" | Tee-Object -FilePath "$root/build/video-phone-$Label.log"
if($LASTEXITCODE -ne 0){throw '基准执行失败'}
