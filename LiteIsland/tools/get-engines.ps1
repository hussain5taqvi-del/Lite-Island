# Runs only on the PC that builds LiteIsland (build.bat). Assembles release\engines and release\filee.
# Nothing here is needed on the user's PC: the finished release folder has everything inside.
param(
    [Parameter(Mandatory)][string]$Release,
    [switch]$Full
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$here = $PSScriptRoot
$cache = Join-Path $here '.cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$eng = Join-Path $Release 'engines'
New-Item -ItemType Directory -Force -Path $eng | Out-Null
$ua = @{ 'User-Agent' = 'LiteIsland-build' }

function Get-File($url, $name) {
    $p = Join-Path $cache $name
    if (-not (Test-Path $p) -or (Get-Item $p).Length -lt 1000) {
        Write-Host "  downloading $name ..."
        Invoke-WebRequest -Uri $url -OutFile $p -Headers $ua -UseBasicParsing
    }
    return $p
}

Write-Host '[1/4] yt-dlp'
Copy-Item (Get-File 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' 'yt-dlp.exe') (Join-Path $eng 'yt-dlp.exe') -Force

Write-Host '[2/4] deno (needed by yt-dlp for YouTube)'
$denoZip = Get-File 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' 'deno.zip'
$tmp = Join-Path $cache 'deno-x'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive -Path $denoZip -DestinationPath $tmp -Force
Copy-Item (Join-Path $tmp 'deno.exe') (Join-Path $eng 'deno.exe') -Force

Write-Host '[3/4] background-removal model'
try {
    Copy-Item (Get-File 'https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx' 'u2netp.onnx') (Join-Path $eng 'u2netp.onnx') -Force
} catch { Write-Host '  model skipped (the app will fetch it on first use)' }

Write-Host '[4/4] Filee converter (256 formats)'
$fil = Join-Path $Release 'filee'
if (Test-Path $fil) { Remove-Item $fil -Recurse -Force }
$rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/KnifeLemon/Filee/releases/latest' -Headers $ua
$asset = $rel.assets | Where-Object { $_.name -match 'win.*portable.*\.zip$' } | Select-Object -First 1
if (-not $asset) { $asset = $rel.assets | Where-Object { $_.name -match 'win.*\.zip$' } | Select-Object -First 1 }
if (-not $asset) { throw 'No Windows zip in the latest Filee release. Open README, section "Filee engine".' }
$zip = Get-File $asset.browser_download_url $asset.name
$x = Join-Path $cache 'filee-x'
if (Test-Path $x) { Remove-Item $x -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $x -Force
$root = (Get-ChildItem $x -Recurse -Filter 'filee-cli.exe' | Select-Object -First 1).Directory.FullName
New-Item -ItemType Directory -Force -Path $fil | Out-Null
Copy-Item (Join-Path $root '*') $fil -Recurse -Force
# Filee's tray app is not used, only its command line and its engines
Remove-Item (Join-Path $fil 'Filee.exe') -Force -ErrorAction SilentlyContinue

# Engines: ffmpeg, pandoc, ghostscript, 7zip, rhwp (+ the heavy ones only with -Full)
# Lean by default: pandoc (220 MB), ghostscript and rhwp are only bundled with -Full
$only = @('7zip', 'ffmpeg', 'vcruntime')
if ($Full) { $only += @('rhwp', 'pandoc', 'ghostscript') }
if ($Full) { $only += @('libreoffice', 'h2orestart', 'jre', 'calibre', 'tesseract') }
& (Join-Path $here 'filee\build\fetch-engines.ps1') -Destination (Join-Path $fil 'engines') -Only $only -RuntimeTo $fil

if (-not $Full) { Remove-Item (Join-Path $fil 'engines\rhwp') -Recurse -Force -ErrorAction SilentlyContinue } # HWP only in the full pack
# Trim what the command line converter never uses: ffplay, language folders, import libs
Get-ChildItem $fil -Recurse -Include 'ffplay.exe' -File -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($l in 'da','de','es','fr','nl','pt','pt-BR','zh-CN','zh-TW') { Remove-Item (Join-Path $fil $l) -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem $Release -Filter '*.lib' -File -ErrorAction SilentlyContinue | Remove-Item -Force

Copy-Item (Join-Path $here 'filee\LICENSE') (Join-Path $fil 'LICENSE.Filee.txt') -Force
Copy-Item (Join-Path $here 'filee\THIRD-PARTY-NOTICES.md') (Join-Path $fil 'THIRD-PARTY-NOTICES.md') -Force
Write-Host 'Engines ready.'
