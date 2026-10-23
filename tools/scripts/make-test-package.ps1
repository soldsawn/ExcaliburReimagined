# Builds the test package for other Excalibur owners: one folder (and a zip of it) that installs without the
# repo or the .NET SDK. Output: dist\ExcaliburReimagined-<version>-deneme\ and dist\ExcaliburReimagined-<version>-deneme.zip
#   1-Kur.cmd / 2-Kaldir.cmd     install / uninstall (tools\package\kurulum\*.ps1; PawnIO is downloaded and hash-checked there)
#   BENI OKU.txt                 the steps, in Turkish (version, commit and date filled in)
#   program\<project>\           the services and the app, published self-contained for win-x64 (no .NET install needed)
# Needs tools\pawnio-modules (get-pawnio-module.ps1). Usage:  powershell -ExecutionPolicy Bypass -File tools\scripts\make-test-package.ps1
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$commit = (& git -C $repo rev-parse --short HEAD).Trim()
$name = "ExcaliburReimagined-$version-deneme"
$dist = Join-Path $repo 'dist'
$out = Join-Path $dist $name
$zip = "$out.zip"

if (-not (Test-Path (Join-Path $repo 'tools\pawnio-modules\LpcACPIEC.bin'))) { throw 'tools\pawnio-modules is missing: run tools\scripts\get-pawnio-module.ps1 first.' }
if (& git -C $repo status --porcelain -- src) { Write-Warning 'src has uncommitted changes; the package will not match the commit it names.' }

# A readme edited by hand in the last package would be lost: stop until it is copied into tools\package.
$package = Join-Path $repo 'tools\package'
$builtReadme = Join-Path $out 'BENI OKU.txt'
if (Test-Path $builtReadme) {
    $norm = { param($t) (($t -replace "`r", '') -split "`n" | Select-Object -Skip 2 | ForEach-Object { $_.TrimEnd() }) -join "`n" }
    if ((& $norm ([IO.File]::ReadAllText($builtReadme))) -ne (& $norm ([IO.File]::ReadAllText((Join-Path $package 'BENI OKU.txt'))))) {
        throw "$builtReadme was edited and differs from tools\package\BENI OKU.txt: copy the edits there first (keep the {VERSION} line)."
    }
}
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($project in 'Excalibur.FanService', 'Excalibur.FanGuardian', 'Excalibur.App') {
    Write-Host "publish $project"
    & dotnet publish (Join-Path $repo "src\$project") -c Release -r win-x64 --self-contained true -o (Join-Path $out "program\$project") --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

Copy-Item (Join-Path $package '1-Kur.cmd'), (Join-Path $package '2-Kaldir.cmd') -Destination $out
Copy-Item (Join-Path $package 'kurulum') -Destination $out -Recurse
Copy-Item (Join-Path $repo 'LICENSE') -Destination (Join-Path $out 'program\LICENSE.txt')
Copy-Item (Join-Path $repo 'tools\pawnio-modules\COPYING') -Destination (Join-Path $out 'program\PawnIO-modules-LICENSE.txt')
$readme = [IO.File]::ReadAllText((Join-Path $package 'BENI OKU.txt'), [Text.Encoding]::UTF8)
$readme = $readme.Replace('{VERSION}', $version).Replace('{COMMIT}', $commit).Replace('{DATE}', (Get-Date -Format 'yyyy-MM-dd'))
# CRLF and a BOM so Notepad on any Windows shows the Turkish letters.
[IO.File]::WriteAllText((Join-Path $out 'BENI OKU.txt'), ($readme -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($true)))

Compress-Archive -Path $out -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Host "ready: $zip ($size MB, commit $commit)"
