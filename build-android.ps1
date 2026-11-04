param(
    [string]$SdkRoot = $env:ANDROID_HOME,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome,
    [string]$OutputDirectory,
    [switch]$Test,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$temporary=Join-Path $root 'build\tmp'; New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$env:GRADLE_USER_HOME=Join-Path $root 'build\gradle-home'
$env:ANDROID_USER_HOME=Join-Path $root 'build/android-home'
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$temporary+'"'
$android = Join-Path $root 'android'
if (!$JavaHome -and (Test-Path -LiteralPath 'C:\Program Files\Java\jdk-17')) { $JavaHome = 'C:\Program Files\Java\jdk-17' }
if (!$JavaHome -or !(Test-Path -LiteralPath (Join-Path $JavaHome 'bin\javac.exe'))) { throw '请使用 -JavaHome 指定 JDK 17 或更高版本。' }
if ($SdkRoot) {
    if (!(Test-Path -LiteralPath (Join-Path $SdkRoot 'platforms\android-36\android.jar'))) { throw 'SDK 缺少 Android 36 平台。' }
    $path = [IO.Path]::GetFullPath($SdkRoot).Replace('\','/').Replace(':','\:')
    [IO.File]::WriteAllText((Join-Path $android 'local.properties'),"sdk.dir=$path`n",[Text.UTF8Encoding]::new($false))
} elseif (!(Test-Path -LiteralPath (Join-Path $android 'local.properties'))) { throw '请使用 -SdkRoot 指定 Android SDK，需 platform 36 / build-tools 35.0.0。' }
if (!$SdkRoot) {
    $sdkLine = Get-Content -LiteralPath (Join-Path $android 'local.properties') | Where-Object { $_ -match '^sdk\.dir=' } | Select-Object -First 1
    if (!$sdkLine) { throw 'local.properties 缺少 sdk.dir' }
    $SdkRoot = $sdkLine.Substring(8).Replace('\:',':').Replace('\\','\')
}
. "$root/tools/android-package.ps1"
$previousApks = @(Get-ChildItem -LiteralPath (Join-Path $root 'dist') -Filter '*.apk' -ErrorAction SilentlyContinue)
if ($previousApks.Count -gt 0 -and !(Test-Path -LiteralPath (Join-Path $env:ANDROID_USER_HOME 'debug.keystore'))) {
    throw '原 Android 签名文件缺失，拒绝自动生成新签名。请恢复 build/android-home/debug.keystore，不要卸载旧应用。'
}
$gradle = if ($GradleHome) { Join-Path $GradleHome 'bin\gradle.bat' } else { Join-Path $android 'gradlew.bat' }
if (!(Test-Path -LiteralPath $gradle)) { throw '未找到 Gradle 入口。' }
$oldJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    $previousPackages = @($previousApks | ForEach-Object { Get-AndroidPackageInfo -Apk $_.FullName -SdkRoot $SdkRoot })
    $tasks = @('-p',$android,'--no-daemon','assemblePreview','lintPreview')
    if ($Test) { $tasks += @('testDebugUnitTest','lintDebug') }
    if ($Offline) { $tasks += '--offline' }
    & $gradle @tasks
    if ($LASTEXITCODE -ne 0) { throw 'Android 构建或验证失败，未更新交付 APK。' }
    $candidate = Get-AndroidPackageInfo -Apk "$android/app/build/outputs/apk/preview/app-preview.apk" -SdkRoot $SdkRoot
    Assert-AndroidUpgrade $null $candidate
    foreach ($previous in $previousPackages) { Assert-AndroidUpgrade $previous $candidate }
    if ($previousPackages.Count -gt 0) { Write-Output 'PASS Android 包名、签名及覆盖升级版本检查；无需卸载旧应用。' }
    else { Write-Warning '未找到 dist 中的旧 APK，本次无法验证签名升级兼容。' }
    $out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'dist' }; New-Item -ItemType Directory -Force -Path $out | Out-Null
    $metadata = Get-Content -LiteralPath "$android\app\build\outputs\apk\preview\output-metadata.json" -Raw | ConvertFrom-Json
    $apk = Join-Path $out ("飞Q-android-" + $metadata.elements[0].versionName + '.apk')
    Copy-Item -LiteralPath "$android\app\build\outputs\apk\preview\app-preview.apk" -Destination $apk -Force
    Get-Item -LiteralPath $apk | Select-Object FullName,Length
    Get-FileHash -LiteralPath $apk -Algorithm SHA256
} finally { $env:JAVA_HOME = $oldJava }
