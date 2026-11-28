param([string]$Label='current',[string]$TemporaryDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build/tmp/video-phone';$data=Join-Path $root 'build/test-data/video-phone';$log=Join-Path $root "build/video-phone-$Label.log"
if($TemporaryDirectory){
    . "$PSScriptRoot/test-workspace.ps1"
    $out=Resolve-TestTemporaryDirectory $TemporaryDirectory;$data=Join-Path $out 'data';$log=Join-Path $out 'video.log'
}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:TEMP=$out; $env:TMP=$out; $env:TMPDIR=$out
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @('RequestInput','ToolsHttp','ToolsService','ToolsPairings','ToolsPreferences','SecureIdentity','Protocol','Models','MessageText','LanNetwork') |
    ForEach-Object { Join-Path "$root/src" "$_.cs" }
& $csc /nologo /utf8output /optimize+ /warn:0 /target:exe "/out:$out\VideoHttpBench.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll @sources "$root\tests\VideoHttpBench.cs"
if($LASTEXITCODE -ne 0){throw '基准编译失败'}
& "$out/VideoHttpBench.exe" $data | Tee-Object -FilePath $log
if($LASTEXITCODE -ne 0){throw '基准执行失败'}
