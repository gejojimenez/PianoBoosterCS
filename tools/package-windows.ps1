<#
.SYNOPSIS
    Packages PianoBooster (the .NET port) for Windows into a portable zip: what tools/package-windows.sh
    does for the C++ code (built with MSYS2), with a self-contained "dotnet publish".

.DESCRIPTION
    1. dotnet publish of src/PianoBooster.App: Release, win-x64, self-contained (the .NET runtime goes
       with it), into a folder (not a single file: the DLLs of FluidSynth are loaded from next to the
       program). It brings what the projects copy next to the program, as the C++ package does:
       BoosterMusicBooks.zip, fonts/DejaVuSans.ttf, translations/langs.json and the translations
       (<culture>/PianoBooster.resources.dll, made by tools/ts-to-resx.ps1).
    2. README.md, LICENSE (GPLv3) and NOTICE.
    3. FluidSynth: libfluidsynth-3.dll and, repeating "objdump -p" until no new one appears, every DLL it
       needs that is in the same MSYS2 folder (as the ldd loop of package-windows.sh).
    4. The SoundFont FluidR3_GM.sf2 in soundfont/, where the program looks for one the first time
       (CSettings::setupDefaultSoundFont()). The C++ package has none.
    5. The zip PianoBooster-<version>-win64.zip (version of Directory.Build.props), with Compress-Archive.

    Part of the .NET port of PianoBooster. GPLv3.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\package-windows.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\package-windows.ps1 -SoundFont D:\sf2\FluidR3_GM.sf2
#>
param(
    # where the package folder and the zip go
    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts\package'),
    # the SoundFont to include; by default FluidR3_GM.sf2 from the usual places
    [string]$SoundFont,
    # do not include a SoundFont (as the C++ package)
    [switch]$NoSoundFont,
    # the folder of libfluidsynth-3.dll and its DLLs (MSYS2 UCRT64), with objdump.exe
    [string]$FluidSynthDir = 'C:\msys64\ucrt64\bin'
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# The texts of the INFO list of a SoundFont (RIFF "sfbk"): INAM name, IENG engineer, ICOP copyright...
function Get-SoundFontInfo([string]$path) {
    $info = [ordered]@{}
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $ascii = [System.Text.Encoding]::ASCII
        if ($ascii.GetString($reader.ReadBytes(4)) -ne 'RIFF') { return $info }
        [void]$reader.ReadUInt32()
        if ($ascii.GetString($reader.ReadBytes(4)) -ne 'sfbk') { return $info }
        if ($ascii.GetString($reader.ReadBytes(4)) -ne 'LIST') { return $info }
        $listEnd = $stream.Position + 4 + $reader.ReadUInt32()
        if ($ascii.GetString($reader.ReadBytes(4)) -ne 'INFO') { return $info }
        while ($stream.Position + 8 -le $listEnd) {
            $id = $ascii.GetString($reader.ReadBytes(4))
            $size = $reader.ReadUInt32()
            $data = $reader.ReadBytes([int]$size)
            if ($size % 2 -eq 1) { [void]$reader.ReadByte() } # chunks are word aligned
            if ($id -ne 'ifil' -and $id -ne 'iver') {
                $info[$id] = $ascii.GetString($data).TrimEnd([char]0).Trim()
            }
        }
    } finally {
        $stream.Close()
    }
    return $info
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.SelectSingleNode('Version') } | Where-Object { $_ })[0].InnerText
$name = "PianoBooster-$version-win64"
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory $OutDir | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path
$packageDir = Join-Path $OutDir $name
$zip = Join-Path $OutDir "$name.zip"

# The SoundFont (checked first: it is the only thing that may be missing)
if (-not $NoSoundFont) {
    if (-not $SoundFont) {
        $candidates = @(
            (Join-Path ([Environment]::GetFolderPath('MyMusic')) 'Soundfonts\FluidR3_GM.sf2'),
            (Join-Path ([Environment]::GetFolderPath('MyMusic')) 'FluidR3_GM.sf2'),
            (Join-Path $FluidSynthDir '..\share\soundfonts\FluidR3_GM.sf2'))
        $SoundFont = @($candidates | Where-Object { Test-Path $_ }) | Select-Object -First 1
        if (-not $SoundFont) {
            throw "FluidR3_GM.sf2 not found (looked in: $($candidates -join '; ')). Give it with -SoundFont, or use -NoSoundFont."
        }
    }
    if (-not (Test-Path $SoundFont)) { throw "SoundFont not found: $SoundFont" }
}
$fluidSynth = Join-Path $FluidSynthDir 'libfluidsynth-3.dll'
$objdump = Join-Path $FluidSynthDir 'objdump.exe'
if (-not (Test-Path $fluidSynth)) { throw "$fluidSynth not found: install FluidSynth with MSYS2 (pacman -S mingw-w64-ucrt-x86_64-fluidsynth)" }
if (-not (Test-Path $objdump)) { throw "$objdump not found: install binutils with MSYS2 (pacman -S mingw-w64-ucrt-x86_64-binutils)" }

Write-Output "Packaging $name"
if (Test-Path $packageDir) { Remove-Item -Recurse -Force $packageDir }
if (Test-Path $zip) { Remove-Item -Force $zip }

# 1. The program, self-contained
& dotnet publish (Join-Path $root 'src\PianoBooster.App\PianoBooster.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false `
    -o $packageDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
foreach ($needed in @('PianoBooster.exe', 'BoosterMusicBooks.zip', 'fonts\DejaVuSans.ttf', 'translations\langs.json', 'es\PianoBooster.resources.dll')) {
    if (-not (Test-Path (Join-Path $packageDir $needed))) { throw "$needed is missing from the published folder" }
}
# The NuGet packages of Skia and HarfBuzz bring the symbols of their native DLLs (about 100 MB), and
# DryWetMIDI its library for macOS: not needed to run on Windows
Get-ChildItem $packageDir -File | Where-Object { $_.Extension -in '.pdb', '.dylib' } | Remove-Item

# 2. The documents
foreach ($doc in @('README.md', 'LICENSE', 'NOTICE')) {
    Copy-Item (Join-Path $root $doc) $packageDir
}

# 3. FluidSynth and every DLL it needs from the same folder
$queue = New-Object System.Collections.Generic.Queue[string]
$copied = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
$queue.Enqueue('libfluidsynth-3.dll')
[void]$copied.Add('libfluidsynth-3.dll')
while ($queue.Count -gt 0) {
    $dll = $queue.Dequeue()
    $source = Join-Path $FluidSynthDir $dll
    Copy-Item $source $packageDir
    foreach ($line in (& $objdump -p $source)) {
        if ($line -match '^\s*DLL Name:\s*(\S+)') {
            $dependency = $Matches[1]
            if ((Test-Path (Join-Path $FluidSynthDir $dependency)) -and $copied.Add($dependency)) {
                $queue.Enqueue($dependency)
            }
        }
    }
}
Write-Output ("FluidSynth: " + (($copied | Sort-Object) -join ', '))

# 4. The SoundFont
if (-not $NoSoundFont) {
    $soundFontDir = Join-Path $packageDir 'soundfont'
    New-Item -ItemType Directory $soundFontDir | Out-Null
    Copy-Item $SoundFont $soundFontDir
    $info = Get-SoundFontInfo $SoundFont
    $readme = @(
        "$(Split-Path $SoundFont -Leaf): the General MIDI SoundFont used by the FluidSynth sound generator of",
        "PianoBooster when no other has been chosen (Setup/MIDI Setup, FluidSynth tab).",
        "",
        "From the INFO chunk of the file:")
    foreach ($key in 'INAM', 'IENG', 'ICOP', 'ICRD', 'ICMT') {
        if ($info.Contains($key)) { $readme += "  ${key}: $($info[$key])" }
    }
    $readme += @("", "It is not part of PianoBooster and is not covered by its licence (GPLv3).")
    Set-Content (Join-Path $soundFontDir 'README.txt') $readme -Encoding ascii
    Write-Output "SoundFont: $(Split-Path $SoundFont -Leaf) ($(($info.Values) -join ' | '))"
    if ($info.Contains('ICMT') -and $info['ICMT'] -match 'not\s+redistribute') {
        Write-Warning "The SoundFont says: '$($info['ICMT'])'. Check that you may distribute it before sharing this package, or use -NoSoundFont or another -SoundFont."
    }
}

# 5. The zip
Compress-Archive -Path $packageDir -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length / 1MB
Write-Output ("Done: {0} ({1:N0} MB)" -f $zip, $size)
