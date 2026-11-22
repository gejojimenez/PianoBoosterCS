# Runs the C++ RefDump on every scenario of tests/PianoBooster.Core.Tests/TestData/scenarios.txt
# and writes the references (TestData/reference/<name>.txt) that the C# tests compare with.
# Build RefDump first (build.sh, from an MSYS2 UCRT64 shell).
param(
    [string]$RefDump = (Join-Path $PSScriptRoot 'build\refdump.exe'),
    [string]$Msys2Bin = 'C:\msys64\ucrt64\bin'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $RefDump)) { throw "RefDump not found: $RefDump (build it with build.sh)" }
$data = (Resolve-Path (Join-Path $PSScriptRoot '..\..\tests\PianoBooster.Core.Tests\TestData')).Path
$referenceDir = Join-Path $data 'reference'
New-Item -ItemType Directory -Force $referenceDir | Out-Null
$env:PATH = "$Msys2Bin;$env:PATH" # the Qt and MinGW DLLs

$count = 0
foreach ($line in Get-Content (Join-Path $data 'scenarios.txt')) {
    $line = $line.Trim()
    if ($line -eq '' -or $line.StartsWith('#')) { continue }
    $fields = $line -split '\s+'
    $name, $mode, $midi = $fields[0], $fields[1], $fields[2]
    $params = @()
    if ($fields.Count -gt 3) { $params = $fields[3..($fields.Count - 1)] }
    # forward slashes, like the C# tests, so that the song title (the file name) is the same
    $midiPath = (Join-Path $data "midi\$midi").Replace('\', '/')
    $outFile = Join-Path $referenceDir "$name.txt"
    & $RefDump $mode $midiPath $outFile @params *> $null
    if ($LASTEXITCODE -ne 0) { throw "RefDump failed on $name (exit code $LASTEXITCODE)" }
    $count++
}
Write-Host "$count references written to $referenceDir"
