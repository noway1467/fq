function Get-WindowsPackage {
    param([Parameter(Mandatory=$true)][string]$Directory)
    if(!(Test-Path -LiteralPath $Directory -PathType Container)){return $null}
    $packages=@(Get-ChildItem -LiteralPath $Directory -Filter '*.exe' -File | Where-Object {
        $_.Name -match '^FeiQ-\d+\.\d+\.\d+\.exe$' -or $_.Name -in @('飞Q.exe','FeiQ.exe')
    } | ForEach-Object {
        $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).ProductVersion
        if($version -notmatch '^\d+\.\d+\.\d+$'){throw ('无法识别 Windows 安装包版本：'+$_.FullName)}
        if($_.Name -like 'FeiQ-*' -and $_.Name -cne "FeiQ-$version.exe"){throw ('EXE 文件名与内部版本不一致：'+$_.FullName)}
        [pscustomobject]@{Path=$_.FullName;Version=$version;Order=[version]$version}
    } | Sort-Object Order -Descending)
    if($packages.Count -gt 1 -and $packages[0].Order -eq $packages[1].Order){throw '同一版本存在多个 Windows 安装包，请先归档重复文件'}
    if($packages.Count){return $packages[0]}
    return $null
}
