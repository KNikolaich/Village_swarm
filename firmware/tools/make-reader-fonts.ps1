<#
.SYNOPSIS
  Makes fonts for the book reader (CrowPanel 3.7") from any TTF files and writes firmware/lib/reader-fonts/ReaderFonts.h.

.DESCRIPTION
  U8g2 has few Cyrillic fonts and none that is proportional at 18 or 20 pixels, so the reader can take fonts made from TTF files.
  Which fonts and sizes is in firmware/tools/reader-fonts.json (edit it, run this script again):

    { "fonts": [ { "id": "literata", "name": "Literata", "ttf": "fonts/Literata-VF.ttf", "axes": { "weight": 400, "optical": "size" }, "sizes": [14, 16, 18, 20] },
                 { "id": "serif",  "name": "Times", "ttf": "C:/Windows/Fonts/times.ttf", "sizes": [16, 18, 22] } ] }

    id     letters, digits, underscore (names the C arrays)
    name   what the reader's settings show after "Font:" (any text, Cyrillic is fine; keep it short)
    ttf    path to a TTF/OTF, relative to the json file or absolute; it must have Cyrillic (and the licence must allow embedding)
    axes   only for a variable font (Literata, Roboto Mono): "weight" 400 is regular, "optical": "size" sets the optical-size axis to the pixel size
    sizes  pixel sizes of the em square (up to 8 per font, 8..48)

  Pipeline:  TTF --(ttf2bdf.py, Pillow/FreeType, 1-bit)--> BDF --(bdfconv from u8g2)--> C arrays --> ReaderFonts.h
  Characters made: U+0020-007E, U+0401 (Yo), U+0410-044F (A-ya), U+0451 (yo): what the reader draws (Reader.h printable()).

  Needs on this PC:
    1. Python 3 with Pillow:      python -m pip install pillow
    2. bdfconv.exe (u8g2's own converter). Looked for in this order: -BdfconvPath, firmware/tools/.cache/bdfconv.exe, anything named
       bdfconv*.exe under %USERPROFILE%\.platformio, else DOWNLOADED (prebuilt, from the u8g2 repository:
       https://github.com/olikraus/u8g2/raw/master/tools/font/bdfconv/bdfconv.exe) into firmware/tools/.cache with its SHA-256 printed.
       No download wanted: -NoDownload and -BdfconvPath, or build it: get https://github.com/olikraus/u8g2 and in tools/font/bdfconv
       run  gcc -O2 -o bdfconv.exe *.c  (WinLibs gcc is enough).

  Afterwards the script prints the flash each font takes and the total, builds monitor-crowpanel37 (skip with -SkipBuild) and warns
  when the flash of the build is over 80 percent. Then flash as usual; the reader's settings list every font of the json after
  the built-in ones. Commit lib/reader-fonts/ReaderFonts.h so every build has them. Without that file the firmware builds as before.

  Flash cost: about 4-8 KB per size for ~160 characters (ASCII and Cyrillic), more for wide fonts or big sizes: the default json (11 sizes of
  three fonts) is about 35-45 KB, about 1 percent of the 3.3 MB app slot. The exact numbers are printed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File firmware\tools\make-reader-fonts.ps1
#>
[CmdletBinding()]
param(
    [string]$Config,
    [string]$BdfconvPath,
    [switch]$NoDownload,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$firmware = Join-Path $repo 'firmware'
if (-not $Config) { $Config = Join-Path $here 'reader-fonts.json' }
$outHeader = Join-Path $firmware 'lib\reader-fonts\ReaderFonts.h'
$work = Join-Path $here '.cache'
$map = '32-126,1025,1040-1103,1105'   # U+0020-007E, U+0401 (Yo), U+0410-044F (A-ya), U+0451 (yo)
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Fail($text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- the config
if (-not (Test-Path $Config)) { Fail "Config not found: $Config" }
$configDir = Split-Path -Parent (Resolve-Path $Config).Path
try {
    $json = Get-Content -Raw -Encoding UTF8 $Config | ConvertFrom-Json
} catch {
    Fail "Cannot read $Config as JSON: $($_.Exception.Message)"
}
$fonts = @($json.fonts)
if ($fonts.Count -eq 0) { Fail "No fonts in $Config (the list `"fonts`" is empty)." }
if ($fonts.Count -gt 6) { Fail "Too many fonts ($($fonts.Count)); the reader's list takes up to 6." }
$seen = @{}
foreach ($f in $fonts) {
    if ($f.id -notmatch '^[a-z][a-z0-9_]{0,15}$') { Fail "Font id '$($f.id)': lower-case letters, digits, underscore, up to 16, starting with a letter." }
    if ($seen.ContainsKey($f.id)) { Fail "Font id '$($f.id)' appears twice." }
    $seen[$f.id] = $true
    if (-not $f.name -or ([string]$f.name).Length -gt 24) { Fail "Font '$($f.id)': name is needed (up to 24 characters)." }
    $sizes = @($f.sizes)
    if ($sizes.Count -eq 0 -or $sizes.Count -gt 8) { Fail "Font '$($f.id)': 1 to 8 sizes." }
    foreach ($s in $sizes) { if ($s -isnot [int] -or $s -lt 8 -or $s -gt 48) { Fail "Font '$($f.id)': size '$s' must be a whole number from 8 to 48." } }
    $path = [string]$f.ttf
    if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $configDir $path }
    if (-not (Test-Path $path)) { Fail "Font '$($f.id)': TTF not found: $path" }
    $f | Add-Member -NotePropertyName ttfPath -NotePropertyValue (Resolve-Path $path).Path -Force
}

# ---------------------------------------------------------------- Python and Pillow
$python = $null
foreach ($candidate in @('python', 'py')) {
    if (Get-Command $candidate -ErrorAction SilentlyContinue) { $python = $candidate; break }
}
if (-not $python) { Fail "Python 3 not found. Install it (winget install Python.Python.3.12) and run again." }
& $python -c "import PIL" 2>$null
if ($LASTEXITCODE -ne 0) { Fail "Pillow is missing. Run:  $python -m pip install pillow   and run this script again." }

# ---------------------------------------------------------------- bdfconv
function Find-Bdfconv {
    if ($BdfconvPath) {
        if (-not (Test-Path $BdfconvPath)) { Fail "bdfconv not found at -BdfconvPath $BdfconvPath" }
        return (Resolve-Path $BdfconvPath).Path
    }
    $cached = Join-Path $work 'bdfconv.exe'
    if (Test-Path $cached) { return $cached }
    $pio = Join-Path $env:USERPROFILE '.platformio'
    if (Test-Path $pio) {
        $found = Get-ChildItem -Path $pio -Recurse -Filter 'bdfconv*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$bdfconv = Find-Bdfconv
if (-not $bdfconv) {
    if ($NoDownload) { Fail "bdfconv.exe not found. Give -BdfconvPath, or see the help (Get-Help $PSCommandPath) for where to get it." }
    $url = 'https://github.com/olikraus/u8g2/raw/master/tools/font/bdfconv/bdfconv.exe'
    Write-Host "bdfconv.exe not found: downloading the prebuilt one from the u8g2 repository:`n  $url"
    $bdfconv = Join-Path $work 'bdfconv.exe'
    try {
        Invoke-WebRequest -Uri $url -OutFile $bdfconv -UseBasicParsing
    } catch {
        Fail "Download failed ($($_.Exception.Message)). Download bdfconv.exe by hand into $work or build it (see Get-Help $PSCommandPath)."
    }
    Write-Host ("  saved to {0}, SHA-256 {1}" -f $bdfconv, (Get-FileHash $bdfconv -Algorithm SHA256).Hash)
}
Write-Host "bdfconv: $bdfconv"

# ---------------------------------------------------------------- TTF -> BDF -> C
$arrays = @()
$families = @()
$costs = @()
foreach ($f in $fonts) {
    $entries = @()
    foreach ($size in @($f.sizes)) {
        $name = "u8g2_font_reader_$($f.id)_${size}_cyr"
        $bdf = Join-Path $work "$($f.id)-$size.bdf"
        $c = Join-Path $work "$($f.id)-$size.c"
        Remove-Item -ErrorAction SilentlyContinue $c

        Write-Host "`n== $($f.name) $size : TTF -> BDF"
        $axesArg = @()
        if ($f.axes) {
            $pairs = @()
            foreach ($p in $f.axes.PSObject.Properties) { $pairs += ("{0}={1}" -f $p.Name, $p.Value) }
            $axesArg = @('--axes', ($pairs -join ','))
        }
        & $python (Join-Path $here 'ttf2bdf.py') $f.ttfPath $size $bdf --ranges $map --name $f.id @axesArg
        if ($LASTEXITCODE -ne 0) { Fail "ttf2bdf.py failed for $($f.id) $size" }

        Write-Host "== $($f.name) $size : BDF -> U8g2 font ($name)"
        & $bdfconv -v -f 1 -m $map -n $name -o $c $bdf
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $c)) {
            Fail "bdfconv failed for $($f.id) $size (exit $LASTEXITCODE). Try it by hand:  $bdfconv -v -f 1 -m `"$map`" -n $name -o $c $bdf"
        }
        $text = [IO.File]::ReadAllText($c)
        $m = [regex]::Match($text, [regex]::Escape($name) + '\s*\[(\d+)\]')
        if (-not $m.Success) { Fail "bdfconv's output has no array named $name (see $c)." }
        $costs += [pscustomobject]@{ Font = $f.name; Size = $size; Bytes = [int]$m.Groups[1].Value }
        # the generated file includes u8g2.h; the reader already has the font types, so only the array stays
        $arrays += (($text -split "`r?`n" | Where-Object { $_ -notmatch '^\s*#\s*include' }) -join "`n")
        $entries += ('{"' + $size + '", ' + $name + '}')
    }
    $label = ([string]$f.name) -replace '\\', '\\' -replace '"', '\"'
    $families += ('    {"' + $label + '", {' + ($entries -join ', ') + '}, ' + $entries.Count + '}')
}

# ---------------------------------------------------------------- the header
New-Item -ItemType Directory -Force -Path (Split-Path $outHeader) | Out-Null
$header = @"
#pragma once
// GENERATED by firmware/tools/make-reader-fonts.ps1 from firmware/tools/reader-fonts.json. Do not edit by hand: run the script again.
// The book reader (reader_crowpanel37.cpp) offers every family below in its settings, after the built-in ones.
#include <stdint.h>

#ifndef U8G2_FONT_SECTION
#define U8G2_FONT_SECTION(name)
#endif

$($arrays -join "`n`n")

// The families for the reader's table: name, sizes (label, font), how many.
#define READER_GENERATED_FAMILIES \
$($families -join ", \`n"),
"@
[IO.File]::WriteAllText($outHeader, ($header -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))

# ---------------------------------------------------------------- what it costs
Write-Host "`nFlash taken by the fonts (bytes of the arrays):" -ForegroundColor Cyan
$costs | ForEach-Object { Write-Host ("  {0,-24} {1,3} px  {2,7}" -f $_.Font, $_.Size, $_.Bytes) }
$total = ($costs | Measure-Object -Property Bytes -Sum).Sum
Write-Host ("  total {0} bytes ({1:N1} KB)" -f $total, ($total / 1024)) -ForegroundColor Cyan
Write-Host "`nWritten: $outHeader" -ForegroundColor Green

# ---------------------------------------------------------------- the build: how full the flash is
if (-not $SkipBuild) {
    $pio = $null
    if (Get-Command pio -ErrorAction SilentlyContinue) { $pio = 'pio' }
    elseif (Test-Path (Join-Path $env:USERPROFILE '.platformio\penv\Scripts\pio.exe')) { $pio = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\pio.exe' }
    if (-not $pio) {
        Write-Host "pio not found: skipping the build check (run  pio run -e monitor-crowpanel37  yourself and read its 'Flash:' line)." -ForegroundColor Yellow
    } else {
        Write-Host "`nBuilding monitor-crowpanel37 to see how full the flash is (-SkipBuild skips this)..."
        Push-Location $firmware
        try { $log = & $pio run -e monitor-crowpanel37 2>&1 | Out-String } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { Write-Host $log; Fail "The build failed: the output is above." }
        $flash = [regex]::Match($log, 'Flash:.*?([\d.]+)%\s*\(used (\d+) bytes from (\d+) bytes\)')
        if ($flash.Success) {
            $percent = [double]::Parse($flash.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
            Write-Host ("Flash of the app slot: {0}% ({1} of {2} bytes used)" -f $percent, $flash.Groups[2].Value, $flash.Groups[3].Value)
            if ($percent -gt 80) { Write-Host "WARNING: over 80 percent. Drop a size or a font from the json and run again." -ForegroundColor Yellow }
        } else {
            Write-Host "Built, but the 'Flash:' line was not found in the output." -ForegroundColor Yellow
        }
    }
}

Write-Host @"

Next:
  cd firmware
  pio run -e monitor-crowpanel37 -t upload      (pio is at %USERPROFILE%\.platformio\penv\Scripts\pio.exe)
  then in a book: press the rocker -> "Font" -> your font -> "Size"
  git add firmware/lib/reader-fonts/ReaderFonts.h firmware/tools/reader-fonts.json   (so every build has the fonts)
"@
