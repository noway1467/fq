param([string]$TemporaryDirectory,[string]$JavaHome=$env:JAVA_HOME)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temporary=if($TemporaryDirectory){[IO.Path]::GetFullPath($TemporaryDirectory)}else{Join-Path $root 'build/tmp/store-zip-interop'}
$prefix=[IO.Path]::GetFullPath((Join-Path $root 'build/tmp')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(!$temporary.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
$artifacts=Join-Path $temporary 'archives'
$classes=Join-Path $temporary 'classes'
New-Item -ItemType Directory -Force -Path $temporary,$artifacts,$classes | Out-Null
$env:TEMP=$temporary;$env:TMP=$temporary;$env:TMPDIR=$temporary
$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$temporary+'" -Dfile.encoding=UTF-8'
if(!$JavaHome){$JavaHome='C:/Program Files/Java/jdk-17'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@(Get-ChildItem -LiteralPath "$root/src" -Filter '*.cs' -File | ForEach-Object FullName)
$arguments=@('/nologo','/utf8output','/warn:4','/warnaserror+','/target:exe','/main:FeiqLight.Tests.StoreZipTests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll',"/win32manifest:$root\src\app.manifest","/out:$temporary\StoreZipTests.exe")
& $compiler @arguments @sources "$root\tests\StoreZipTests.cs"
if($LASTEXITCODE){throw 'Windows ZIP 测试编译失败'}
& "$temporary/StoreZipTests.exe" generate $artifacts
if($LASTEXITCODE){throw 'Windows ZIP 编码或 .NET 解压失败'}
& "$JavaHome/bin/javac.exe" -encoding UTF-8 -Xlint:all -d $classes "$root/android/app/src/main/java/org/feiqlight/android/core/Protocol.java" "$root/android/app/src/main/java/org/feiqlight/android/core/StoreZipWriter.java" "$root/tests/StoreZipInterop.java"
if($LASTEXITCODE){throw 'JDK ZIP 互通入口编译失败'}
& "$JavaHome/bin/java.exe" -cp $classes StoreZipInterop "$artifacts/windows-store.zip" "$artifacts/windows-store.tsv" $artifacts
if($LASTEXITCODE){throw 'JDK ZipFile / ZipInputStream 解压失败'}
& "$temporary/StoreZipTests.exe" verify "$artifacts/android-store.zip" "$artifacts/android-store.tsv"
if($LASTEXITCODE){throw 'Android ZIP 的 .NET 解压失败'}

# Shell.Application 使用与资源管理器相同的压缩文件夹解压器；只解压本轮生成的测试包。
$shell=New-Object -ComObject Shell.Application
try {
    foreach($platform in @('windows','android')){
        $destination=Join-Path $temporary ("shell-"+$platform)
        if(Test-Path -LiteralPath $destination){throw "测试解压目录已经存在：$destination"}
        New-Item -ItemType Directory -Path $destination | Out-Null
        $archive=$shell.NameSpace((Join-Path $artifacts "$platform-store.zip"))
        $target=$shell.NameSpace($destination)
        if(!$archive -or !$target){throw 'Windows 资源管理器压缩文件夹不可用'}
        $target.CopyHere($archive.Items(),1556)
        $expected=@(Get-Content -LiteralPath "$artifacts/$platform-store.tsv" | ForEach-Object {
            $parts=$_.Split("`t")
            [pscustomobject]@{path=Join-Path $destination ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[0])).Replace('/',[IO.Path]::DirectorySeparatorChar));hash=$parts[1]}
        })
        $clock=[Diagnostics.Stopwatch]::StartNew()
        do {
            $complete=@($expected | Where-Object {!(Test-Path -LiteralPath $_.path -PathType Leaf)}).Count -eq 0
            if(!$complete){Start-Sleep -Milliseconds 100}
        }while(!$complete -and $clock.ElapsedMilliseconds -lt 10000)
        if(!$complete){throw "资源管理器解压未完成：$platform"}
        foreach($entry in $expected){if((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.hash){throw "资源管理器解压校验不符：$($entry.path)"}}
        Write-Output "PASS $platform ZIP -> Windows 资源管理器压缩文件夹：$($expected.Count) 项中文/特殊路径、SHA-256"
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($archive) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($target) | Out-Null
    }
}finally{[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null}
