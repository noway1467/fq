param(
    [string]$SdkRoot = $env:ANDROID_HOME,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradleHome,
    [switch]$Test,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$temporary=Join-Path $root 'build\tmp'; New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$env:GRADLE_USER_HOME=Join-Path $root 'build\gradle-home'
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$temporary+'"'
$android = Join-Path $root 'android'
if (!$JavaHome -and (Test-Path -LiteralPath 'C:\Program Files\Java\jdk-17')) { $JavaHome = 'C:\Program Files\Java\jdk-17' }
if (!$JavaHome -or !(Test-Path -LiteralPath (Join-Path $JavaHome 'bin\javac.exe'))) { throw '请使用 -JavaHome 指定 JDK 17 或更高版本。' }
if ($SdkRoot) {
    if (!(Test-Path -LiteralPath (Join-Path $SdkRoot 'platforms\android-36\android.jar'))) { throw 'SDK 缺少 Android 36 平台。' }
    $path = [IO.Path]::GetFullPath($SdkRoot).Replace('\','/').Replace(':','\:')
    [IO.File]::WriteAllText((Join-Path $android 'local.properties'),"sdk.dir=$path`n",[Text.UTF8Encoding]::new($false))
} elseif (!(Test-Path -LiteralPath (Join-Path $android 'local.properties'))) { throw '请使用 -SdkRoot 指定 Android SDK，需 platform 36 / build-tools 35.0.0。' }
$gradle = if ($GradleHome) { Join-Path $GradleHome 'bin\gradle.bat' } else { Join-Path $android 'gradlew.bat' }
if (!(Test-Path -LiteralPath $gradle)) { throw '未找到 Gradle 入口。' }
$oldJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    $tasks = @('-p',$android,'--no-daemon','assemblePreview','lintPreview')
    if ($Test) { $tasks += @('testDebugUnitTest','lintDebug') }
    if ($Offline) { $tasks += '--offline' }
    & $gradle @tasks
    if ($LASTEXITCODE -ne 0) { throw 'Android 构建或验证失败，未更新交付 APK。' }
    $out = Join-Path $root 'dist'; New-Item -ItemType Directory -Force -Path $out | Out-Null
    $apk = Join-Path $out '飞Q-android-0.2.20-preview.apk'
    Copy-Item -LiteralPath "$android\app\build\outputs\apk\preview\app-preview.apk" -Destination $apk -Force
    Get-Item -LiteralPath $apk | Select-Object FullName,Length
    Get-FileHash -LiteralPath $apk -Algorithm SHA256
} finally { $env:JAVA_HOME = $oldJava }
