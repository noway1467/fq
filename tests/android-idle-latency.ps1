param([string]$JavaHome='C:/Program Files/Java/jdk-11',[int]$WakeDelay=2500)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temp=Join-Path $root 'build/tmp';New-Item -ItemType Directory -Force -Path $temp|Out-Null
$env:TEMP=$temp;$env:TMP=$temp;$env:TMPDIR=$temp;$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$temp+'"'
$out=Join-Path $root 'build/idle-latency';New-Item -ItemType Directory -Force -Path $out|Out-Null
$core=@(Get-ChildItem -LiteralPath "$root/android/app/src/main/java/org/feiqlight/android/core" -Filter '*.java'|ForEach-Object FullName)
& "$JavaHome/bin/javac.exe" -encoding UTF-8 -d $out @core "$PSScriptRoot/IdleSendLatency.java"
if($LASTEXITCODE -ne 0){throw '空闲延迟测试编译失败'}
& "$JavaHome/bin/java.exe" '-Dfile.encoding=UTF-8' -cp $out IdleSendLatency $WakeDelay
if($LASTEXITCODE -ne 0){throw '空闲发送延迟回归失败'}
