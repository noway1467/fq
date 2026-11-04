$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/android-package.ps1')
$previous = [pscustomobject]@{ Package='org.feiqlight.android'; VersionCode=39; VersionName='0.6.2'; Certificates='old-certificate' }
$passed=0
foreach ($case in @(
    @{ Name='同版本重建兼容'; Code=39; Version='0.6.2'; Cert='old-certificate'; Package='org.feiqlight.android'; Reject=$false },
    @{ Name='后续版本覆盖兼容'; Code=40; Version='0.6.3'; Cert='old-certificate'; Package='org.feiqlight.android'; Reject=$false },
    @{ Name='拒绝更换签名'; Code=40; Version='0.6.3'; Cert='new-certificate'; Package='org.feiqlight.android'; Reject=$true },
    @{ Name='拒绝包名变化'; Code=40; Version='0.6.3'; Cert='old-certificate'; Package='org.feiqlight.changed'; Reject=$true },
    @{ Name='拒绝版本回退'; Code=38; Version='0.6.1'; Cert='old-certificate'; Package='org.feiqlight.android'; Reject=$true },
    @{ Name='拒绝新版本忘记递增'; Code=39; Version='0.6.3'; Cert='old-certificate'; Package='org.feiqlight.android'; Reject=$true },
    @{ Name='拒绝无签名'; Code=40; Version='0.6.3'; Cert=''; Package='org.feiqlight.android'; Reject=$true }
)) {
    $candidate=[pscustomobject]@{ Package=$case.Package; VersionCode=$case.Code; VersionName=$case.Version; Certificates=$case.Cert }
    $rejected=$false
    try { Assert-AndroidUpgrade $previous $candidate } catch { $rejected=$true }
    if ($rejected -ne $case.Reject) { throw "FAIL $($case.Name)" }
    Write-Output "PASS $($case.Name)"; $passed++
}
Write-Output "RESULT $passed passed, 0 failed"
