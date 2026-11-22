<#
.SYNOPSIS
    Converts the Qt Linguist translations of PianoBooster (translations/pianobooster_<code>.ts of the
    C++ code) to the .resx files of the .NET port (src/PianoBooster.App/translations/Strings.<culture>.resx).

.DESCRIPTION
    Does what lrelease does (with its default options) for the CMake build of the C++ code:
    - The languages are the ones of translations/CMakeLists.txt (TRANSLATIONS_FILES), or all of them
      with -All (as INSTALL_ALL_LANGS); never pianobooster_blank.ts.
    - A message is used if its translation is not empty and is not "obsolete" or "vanished";
      "unfinished" translations that have a text are used too (lrelease drops them only with -nounfinished).
    Keys of the .resx (read by PianoBooster.App.Strings):
    - "<source>": the translation in the first context (Qt class) where the text appears.
    - "<context>|<source>": also written, for every context, when the same text has different
      translations in different contexts (the note names "B" -> "H" of German...). Listed at the end.
    - "<source>#<k>": the plural forms (numerus="yes"), k = 0, 1, ...
    Also writes Strings.resx, empty: the neutral resources (English, the texts of the source code).
    Generated .resx files that are no longer produced are deleted.

    Part of the .NET port of PianoBooster. GPLv3.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\ts-to-resx.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\ts-to-resx.ps1 -All
#>
param(
    # translations/ of the C++ code (read only)
    [string]$TsDir = (Join-Path $PSScriptRoot '..\..\PianoBooster\translations'),
    # where the .resx files go
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\PianoBooster.App\translations'),
    # all the .ts files, not only those of CMakeLists.txt
    [switch]$All,
    # only these codes of Qt (e.g. "es","de"); for tests
    [string[]]$Languages
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$TsDir = (Resolve-Path $TsDir).Path
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory $OutDir | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

# The language codes (of Qt) to convert
if ($Languages) {
    $codes = $Languages
} elseif ($All) {
    $codes = Get-ChildItem $TsDir -Filter 'pianobooster_*.ts' | ForEach-Object { $_.BaseName.Substring('pianobooster_'.Length) }
} else {
    $cmake = Get-Content (Join-Path $TsDir 'CMakeLists.txt') -Raw
    $codes = [regex]::Matches($cmake, 'pianobooster_([A-Za-z@_]+)\.ts') | ForEach-Object { $_.Groups[1].Value }
}
$codes = @($codes | Where-Object { $_ -ne 'blank' } | Select-Object -Unique)

# Qt code -> .NET culture name: "zh_HK" -> "zh-HK", "sr@latin" -> "sr-Latn"
function ConvertTo-CultureName([string]$code) {
    $name = $code -replace '@latin$', '-Latn' -replace '_', '-'
    try {
        $culture = [System.Globalization.CultureInfo]::GetCultureInfo($name)
        if ($culture.Name -ieq $name -and $culture.EnglishName -notmatch '^Unknown') { return $culture.Name }
    } catch { }
    return $null
}

function Write-Resx([string]$path, [string]$comment, $entries) {
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = [System.Xml.XmlWriter]::Create($path, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteComment($comment)
        $writer.WriteStartElement('root')
        foreach ($h in @(@('resmimetype', 'text/microsoft-resx'), @('version', '2.0'),
                @('reader', 'System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'),
                @('writer', 'System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'))) {
            $writer.WriteStartElement('resheader')
            $writer.WriteAttributeString('name', $h[0])
            $writer.WriteElementString('value', $h[1])
            $writer.WriteEndElement()
        }
        foreach ($e in $entries) {
            $writer.WriteStartElement('data')
            $writer.WriteAttributeString('name', $e.Key)
            $writer.WriteAttributeString('xml', 'space', 'http://www.w3.org/XML/1998/namespace', 'preserve')
            $writer.WriteElementString('value', $e.Value)
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    } finally {
        $writer.Close()
    }
}

# The texts of one message (an array: one text, or the plural forms); $null if it is not translated
function Get-Translation($message) {
    $translation = $message.SelectSingleNode('translation')
    if ($null -eq $translation) { return $null }
    $type = $translation.GetAttribute('type')
    if ($type -eq 'obsolete' -or $type -eq 'vanished') { return $null }
    if ($message.GetAttribute('numerus') -eq 'yes') {
        $forms = @($translation.SelectNodes('numerusform') | ForEach-Object { $_.InnerText })
        if (-not ($forms | Where-Object { $_ -ne '' })) { return $null }
        return @{ Forms = $forms }
    }
    if ($translation.InnerText -eq '') { return $null }
    return @{ Forms = @($translation.InnerText) }
}

$written = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
$total = 0

foreach ($code in $codes) {
    $tsFile = Join-Path $TsDir "pianobooster_$code.ts"
    $culture = ConvertTo-CultureName $code
    if (-not $culture) { Write-Warning "${code}: not a culture of .NET, skipped"; continue }
    if (-not (Test-Path $tsFile)) { Write-Warning "${code}: $tsFile not found, skipped"; continue }

    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($tsFile)

    # source -> list of @{ Context; Forms; Numerus }, in the order of the file
    $bySource = [ordered]@{}
    foreach ($context in $xml.SelectNodes('/TS/context')) {
        $contextName = $context.SelectSingleNode('name').InnerText
        foreach ($message in $context.SelectNodes('message')) {
            $translated = Get-Translation $message
            if ($null -eq $translated) { continue }
            $source = $message.SelectSingleNode('source').InnerText
            if ($source.Contains('|')) { Write-Warning "${code}: '$source' has a |: its key may clash with a key by context" }
            if (-not $bySource.Contains($source)) { $bySource[$source] = New-Object System.Collections.ArrayList }
            [void]$bySource[$source].Add(@{ Context = $contextName; Forms = $translated.Forms; Numerus = ($message.GetAttribute('numerus') -eq 'yes') })
        }
    }

    $entries = New-Object System.Collections.ArrayList
    $keys = New-Object System.Collections.Generic.HashSet[string]
    $conflicts = New-Object System.Collections.ArrayList
    function Add-Entries([string]$key, $item) {
        if ($item.Numerus) {
            for ($k = 0; $k -lt $item.Forms.Count; $k++) {
                if ($item.Forms[$k] -ne '' -and $keys.Add("$key#$k")) { [void]$entries.Add(@{ Key = "$key#$k"; Value = $item.Forms[$k] }) }
            }
        } elseif ($keys.Add($key)) {
            [void]$entries.Add(@{ Key = $key; Value = $item.Forms[0] })
        }
    }
    foreach ($source in $bySource.Keys) {
        $items = $bySource[$source]
        Add-Entries $source $items[0]
        $distinct = @($items | ForEach-Object { $_.Forms -join "`n" } | Select-Object -Unique)
        if ($distinct.Count -gt 1) {
            [void]$conflicts.Add("'$source' (" + (($items | ForEach-Object { "$($_.Context): $($_.Forms -join ' / ')" }) -join '; ') + ')')
            foreach ($item in $items) { Add-Entries "$($item.Context)|$source" $item }
        }
    }

    $resx = Join-Path $OutDir "Strings.$culture.resx"
    Write-Resx $resx "Generated by tools/ts-to-resx.ps1 from translations/pianobooster_$code.ts of the C++ code. Do not edit: run the script again." $entries
    [void]$written.Add((Split-Path $resx -Leaf))
    $total++
    Write-Output ("{0,-8} -> Strings.{1}.resx: {2} texts" -f $code, $culture, $entries.Count)
    foreach ($c in $conflicts) { Write-Output "         by context: $c" }
}

# The neutral resources: English, the texts themselves (Strings.Tr returns the text when there is no key)
Write-Resx (Join-Path $OutDir 'Strings.resx') 'Generated by tools/ts-to-resx.ps1: the neutral resources (English) are the texts of the source code, so it is empty.' @()

foreach ($old in Get-ChildItem $OutDir -Filter 'Strings.*.resx') {
    if (-not $written.Contains($old.Name)) {
        Remove-Item $old.FullName
        Write-Output "removed $($old.Name)"
    }
}
Write-Output "$total languages written to $OutDir"
