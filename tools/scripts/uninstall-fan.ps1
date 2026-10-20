# Removes what install-fan.ps1 installed and puts the fans back on the stock EC curve.
# Run from an elevated PowerShell:  powershell -ExecutionPolicy Bypass -File tools\scripts\uninstall-fan.ps1 [-DryRun]
# Keeps %ProgramData%\ExcaliburReimagined (logs, settings); delete it by hand if you want.
param([switch]$DryRun)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$installDir = Join-Path $env:ProgramFiles 'ExcaliburReimagined'
$dataDir = Join-Path $env:ProgramData 'ExcaliburReimagined'
$stateFile = Join-Path $dataDir 'install-state.json'
$links = 'Excalibur Reimagined.lnk', 'Excalibur Reimagined - Gelişmiş fan kontrolü.lnk', 'Excalibur Reimagined Ayrıntılar.lnk', 'Excalibur Fan.lnk', 'Excalibur Fan Ayrıntılar.lnk'  # current and earlier names

function Step([string]$what, [scriptblock]$action) {
    if ($DryRun) { Write-Host "[dry-run] $what" -ForegroundColor Cyan; return }
    Write-Host "-> $what"
    & $action
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if (-not $DryRun) { throw 'Run this from an elevated (administrator) PowerShell.' }
    Write-Warning 'Not elevated: dry run only.'
}

# Guardian first, then the fan service: stopping the fan service writes 0xFF to both targets.
foreach ($name in 'ExcaliburFanGuardian', 'ExcaliburFan') {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) { continue }
    if ($svc.Status -ne 'Stopped') {
        Step "stop service $name" { Stop-Service -Name $name -Force; (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    }
    Step "delete service $name" { & sc.exe delete $name | Out-Null }
}

# Belt and braces: make sure both fan targets are 0xFF even if the service could not do it.
$probe = Join-Path $repo 'src\Excalibur.EcProbe\bin\Debug\net9.0-windows\Excalibur.EcProbe.exe'
if (Test-Path $probe) {
    Step 'fanreset (0x2C/0x2D = 0xFF) with the EC probe' { & $probe fanreset }
} else {
    Write-Warning "EC probe not built ($probe); could not double-check the fan targets. Run run-ec-probe.cmd fanstate."
}

foreach ($proc in @(Get-Process -Name 'ExcaliburReimagined', 'ExcaliburFanTestUi', 'ExcaliburTray' -ErrorAction SilentlyContinue)) {
    Step "close $($proc.Name) (pid $($proc.Id))" { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null }
}
foreach ($dir in (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'), [Environment]::GetFolderPath('Startup')) {
    foreach ($link in $links) {
        $lnk = Join-Path $dir $link
        if (Test-Path $lnk) { Step "remove $lnk" { Remove-Item $lnk -Force } }
    }
}
if (Test-Path $installDir) { Step "remove $installDir" { Remove-Item $installDir -Recurse -Force } }

if (Test-Path $stateFile) {
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    $mode = switch ($state.NbfcStartMode) { 'Auto' { 'Automatic' } 'Manual' { 'Manual' } default { 'Disabled' } }
    Step "restore NbfcService start type to $mode$(if ($state.NbfcState -eq 'Running') { ' and start it' })" {
        Set-Service -Name 'NbfcService' -StartupType $mode
        if ($state.NbfcState -eq 'Running') { Start-Service -Name 'NbfcService' }
        Remove-Item $stateFile -Force
    }
}

if ($DryRun) { Write-Host "`nDry run only: nothing was changed." -ForegroundColor Cyan } else { Write-Host "`nUninstalled. Data kept in $dataDir." }
