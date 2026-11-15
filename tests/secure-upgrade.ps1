param([switch]$WindowsOnly,[switch]$AndroidOnly)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build/tmp/secure-upgrade/tests'
$logs=Join-Path $root 'build/secure-upgrade-validation'
$data=Join-Path $root ('build/test-data/secure-upgrade-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $out,$logs,$data | Out-Null
$env:TEMP=$out;$env:TMP=$out;$env:TMPDIR=$out;$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$out+'"'
Write-Output "DATA $data"
if(!$AndroidOnly){
    $sources=@(Get-ChildItem -LiteralPath "$root/src" -Filter '*.cs' | ForEach-Object FullName)
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /utf8output /warn:4 /warnaserror+ /target:exe /main:FeiqLight.Tests.SecureUpgradeTests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$out\SecureUpgradeTests.exe" @sources "$root\tests\SecureUpgradeTests.cs"
    if($LASTEXITCODE -ne 0){throw 'Windows 安全回归编译失败'}
    & "$out/SecureUpgradeTests.exe" "$data/windows" | Tee-Object -FilePath "$logs/windows-secure.log"
    if($LASTEXITCODE -ne 0){throw 'Windows 安全回归失败'}
}
if(!$WindowsOnly){
    $core=@(Get-ChildItem -LiteralPath "$root/android/app/src/main/java/org/feiqlight/android/core" -Filter '*.java' | ForEach-Object FullName)
    & 'C:/Program Files/Java/jdk-17/bin/javac.exe' -encoding UTF-8 -d $out @core "$root/tests/SecureUpgrade.java"
    if($LASTEXITCODE -ne 0){throw 'Android 核心安全回归编译失败'}
    & 'C:/Program Files/Java/jdk-17/bin/java.exe' '-Dfile.encoding=UTF-8' -cp $out SecureUpgrade "$data/android" | Tee-Object -FilePath "$logs/android-secure.log"
    if($LASTEXITCODE -ne 0){throw 'Android 核心安全回归失败'}
}
$resolved=[IO.Path]::GetFullPath($data)
if(!$resolved.StartsWith([IO.Path]::GetFullPath("$root/build/test-data").TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '拒绝越界清理'}
Write-Output "清理已验证测试资料：$resolved"
Remove-Item -LiteralPath $resolved -Recurse -Force
