# Builds the Excalibur Reimagined setup: dist\ExcaliburReimagined-<version>-setup.exe (tools\setup\ExcaliburReimagined.iss).
# Publishes the services and the app self-contained for win-x64 (no .NET install needed) into dist\setup-staging\, then runs
# Inno Setup's compiler. Needs Inno Setup 6 (winget install JRSoftware.InnoSetup) and tools\pawnio-modules (get-pawnio-module.ps1).
# Usage:  powershell -ExecutionPolicy Bypass -File tools\scripts\make-setup.ps1
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$staging = Join-Path $repo 'dist\setup-staging'

if (-not (Test-Path (Join-Path $repo 'tools\pawnio-modules\LpcACPIEC.bin'))) { throw 'tools\pawnio-modules is missing: run tools\scripts\get-pawnio-module.ps1 first.' }
if (& git -C $repo status --porcelain -- src) { Write-Warning 'src has uncommitted changes; the setup will not match a commit.' }
$iscc = @("${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup).' }

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
foreach ($project in 'Excalibur.FanService', 'Excalibur.FanGuardian', 'Excalibur.App') {
    Write-Host "publish $project"
    & dotnet publish (Join-Path $repo "src\$project") -c Release -r win-x64 --self-contained true -o (Join-Path $staging $project) --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

& $iscc /Q "/DAppVersion=$version" "/DStaging=$staging" (Join-Path $repo 'tools\setup\ExcaliburReimagined.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
$setup = Join-Path $repo "dist\ExcaliburReimagined-$version-setup.exe"
Write-Host "ready: $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"
