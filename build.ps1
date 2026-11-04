param([switch]$Test, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$temporary=Join-Path $root 'build\tmp'; New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '未找到系统 .NET Framework C# 编译器。请安装 .NET Framework 4.8。' }
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$previous = Join-Path $root 'dist/飞Q.exe'
$candidate = Join-Path $temporary ('windows-candidate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $candidate | Out-Null
$testOut = Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $testOut | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$common = @('/nologo','/utf8output','/optimize+','/warn:4','/warnaserror+','/platform:anycpu','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll')
& $compiler @common '/target:exe' "/out:$testOut\MakeIcon.exe" "$root\src\Theme.cs" "$root\src\Motion.cs" "$root\src\EmojiAvatars.cs" "$root\tools\MakeIcon.cs"
if ($LASTEXITCODE -ne 0) { throw '图标生成工具编译失败' }
& "$testOut\MakeIcon.exe" "$testOut\app.ico"
if ($LASTEXITCODE -ne 0) { throw '图标生成失败' }
$common += @("/resource:$root\src\emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$root\src\emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$root\docs\Twemoji-LICENSE-GRAPHICS.txt,FeiqLight.Twemoji-LICENSE-GRAPHICS.txt")
$common += @("/resource:$root\assets\avatars\atlas.png,FeiqLight.Avatars.png","/resource:$root\assets\avatars\catalog.txt,FeiqLight.Avatars.txt")
foreach ($asset in @('index.html','app.css','app.js')) { $common += "/resource:$root\assets\tools\$asset,FeiqLight.Tools.$asset" }
& $compiler @common '/target:winexe' "/win32icon:$testOut\app.ico" "/win32manifest:$root\src\app.manifest" "/out:$candidate\飞Q.exe" @sources
if ($LASTEXITCODE -ne 0) { throw '应用编译失败' }
# 先验证旧成品写出的目录、开关和配对，再替换交付文件。
if (Test-Path -LiteralPath $previous) {
    & "$root/tests/upgrade-settings.ps1" -ExecutablePath "$candidate/飞Q.exe" -PreviousExecutablePath $previous
} else { Write-Warning '未找到 dist 中的旧 EXE，本次无法验证跨版本配置保留。' }
if ($Test) {
    $testOut = Join-Path $root 'build'
    New-Item -ItemType Directory -Force -Path $testOut | Out-Null
    & $compiler @common '/target:exe' '/main:FeiqLight.Tests.SelfTests' "/win32manifest:$root\src\app.manifest" "/out:$testOut\FeiqLight.Tests.exe" @sources "$root\tests\SelfTests.cs" "$root\tests\UiSmokeTests.cs"
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
    & "$testOut\FeiqLight.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw '测试未通过' }
    & "$root\tests\shared-remote.ps1"
}
Copy-Item -LiteralPath "$candidate/飞Q.exe" -Destination "$out/飞Q.exe" -Force
Remove-Item -LiteralPath "$candidate/飞Q.exe"
Get-Item -LiteralPath "$out\飞Q.exe" | Select-Object FullName,Length
