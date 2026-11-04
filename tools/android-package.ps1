function Get-AndroidPackageInfo {
    param([string]$Apk, [string]$SdkRoot)
    $aapt = Join-Path $SdkRoot 'build-tools/35.0.0/aapt.exe'
    $signer = Join-Path $SdkRoot 'build-tools/35.0.0/apksigner.bat'
    # SDK 的 aapt 在 Windows 上不能可靠读取中文文件名，使用项目内 ASCII 检查副本。
    $temporary = Join-Path (Split-Path -Parent $PSScriptRoot) ('build/tmp/apk-check-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $copy = Join-Path $temporary 'package.apk'
    try {
        Copy-Item -LiteralPath $Apk -Destination $copy
        $badging = & $aapt dump badging $copy
        if ($LASTEXITCODE -ne 0) { throw "无法读取 APK 元数据：$Apk" }
        $package = @($badging | Where-Object { $_ -match '^package:' })
        if ($package.Count -ne 1 -or $package[0] -notmatch "name='([^']+)' versionCode='([0-9]+)' versionName='([^']+)'") { throw 'APK 包名或版本信息无效' }
        $name=$Matches[1]; $code=[long]$Matches[2]; $version=$Matches[3]
        $signature = & $signer verify --print-certs $copy
        if ($LASTEXITCODE -ne 0) { throw "APK 签名验证失败：$Apk" }
        $certificates = @($signature | ForEach-Object {
            if ($_ -match '^Signer #[0-9]+ certificate SHA-256 digest: ([0-9a-fA-F]+)$') { $Matches[1].ToLowerInvariant() }
        } | Sort-Object -Unique)
        if ($certificates.Count -eq 0) { throw '未找到 APK 签名证书' }
        [pscustomobject]@{ Package=$name; VersionCode=$code; VersionName=$version; Certificates=($certificates -join ',') }
    } finally {
        if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy }
        Remove-Item -LiteralPath $temporary
    }
}

function Assert-AndroidUpgrade {
    param($Previous, $Candidate)
    if ($Candidate.Package -ne 'org.feiqlight.android' -or ($Previous -and $Candidate.Package -cne $Previous.Package)) {
        throw 'Android 包名变更会成为另一应用，拒绝交付；必须保留原包名和应用数据。'
    }
    if (!$Candidate.Certificates) { throw 'Android 签名证书缺失，拒绝交付。' }
    if (!$Previous) { return }
    if ($Candidate.Certificates -cne $Previous.Certificates) {
        throw 'Android 签名与现有交付包不一致，拒绝交付；请恢复原签名，不要卸载旧应用。'
    }
    if ($Candidate.VersionCode -lt $Previous.VersionCode -or
        ($Candidate.VersionName -cne $Previous.VersionName -and $Candidate.VersionCode -le $Previous.VersionCode)) {
        throw 'Android 升级版本号无效，拒绝交付；更换版本名时必须递增 versionCode。'
    }
}
