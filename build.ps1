param([switch]$Test, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$temporary=Join-Path $root 'build\tmp'; New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '未找到系统 .NET Framework C# 编译器。请安装 .NET Framework 4.8。' }
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$testOut = Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $testOut | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$common = @('/nologo','/utf8output','/optimize+','/warn:4','/warnaserror+','/platform:anycpu','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll')
& $compiler @common '/target:exe' "/out:$testOut\MakeIcon.exe" "$root\src\Theme.cs" "$root\tools\MakeIcon.cs"
if ($LASTEXITCODE -ne 0) { throw '图标生成工具编译失败' }
& "$testOut\MakeIcon.exe" "$testOut\app.ico"
if ($LASTEXITCODE -ne 0) { throw '图标生成失败' }
$common += @("/resource:$root\src\emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$root\src\emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$root\docs\Twemoji-LICENSE-GRAPHICS.txt,FeiqLight.Twemoji-LICENSE-GRAPHICS.txt")
& $compiler @common '/target:winexe' "/win32icon:$testOut\app.ico" "/win32manifest:$root\src\app.manifest" "/out:$out\飞Q.exe" @sources
if ($LASTEXITCODE -ne 0) { throw '应用编译失败' }
if ($Test) {
    $testOut = Join-Path $root 'build'
    New-Item -ItemType Directory -Force -Path $testOut | Out-Null
    & $compiler @common '/target:exe' '/main:FeiqLight.Tests.SelfTests' "/win32manifest:$root\src\app.manifest" "/out:$testOut\FeiqLight.Tests.exe" @sources "$root\tests\SelfTests.cs" "$root\tests\UiSmokeTests.cs"
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
    & "$testOut\FeiqLight.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw '测试未通过' }
}
Get-Item -LiteralPath "$out\飞Q.exe" | Select-Object FullName,Length
