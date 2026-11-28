param([string]$JavaHome=$env:JAVA_HOME,[string]$TemporaryDirectory,[switch]$WindowsOnly,[switch]$AndroidOnly,[ValidateSet('all','batch','boundary','recovery','eviction')][string]$Case='all')
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$root=Split-Path -Parent $PSScriptRoot
$fixtures=Join-Path $root 'tests'
if(!$JavaHome){$JavaHome='C:/Program Files/Java/jdk-17'}
$temporary=if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $root 'build/tmp/wp5/regress'}
$prefix=[IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(!$temporary.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw '定向回归资料必须位于当前项目 build/tmp 内。'}
$data=Join-Path $temporary ('d-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$bin=Join-Path $temporary 'bin';$logs=Join-Path $temporary 'logs'
New-Item -ItemType Directory -Force -Path $temporary,$data,$bin,$logs | Out-Null
$env:TEMP=$temporary;$env:TMP=$temporary;$env:TMPDIR=$temporary;$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$temporary+'"'
if(!$AndroidOnly) {
    $sources=@('Protocol.cs','Models.cs','MessageText.cs','RequestInput.cs','SecureIdentity.cs','LanNetwork.cs','SendBatch.cs') | ForEach-Object {Join-Path "$root/src" $_}
    $sources+=@(Get-ChildItem -LiteralPath "$root/src" -Filter 'LanService*.cs' -File | ForEach-Object FullName)
    & "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /utf8output /optimize+ /warn:4 /warnaserror+ /target:exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$bin/SmallFileTransferTests.exe" @sources (Join-Path $fixtures 'LoopbackPorts.cs') (Join-Path $fixtures 'SmallFileTransferTests.cs')
    if($LASTEXITCODE -ne 0){throw 'Windows WP-5 定向回归编译失败。'}
    & "$bin/SmallFileTransferTests.exe" "$data/windows" $Case | Tee-Object -FilePath "$logs/windows-$Case.log"
    if($LASTEXITCODE -ne 0){throw 'Windows WP-5 定向回归失败。'}
}
if(!$WindowsOnly) {
    $core=@(Get-ChildItem -LiteralPath "$root/android/app/src/main/java/org/feiqlight/android/core" -Filter '*.java' -File | ForEach-Object FullName)
    & "$JavaHome/bin/javac.exe" -encoding UTF-8 -d $bin @core (Join-Path $root 'android/app/src/test/java/org/feiqlight/android/TestPorts.java') (Join-Path $fixtures 'SmallFileTransfer.java')
    if($LASTEXITCODE -ne 0){throw 'Android WP-5 定向回归编译失败。'}
    & "$JavaHome/bin/java.exe" '-Dfile.encoding=UTF-8' -cp $bin SmallFileTransfer "$data/android" $Case | Tee-Object -FilePath "$logs/android-$Case.log"
    if($LASTEXITCODE -ne 0){throw 'Android WP-5 定向回归失败。'}
}
