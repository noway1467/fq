$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'assets\avatars'
$temporary = Join-Path $root ('build\tmp\avatar-atlas-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
$atlas = $null; $graphics = $null
try {
    $catalog = @(Get-Content -LiteralPath (Join-Path $assets 'catalog.txt') -Encoding UTF8)
    # 每格四周留 4 px 透明边，缩放过滤时不会采样到相邻头像的颜色。
    $atlas = [Drawing.Bitmap]::new(480, [int]([Math]::Ceiling($catalog.Count / 6.0) * 80))
    $graphics = [Drawing.Graphics]::FromImage($atlas)
    $graphics.Clear([Drawing.Color]::Transparent)
    for ($i = 0; $i -lt $catalog.Count; $i++) {
        $emoji = $catalog[$i].Split('|')[0]
        $code = [char]::ConvertToUtf32($emoji, 0).ToString('x')
        $file = Join-Path $temporary ($code + '.png')
        Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/jdecked/twemoji/v16.0.1/assets/72x72/$code.png" -OutFile $file
        $image = [Drawing.Bitmap]::new($file)
        try { $graphics.DrawImageUnscaled($image, [int](($i % 6) * 80 + 4), [int]([Math]::Floor($i / 6.0) * 80 + 4)) }
        finally { $image.Dispose() }
    }
    $atlas.Save((Join-Path $assets 'atlas.png'), [Drawing.Imaging.ImageFormat]::Png)
    Copy-Item -LiteralPath (Join-Path $root 'docs\Twemoji-LICENSE-GRAPHICS.txt') -Destination (Join-Path $assets 'LICENSE.txt')
} finally {
    if ($graphics) { $graphics.Dispose() }; if ($atlas) { $atlas.Dispose() }
    $resolved = [IO.Path]::GetFullPath($temporary)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'build\tmp')).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理项目外路径' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
