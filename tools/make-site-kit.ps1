# Builds site-kit\ (and dist\AION2DpsMeter-site-kit.zip): everything a download page needs.
#   docs\site\*            the page brief for Claude Code and a static preview page (copied as they are)
#   images\                screenshots in English and Russian, a transparent overlay, icon, installer art, social preview
#   video\                 the overlay through a scripted boss fight: MP4 (web), GIF (chats, READMEs), poster frame
# Everything is drawn offscreen by the app itself (--render-sample): no window opens, the game is not touched, and the
# party is made up (no real character names). Needs ffmpeg for the video (found on PATH or passed with -Ffmpeg).
param([string]$Ffmpeg = "")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$kit = Join-Path $root "site-kit"
$images = Join-Path $kit "images"
$video = Join-Path $kit "video"
$work = Join-Path ([IO.Path]::GetTempPath()) "aion2dpsmeter-site-kit"
Add-Type -AssemblyName System.Drawing, System.IO.Compression, System.IO.Compression.FileSystem

dotnet build (Join-Path $root "src\AionMeter.App\AionMeter.App.csproj") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = Join-Path $root "src\AionMeter.App\bin\Release\net10.0-windows\AION2DpsMeter.exe"

foreach ($d in $kit, $work) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force $images, $video, $work | Out-Null

function Render([string]$out, [string[]]$arguments) {
    $p = Start-Process $exe -ArgumentList (@('--render-sample', "`"$out`"") + $arguments) -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0 -or -not (Test-Path $out)) { throw "render failed: $out" }
}

# Bottom strip of an image (rendered at 1.5x): the overlay's update banner and footer.
function Crop-Bottom([string]$from, [string]$to, [int]$height, [int]$skip) {
    $img = [System.Drawing.Image]::FromFile($from)
    try {
        $rect = New-Object System.Drawing.Rectangle 0, ($img.Height - $height - $skip), $img.Width, $height
        $part = ([System.Drawing.Bitmap]$img).Clone($rect, $img.PixelFormat)
        $part.Save($to, [System.Drawing.Imaging.ImageFormat]::Png)
        $part.Dispose()
    }
    finally { $img.Dispose() }
}

# ---------------------------------------------------------------- screenshots
$overlaySize = @('--width', '520', '--height', '420')
foreach ($lang in 'en', 'ru') {
    Render (Join-Path $images "overlay-$lang.png") (@('--lang', $lang, '--backdrop', 'dark') + $overlaySize)
    foreach ($tab in 'dps', 'accuracy', 'rotation') {
        Render (Join-Path $images "breakdown-$tab-$lang.png") @('--lang', $lang, '--window', 'breakdown', '--tab', $tab)
    }
    Render (Join-Path $images "boss-timers-$lang.png") @('--lang', $lang, '--window', 'timers')
    Render (Join-Path $images "update-window-$lang.png") @('--lang', $lang, '--window', 'update')
    $withBanner = Join-Path $work "overlay-update-$lang.png"
    Render $withBanner (@('--lang', $lang, '--window', 'overlay-update', '--backdrop', 'dark') + $overlaySize)
    Crop-Bottom $withBanner (Join-Path $images "update-banner-$lang.png") 150 20
}
Render (Join-Path $images "overlay-en-transparent.png") (@('--lang', 'en', '--backdrop', 'none') + $overlaySize)

# App icon (the 256 px frame of app.ico is a PNG) and the installer's side art.
$ico = [IO.File]::ReadAllBytes((Join-Path $root "src\AionMeter.App\Assets\app.ico"))
for ($i = 0; $i -lt [BitConverter]::ToUInt16($ico, 4); $i++) {
    $e = 6 + 16 * $i
    if ($ico[$e] -eq 0) {
        $size = [BitConverter]::ToUInt32($ico, $e + 8); $offset = [BitConverter]::ToUInt32($ico, $e + 12)
        [IO.File]::WriteAllBytes((Join-Path $images "icon-256.png"), $ico[$offset..($offset + $size - 1)])
    }
}
Copy-Item (Join-Path $root "installer\wizard.png") (Join-Path $images "installer-art.png")

# ---------------------------------------------------------------- social preview (1200x630)
$W = 1200; $H = 630
$og = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($og)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
$full = New-Object System.Drawing.Rectangle 0, 0, $W, $H
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $full, ([System.Drawing.Color]::FromArgb(255, 0x24, 0x31, 0x52)), ([System.Drawing.Color]::FromArgb(255, 0x07, 0x09, 0x10)), 35
$g.FillRectangle($bg, $full)
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(700, 40, 520, 560)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
$glow.CenterColor = [System.Drawing.Color]::FromArgb(70, 0xC9, 0xA4, 0x5C); $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0xC9, 0xA4, 0x5C))
$g.FillPath($glow, $glowPath)
$card = [System.Drawing.Image]::FromFile((Join-Path $images "overlay-en-transparent.png"))
$ch = 580; $cw = [int]($card.Width * $ch / $card.Height)
$g.DrawImage($card, $W - $cw - 60, [int](($H - $ch) / 2), $cw, $ch)
$card.Dispose()
$logo = [System.Drawing.Image]::FromFile((Join-Path $images "icon-256.png"))
$g.DrawImage($logo, 70, 120, 96, 96); $logo.Dispose()
$gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xEC, 0xCB, 0x82))
$text = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xF1, 0xE9, 0xD8))
$dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xA3, 0xAC, 0xBF))
$px = [System.Drawing.GraphicsUnit]::Pixel
$g.DrawString("AION2 DPS Meter", (New-Object System.Drawing.Font 'Palatino Linotype', 66, ([System.Drawing.FontStyle]::Bold), $px), $gold, 64, 238)
$g.DrawString("Live DPS overlay for AION 2", (New-Object System.Drawing.Font 'Segoe UI Semibold', 34, ([System.Drawing.FontStyle]::Regular), $px), $text, 70, 336)
$g.DrawString("Party DPS · boss HP · skill breakdown · boss timers", (New-Object System.Drawing.Font 'Segoe UI', 24, ([System.Drawing.FontStyle]::Regular), $px), $dim, 72, 392)
$g.DrawString("Free · Open source · Windows 10/11 · Global EU / NA", (New-Object System.Drawing.Font 'Segoe UI Semibold', 22, ([System.Drawing.FontStyle]::Regular), $px), $gold, 72, 470)
$g.Dispose()
$og.Save((Join-Path $images "social-preview.png"), [System.Drawing.Imaging.ImageFormat]::Png); $og.Dispose()

# ---------------------------------------------------------------- video
if (-not $Ffmpeg) {
    $Ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
    if (-not $Ffmpeg) {
        $Ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\CapCut\Apps\*\ffmpeg.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
foreach ($lang in 'en', 'ru') {
    $frames = Join-Path $work "frames-$lang"
    Render $frames (@('--lang', $lang, '--backdrop', 'dark', '--animate', '12', '--fps', '10') + $overlaySize)
    $last = Get-ChildItem $frames -Filter 'frame_*.png' | Sort-Object Name | Select-Object -Last 1
    Copy-Item $last.FullName (Join-Path $video "overlay-fight-$lang-poster.png")
    if (-not $Ffmpeg) { Write-Warning "ffmpeg not found: frames are in $frames"; continue }

    $encoders = & $Ffmpeg -hide_banner -encoders 2>$null | Out-String
    $codec = if ($encoders -match 'libx264') { @('-c:v', 'libx264', '-crf', '20', '-preset', 'slow') } else { @('-c:v', 'h264_mf', '-b:v', '3M') }
    $hold = "tpad=stop_mode=clone:stop_duration=2.5" # the result stays on screen before the loop restarts
    & $Ffmpeg -hide_banner -loglevel error -y -framerate 10 -i (Join-Path $frames 'frame_%03d.png') `
        -vf "$hold,format=yuv420p" @codec -movflags +faststart -an (Join-Path $video "overlay-fight-$lang.mp4")
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg (mp4) failed" }
    & $Ffmpeg -hide_banner -loglevel error -y -framerate 10 -i (Join-Path $frames 'frame_%03d.png') `
        -vf "$hold,fps=8,scale=420:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" `
        -loop 0 (Join-Path $video "overlay-fight-$lang.gif")
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg (gif) failed" }
}

# ---------------------------------------------------------------- README pictures, texts, zip
# The English ones also illustrate README.md on GitHub.
$readme = Join-Path $root "docs\images"
New-Item -ItemType Directory -Force $readme | Out-Null
@{ "images\icon-256.png" = "icon-256.png"; "video\overlay-fight-en.gif" = "overlay-fight.gif"; "images\overlay-en.png" = "overlay.png"
   "images\breakdown-dps-en.png" = "breakdown-dps.png"; "images\breakdown-accuracy-en.png" = "breakdown-accuracy.png"
   "images\breakdown-rotation-en.png" = "breakdown-rotation.png"; "images\boss-timers-en.png" = "boss-timers.png"
   "images\update-window-en.png" = "update-window.png"; "images\update-banner-en.png" = "update-banner.png" }.GetEnumerator() |
    ForEach-Object { if (Test-Path (Join-Path $kit $_.Key)) { Copy-Item (Join-Path $kit $_.Key) (Join-Path $readme $_.Value) -Force } }

Copy-Item (Join-Path $root "docs\site\*") $kit -Recurse
$zip = Join-Path $root "dist\AION2DpsMeter-site-kit.zip"
New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $kit -Recurse -File | ForEach-Object {
        $entry = "site-kit/" + $_.FullName.Substring($kit.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }
Remove-Item $work -Recurse -Force

Get-ChildItem $kit -Recurse -File | Select-Object @{ n = "File"; e = { $_.FullName.Substring($kit.Length + 1) } }, @{ n = "KB"; e = { [math]::Round($_.Length / 1KB) } }
"zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
