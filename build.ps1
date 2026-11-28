param([switch]$Test, [string]$OutputDirectory, [string]$TemporaryDirectory)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$temporary=if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $root 'build\tmp'}
if($TemporaryDirectory -and !$temporary.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TEMP=$temporary; $env:TMP=$temporary; $env:TMPDIR=$temporary
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '未找到系统 .NET Framework C# 编译器。请安装 .NET Framework 4.8。' }
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force -Path $out | Out-Null
. "$root/tools/windows-package.ps1"
$previousPackage=Get-WindowsPackage -Directory (Join-Path $root 'dist')
$previous=if($previousPackage){$previousPackage.Path}else{$null}
$candidate = Join-Path $temporary ('windows-candidate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $candidate | Out-Null
$testOut = if($TemporaryDirectory){Join-Path $temporary 'bin'}else{Join-Path $root 'build'}
New-Item -ItemType Directory -Force -Path $testOut | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$common = @('/nologo','/utf8output','/optimize+','/warn:4','/warnaserror+','/platform:anycpu','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll')
& $compiler @common '/target:exe' "/out:$testOut\MakeIcon.exe" "$root\src\Theme.cs" "$root\src\MessageText.cs" "$root\src\Motion.cs" "$root\src\EmojiAvatars.cs" "$root\tools\MakeIcon.cs"
if ($LASTEXITCODE -ne 0) { throw '图标生成工具编译失败' }
& "$testOut\MakeIcon.exe" "$testOut\app.ico"
if ($LASTEXITCODE -ne 0) { throw '图标生成失败' }
$common += @("/resource:$root\src\emoji-atlas.png,FeiqLight.EmojiAtlas.png","/resource:$root\src\emoji-atlas.txt,FeiqLight.EmojiAtlas.txt","/resource:$root\docs\Twemoji-LICENSE-GRAPHICS.txt,FeiqLight.Twemoji-LICENSE-GRAPHICS.txt")
$common += @("/resource:$root\assets\avatars\atlas.png,FeiqLight.Avatars.png","/resource:$root\assets\avatars\catalog.txt,FeiqLight.Avatars.txt")
foreach ($asset in @('index.html','app.css','app.js','guest.html','guest.css','guest.js','qrcodegen.js')) { $common += "/resource:$root\assets\tools\$asset,FeiqLight.Tools.$asset" }
& $compiler @common '/target:winexe' "/win32icon:$testOut\app.ico" "/win32manifest:$root\src\app.manifest" "/out:$candidate\飞Q.exe" @sources
if ($LASTEXITCODE -ne 0) { throw '应用编译失败' }
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo("$candidate/飞Q.exe").ProductVersion
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Windows 安装包版本格式无效'}
$fileName="FeiQ-$version.exe"
# 先验证旧成品写出的目录、开关和配对，再替换交付文件。
if ($previous) {
    $extra=@()
    if($TemporaryDirectory){
        $baseline=Join-Path $temporary 'previous.exe'
        Copy-Item -LiteralPath $previous -Destination $baseline
        $previous=$baseline
        $extra=@('-TemporaryDirectory',(Join-Path $temporary 'up'))
    }
    & pwsh -NoProfile -File "$root/tests/upgrade-settings.ps1" -ExecutablePath "$candidate/飞Q.exe" -PreviousExecutablePath $previous @extra
    if($LASTEXITCODE){throw 'Windows 配置升级验证失败'}
} else { Write-Warning '未找到 dist 中的旧 EXE，本次无法验证跨版本配置保留。' }
if ($Test) {
    New-Item -ItemType Directory -Force -Path $testOut | Out-Null
    & $compiler @common '/r:System.IO.Compression.dll' '/target:exe' '/main:FeiqLight.Tests.SelfTests' "/win32manifest:$root\src\app.manifest" "/out:$testOut\FeiqLight.Tests.exe" @sources "$root\tests\SelfTests.cs" "$root\tests\UiSmokeTests.cs" "$root\tests\ProtocolVectorTests.cs" "$root\tests\TlsFilePeer.cs" "$root\tests\StoreZipTests.cs"
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
    & "$testOut\FeiqLight.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw '测试未通过' }
    $extra=if($TemporaryDirectory){@('-TemporaryDirectory',(Join-Path $temporary 'shared'))}else{@()}
    & pwsh -NoProfile -File "$root/tests/shared-remote.ps1" @extra
    if($LASTEXITCODE){throw '共享与远控测试失败'}
}
$destination=Join-Path $out $fileName
if([IO.Path]::GetFullPath($out) -eq [IO.Path]::GetFullPath((Join-Path $root 'dist')) -and $previousPackage -and $previousPackage.Path -ne $destination){
    $archive=Join-Path $root ('build/archives/windows-'+$previousPackage.Version+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    if(!$previousPackage.Path.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'dist'))+'\',[StringComparison]::OrdinalIgnoreCase) -or !$archive.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'build'))+'\',[StringComparison]::OrdinalIgnoreCase)){throw '旧包归档路径越界'}
    New-Item -ItemType Directory -Path $archive | Out-Null
    Write-Output ('归档已核对的旧 Windows 安装包：'+$previousPackage.Path+' -> '+$archive)
    Move-Item -LiteralPath $previousPackage.Path -Destination $archive
}
Copy-Item -LiteralPath "$candidate/飞Q.exe" -Destination $destination -Force
Remove-Item -LiteralPath "$candidate/飞Q.exe"
Get-Item -LiteralPath (Join-Path $out $fileName) | Select-Object FullName,Length
