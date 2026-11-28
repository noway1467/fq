param(
    [string]$SdkRoot = $env:ANDROID_HOME,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome,
    [string]$OutputDirectory,
    [string]$TemporaryDirectory,
    [string]$TestOutputDirectory,
    [switch]$Test,
    [switch]$Isolated,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$temporary=if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $root 'build\tmp'}
if($TemporaryDirectory -and !$temporary.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$env:GRADLE_USER_HOME=Join-Path $root 'build\gradle-home'
$env:ANDROID_USER_HOME=Join-Path $root 'build/android-home'
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$temporary+'"'
$appBuild=Join-Path $root 'android/app/build'
$isolatedStore=$null
if($Isolated){
    if(!$TemporaryDirectory -or !$OutputDirectory){throw '隔离构建必须指定临时目录与候选包输出目录。'}
    if(![IO.Path]::GetFullPath($OutputDirectory).StartsWith([IO.Path]::GetFullPath("$root/build/tmp")+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '隔离候选包只允许输出到项目 build/tmp 内。'}
    $originalStore=Join-Path $env:ANDROID_USER_HOME 'debug.keystore'
    if(!(Test-Path -LiteralPath $originalStore -PathType Leaf)){throw 'BLOCKED: 缺少用于验证覆盖升级的原签名。'}
    $env:GRADLE_RO_DEP_CACHE=Join-Path $env:GRADLE_USER_HOME 'caches'
    if(!$GradleHome){
        $wrapper=Get-Content -LiteralPath "$root/android/gradle/wrapper/gradle-wrapper.properties" -Raw
        if($wrapper -notmatch 'distributions/(gradle-[\d.]+)-bin\.zip'){throw '无法识别项目固定的 Gradle 版本。'}
        $distribution=$Matches[1]
        $installed=@(Get-ChildItem -Path "$env:GRADLE_USER_HOME/wrapper/dists/$distribution-bin/*/$distribution/bin/gradle.bat" -File)
        if($installed.Count -ne 1){throw 'BLOCKED: 未找到唯一的已安装固定版本 Gradle，可通过 -GradleHome 指定。'}
        $GradleHome=$installed[0].Directory.Parent.FullName
    }
    $env:GRADLE_USER_HOME=Join-Path $temporary 'g'
    $env:ANDROID_USER_HOME=Join-Path $temporary 'h'
    New-Item -ItemType Directory -Force -Path $env:ANDROID_USER_HOME | Out-Null
    $isolatedStore=Join-Path $env:ANDROID_USER_HOME 'debug.keystore'
    $appBuild=Join-Path $temporary 'b'
    if($Test){
        $maven=Join-Path $temporary 'm'
        if(!(Test-Path -LiteralPath $maven)){Copy-Item -LiteralPath "$root/build/maven-repo" -Destination $maven -Recurse}
        $env:JAVA_TOOL_OPTIONS+=' -Dmaven.repo.local="'+$maven+'"'
    }
}
if($TestOutputDirectory){
    $testOutput=[IO.Path]::GetFullPath($TestOutputDirectory)
    if(!$testOutput.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '测试输出必须位于项目 build 内'}
    New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
    $env:JAVA_TOOL_OPTIONS+=' -Dfeiq.previewDir="'+$testOutput+'"'
}
$android = Join-Path $root 'android'
if (!$JavaHome -and (Test-Path -LiteralPath 'C:\Program Files\Java\jdk-17')) { $JavaHome = 'C:\Program Files\Java\jdk-17' }
if (!$JavaHome -or !(Test-Path -LiteralPath (Join-Path $JavaHome 'bin\javac.exe'))) { throw '请使用 -JavaHome 指定 JDK 17 或更高版本。' }
if ($SdkRoot) {
    if (!(Test-Path -LiteralPath (Join-Path $SdkRoot 'platforms\android-36\android.jar'))) { throw 'SDK 缺少 Android 36 平台。' }
    $path = [IO.Path]::GetFullPath($SdkRoot).Replace('\','/').Replace(':','\:')
    if(!$Isolated){[IO.File]::WriteAllText((Join-Path $android 'local.properties'),"sdk.dir=$path`n",[Text.UTF8Encoding]::new($false))}
    else {$env:ANDROID_HOME=$SdkRoot}
} elseif (!(Test-Path -LiteralPath (Join-Path $android 'local.properties'))) { throw '请使用 -SdkRoot 指定 Android SDK，需 platform 36 / build-tools 35.0.0。' }
if (!$SdkRoot) {
    $sdkLine = Get-Content -LiteralPath (Join-Path $android 'local.properties') | Where-Object { $_ -match '^sdk\.dir=' } | Select-Object -First 1
    if (!$sdkLine) { throw 'local.properties 缺少 sdk.dir' }
    $SdkRoot = $sdkLine.Substring(8).Replace('\:',':').Replace('\\','\')
}
. "$root/tools/android-package.ps1"
$previousApks = @(Get-ChildItem -LiteralPath (Join-Path $root 'dist') -Filter '*.apk' -ErrorAction SilentlyContinue)
if ($previousApks.Count -gt 0 -and !(Test-Path -LiteralPath (Join-Path $root 'build/android-home/debug.keystore'))) {
    throw '原 Android 签名文件缺失，拒绝自动生成新签名。请恢复 build/android-home/debug.keystore，不要卸载旧应用。'
}
$gradle = if ($GradleHome) { Join-Path $GradleHome 'bin\gradle.bat' } else { Join-Path $android 'gradlew.bat' }
if (!(Test-Path -LiteralPath $gradle)) { throw '未找到 Gradle 入口。' }
$oldJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    if($Isolated){Copy-Item -LiteralPath $originalStore -Destination $isolatedStore}
    $previousPackages = @($previousApks | ForEach-Object { Get-AndroidPackageInfo -Apk $_.FullName -SdkRoot $SdkRoot })
    $tasks = @('-p',$android,'--no-daemon','assemblePreview','lintPreview')
    if($TemporaryDirectory){$tasks+=@('--project-cache-dir',(Join-Path $temporary 'pc'))}
    if($Isolated){$tasks+=@("-PfeiqBuildDir=$appBuild","-PfeiqSigningStore=$isolatedStore",'-Pandroid.builder.sdkDownload=false')}
    if ($Test) {
        $tasks += @('testDebugUnitTest','lintDebug')
        if($Isolated){$tasks+='--continue'}
    }
    if ($Offline) { $tasks += '--offline' }
    & $gradle @tasks
    if ($LASTEXITCODE -ne 0) { throw 'Android 构建或验证失败，未更新交付 APK。' }
    $candidate = Get-AndroidPackageInfo -Apk "$appBuild/outputs/apk/preview/app-preview.apk" -SdkRoot $SdkRoot
    Assert-AndroidUpgrade $null $candidate
    foreach ($previous in $previousPackages) { Assert-AndroidUpgrade $previous $candidate }
    if ($previousPackages.Count -gt 0) { Write-Output 'PASS Android 包名、签名及覆盖升级版本检查；无需卸载旧应用。' }
    else { Write-Warning '未找到 dist 中的旧 APK，本次无法验证签名升级兼容。' }
    $out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'dist' }; New-Item -ItemType Directory -Force -Path $out | Out-Null
    $metadata = Get-Content -LiteralPath "$appBuild/outputs/apk/preview/output-metadata.json" -Raw | ConvertFrom-Json
    $apk = Join-Path $out ("飞Q-android-" + $metadata.elements[0].versionName + '.apk')
    Copy-Item -LiteralPath "$appBuild/outputs/apk/preview/app-preview.apk" -Destination $apk -Force
    Get-Item -LiteralPath $apk | Select-Object FullName,Length
    Get-FileHash -LiteralPath $apk -Algorithm SHA256
} finally {
    $env:JAVA_HOME = $oldJava
    if($Isolated -and (Test-Path -LiteralPath $isolatedStore)){Remove-Item -LiteralPath $isolatedStore}
}
