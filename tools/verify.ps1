#requires -Version 7.0
param([ValidateSet('Fast','Full','Release')][string]$Mode='Fast')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$id=[Guid]::NewGuid().ToString('N').Substring(0,6)
$tmpBase=[IO.Path]::GetFullPath("$root/build/tmp")
$work=Join-Path $tmpBase "v$id"
$android=Join-Path $tmpBase "a$id"
$packages=Join-Path $tmpBase "packages-$id"
$report=Join-Path $root ('build/verify/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+"-$Mode-$id")
New-Item -ItemType Directory -Path $work,$report | Out-Null
$owned=@($work)
$env:TEMP=$work;$env:TMP=$work;$env:TMPDIR=$work;$env:JAVA_TOOL_OPTIONS='-Djava.io.tmpdir="'+$work+'"'
$results=[Collections.Generic.List[object]]::new()
$pwsh=(Get-Process -Id $PID).Path
$desktop=$IsWindows -and [Environment]::UserInteractive -and (Get-Process -Id $PID).SessionId -ne 0
$compiler=if($IsWindows){Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'}else{''}
$jdk=if($env:JAVA_HOME){$env:JAVA_HOME}else{'C:/Program Files/Java/jdk-17'}
$noCompiler=if(!$compiler -or !(Test-Path -LiteralPath $compiler)){'缺少 Windows .NET Framework 编译器'}
$noJava=if(!(Test-Path -LiteralPath "$jdk/bin/javac.exe")){'缺少 JDK 17 或更高版本'}
$noDesktop=if(!$desktop){'缺少交互式 Windows 桌面'}

function Add-Result($name,$state,$detail,$log,$command){
    $results.Add([pscustomobject]@{check=$name;status=$state;detail=$detail;log=$log;command=$command})
    Write-Host "$state $name $detail"
}
function Invoke-Check($name,$program,[string[]]$arguments,$blocked){
    $log=Join-Path $report "$name.log"
    $command=@($program)+$arguments
    if($blocked){Add-Result $name 'BLOCKED' $blocked $null $command;return}
    try {
        & $program @arguments 2>&1 | Tee-Object -FilePath $log | Out-Host
        $code=$LASTEXITCODE
        if($code -eq 0){Add-Result $name 'PASS' '' $log $command}
        elseif(Select-String -LiteralPath $log -Pattern 'BLOCKED:' -Quiet){Add-Result $name 'BLOCKED' "退出码 $code，见日志中的 BLOCKED 原因" $log $command}
        else{Add-Result $name 'FAIL' "退出码 $code" $log $command}
    }catch{Add-Result $name 'FAIL' $_.Exception.Message $log $command}
}
function Invoke-Script($name,$file,[string[]]$options,$blocked){
    Invoke-Check $name $pwsh (@('-NoProfile','-File',"$root/$file")+$options) $blocked
}

Push-Location -LiteralPath $root
try {
    $options=@('-TemporaryDirectory',"$work/p",'-OutputDirectory',"$report/protocol")
    if($Mode -eq 'Fast'){$options+='-VectorsOnly'}
    Invoke-Script 'protocol' 'tests/protocol-guest.ps1' $options ($noCompiler ?? $(if($Mode -ne 'Fast'){$noDesktop}))
    Invoke-Script 'interop-tls' 'tests/android-interop.ps1' @('-TemporaryDirectory',"$work/i",'-OutputDirectory',"$work/i") ($noCompiler ?? $noJava)
    if($Mode -ne 'Fast'){
        New-Item -ItemType Directory -Path $packages,$android | Out-Null
        $owned+=$android
        Invoke-Script 'windows-build' 'build.ps1' @('-Test','-TemporaryDirectory',"$work/w",'-OutputDirectory',$packages) ($noCompiler ?? $noDesktop)
        $noAndroid=$noJava
        if(!$noAndroid -and !$env:ANDROID_HOME -and !(Test-Path -LiteralPath "$root/android/local.properties")){$noAndroid='未配置 Android SDK'}
        if(!$noAndroid -and !(Test-Path -LiteralPath "$root/build/maven-repo")){$noAndroid='缺少本地 Robolectric 依赖缓存'}
        Invoke-Script 'android-build' 'build-android.ps1' @('-Test','-Isolated','-Offline','-TemporaryDirectory',$android,'-OutputDirectory',$packages,'-TestOutputDirectory',"$report/android") $noAndroid
        if(Test-Path -LiteralPath "$android/b/test-results/testDebugUnitTest"){Copy-Item -LiteralPath "$android/b/test-results/testDebugUnitTest" -Destination "$report/junit" -Recurse}
        if(Test-Path -LiteralPath "$android/b/reports"){Copy-Item -LiteralPath "$android/b/reports" -Destination "$report/android-reports" -Recurse}
        Invoke-Script 'security-contracts' 'tests/security-audit.ps1' @('-ContractsOnly','-TemporaryDirectory',"$work/security",'-OutputDirectory',"$work/security/data") ($noCompiler ?? $noJava)
        foreach($entry in @('partial-interop','reverse-interop')){Invoke-Script $entry "tests/$entry.ps1" @('-TemporaryDirectory',"$work/$entry") ($noCompiler ?? $noJava)}
        Invoke-Script 'secure-upgrade' 'tests/secure-upgrade.ps1' @('-TemporaryDirectory',"$work/su") ($noCompiler ?? $noJava)
        foreach($entry in @('connection-status','transfer-features')){Invoke-Script $entry "tests/$entry.ps1" @('-TemporaryDirectory',"$work/$entry",'-OutputDirectory',"$report/$entry") ($noCompiler ?? $noDesktop)}
        Invoke-Script 'commit-audit' 'tests/commit-audit.ps1' @('-TemporaryDirectory',"$work/ca") ($noCompiler ?? $noDesktop)
    }
    if($Mode -eq 'Release'){
        if(!(Test-Path -LiteralPath "$root/dist/飞Q.exe") -or !@(Get-ChildItem -LiteralPath "$root/dist" -Filter '*.apk' -File -ErrorAction SilentlyContinue).Count){
            Add-Result 'upgrade-baseline' 'BLOCKED' '缺少旧交付包，不能确认覆盖升级' $null
        }
        $exe=Join-Path $packages '飞Q.exe';$apk=@(Get-ChildItem -LiteralPath $packages -Filter '*.apk' -File)
        if(!(Test-Path -LiteralPath $exe) -or $apk.Count -ne 1){Add-Result 'candidate-digests' 'BLOCKED' '未取得本轮双端候选包' $null}
        else{
            $manifest=Join-Path $report 'sha256.txt'
            Get-FileHash -LiteralPath $exe,$apk[0].FullName | ForEach-Object {"$($_.Hash)  $([IO.Path]::GetFileName($_.Path))"} | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
            Add-Result 'candidate-digests' 'PASS' '仅核对候选包，不发布' $manifest
        }
        $env:FQ_GUEST_TEST_EXE="$work/transfer-features/TransferFeatureTests.exe";$env:FQ_BROWSER_TEMP="$work/browser";$env:FQ_VALIDATION_DIR="$report/browser"
        $browserReason=$null
        if(!(Test-Path -LiteralPath $env:FQ_GUEST_TEST_EXE)){$browserReason='缺少本轮访客测试入口'}
        elseif(!(Get-Command node -ErrorAction SilentlyContinue)){$browserReason='缺少 Node.js'}
        else{
            $browserPath=& node -p "process.env.EDGE_PATH || require('playwright').chromium.executablePath()" 2>$null
            if($LASTEXITCODE -ne 0 -or !$browserPath -or !(Test-Path -LiteralPath $browserPath)){$browserReason='缺少已有 Playwright 或 Chromium/Edge；未下载浏览器'}
        }
        Invoke-Check 'guest-browser' 'node' @("$root/tests/guest-folder-browser.cjs") $browserReason
    }
} catch {
    Add-Result 'verification-runner' 'FAIL' $_.Exception.Message $null
} finally {
    Pop-Location
    $summary=[ordered]@{mode=$Mode;results=@($results);candidateDirectory=if($Mode -ne 'Fast'){$packages};manual=@('独立主动安全审计（未计入协议契约结果）','Android 物理设备验收')}
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$report/summary.json" -Encoding utf8NoBOM
    Write-Host "验证汇总：$report/summary.json"
    foreach($directory in $owned){
        $actual=(Get-Item -LiteralPath $directory).FullName
        if([IO.Path]::GetDirectoryName($actual) -ne $tmpBase -or [IO.Path]::GetFileName($actual) -notin @("v$id","a$id") -or ((Get-Item -LiteralPath $actual).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "拒绝清理意外目录：$actual"}
        Write-Host "清理本次创建且已核对的临时目录：$actual";Remove-Item -LiteralPath $actual -Recurse -Force
    }
}
if($results.status -contains 'FAIL'){exit 1};if($results.status -contains 'BLOCKED'){exit 2};exit 0
