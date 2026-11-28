function Resolve-TestTemporaryDirectory([string]$Directory) {
    $root=Split-Path -Parent $PSScriptRoot
    $prefix=[IO.Path]::GetFullPath("$root/build/tmp")+[IO.Path]::DirectorySeparatorChar
    $resolved=[IO.Path]::GetFullPath($Directory)
    if(!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录必须位于项目 build/tmp 内'}
    return $resolved
}
