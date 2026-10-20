# Downloads the PawnIO module the fan service needs (LpcACPIEC.bin) into tools\pawnio-modules\.
# The module comes from the official PawnIO.Modules release (LGPL-2.1, https://github.com/namazso/PawnIO.Modules);
# it is not stored in this repository. The zip's SHA-256 is checked before anything is extracted.
# Usage:  powershell -ExecutionPolicy Bypass -File tools\scripts\get-pawnio-module.ps1
$ErrorActionPreference = 'Stop'

$version = '0.2.11'
$url = "https://github.com/namazso/PawnIO.Modules/releases/download/$version/release_$($version.Replace('.', '_')).zip"
$sha256 = '43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4'

$target = Join-Path $PSScriptRoot '..\pawnio-modules'
$target = [System.IO.Path]::GetFullPath($target)
if (Test-Path (Join-Path $target 'LpcACPIEC.bin')) { Write-Host "LpcACPIEC.bin is already in $target"; return }

New-Item -ItemType Directory -Force -Path $target | Out-Null
$zip = Join-Path ([System.IO.Path]::GetTempPath()) "pawnio-modules-$version.zip"
Write-Host "downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

$actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
if ($actual -ne $sha256) {
    Remove-Item $zip -Force
    throw "SHA-256 mismatch for the download (expected $sha256, got $actual). Not using it."
}

Expand-Archive -Path $zip -DestinationPath $target -Force
Remove-Item $zip -Force
if (-not (Test-Path (Join-Path $target 'LpcACPIEC.bin'))) { throw 'LpcACPIEC.bin was not in the archive.' }
Write-Host "ready: $(Join-Path $target 'LpcACPIEC.bin')"
