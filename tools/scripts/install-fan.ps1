# Installs Excalibur Reimagined: the fan service, its guardian and the app (tray + main window) (docs/07, docs/08).
# Run from an elevated PowerShell:  powershell -ExecutionPolicy Bypass -File tools\scripts\install-fan.ps1 [-DryRun]
#   -DryRun   only prints what would be done; changes nothing.
# What it does:
#   1. checks PawnIO and the .NET 9 runtimes
#   2. stops existing Excalibur services (the fan service hands the fans to the EC when it stops)
#   3. publishes the services and the app (Release) into %ProgramFiles%\ExcaliburReimagined (old tray/test-window folders removed)
#   4. locks %ProgramData%\ExcaliburReimagined to SYSTEM/Administrators (users: read; \sim stays user-writable)
#   5. disables the NoteBook FanControl service (start type saved, restored by uninstall-fan.ps1)
#   6. creates both services (automatic start, LocalSystem, restart on failure) and starts them
#   7. Start menu: "Excalibur Reimagined" (main window); this user's Startup folder: the same exe in tray mode
param([switch]$DryRun)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$installDir = Join-Path $env:ProgramFiles 'ExcaliburReimagined'
$dataDir = Join-Path $env:ProgramData 'ExcaliburReimagined'
$stateFile = Join-Path $dataDir 'install-state.json'
$services = @(
    @{ Name = 'ExcaliburFan'; Display = 'Excalibur Reimagined Fan Service'; Project = 'src\Excalibur.FanService'; Exe = 'ExcaliburFanService.exe';
       Description = 'Excalibur G870 fan curve control (EC 0x2C/0x2D). Hands the fans back to the EC when stopped.' },
    @{ Name = 'ExcaliburFanGuardian'; Display = 'Excalibur Reimagined Fan Guardian'; Project = 'src\Excalibur.FanGuardian'; Exe = 'ExcaliburFanGuardian.exe';
       Description = 'Hands the fans back to the EC (0xFF) if the Excalibur fan service dies while holding them.' }
)
# One exe: tray mode by default, the main window with --window (each in its own process).
$app = @{ Project = 'src\Excalibur.App'; Exe = 'ExcaliburReimagined.exe'; Link = 'Excalibur Reimagined.lnk' }
# Earlier installs: separate tray and test-window apps, and their shortcut names.
$oldProcesses = 'ExcaliburFanTestUi', 'ExcaliburTray'
$oldFolders = 'Excalibur.FanTestUi', 'Excalibur.Tray'
$oldLinks = 'Excalibur Fan.lnk', 'Excalibur Fan Ayrıntılar.lnk', 'Excalibur Reimagined Ayrıntılar.lnk', 'Excalibur Reimagined - Gelişmiş fan kontrolü.lnk'

function Step([string]$what, [scriptblock]$action) {
    if ($DryRun) { Write-Host "[dry-run] $what" -ForegroundColor Cyan; return }
    Write-Host "-> $what"
    & $action
}

# --- 0. elevation -------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if (-not $DryRun) { throw 'Run this from an elevated (administrator) PowerShell.' }
    Write-Warning 'Not elevated: dry run only.'
}

# --- 1. prerequisites ---------------------------------------------------------------------
if (-not (Test-Path 'C:\Program Files\PawnIO\PawnIOLib.dll')) { throw 'PawnIO is not installed (C:\Program Files\PawnIO\PawnIOLib.dll missing).' }
$runtimes = & dotnet --list-runtimes 2>$null
if (-not ($runtimes -match '^Microsoft\.NETCore\.App 9\.')) { throw '.NET 9 runtime (Microsoft.NETCore.App 9.x) not found.' }
if (-not ($runtimes -match '^Microsoft\.WindowsDesktop\.App 9\.')) { throw '.NET 9 Windows Desktop runtime not found (needed by the app).' }
Write-Host 'PawnIO and .NET 9 runtimes found.'

# --- 2. stop what is running --------------------------------------------------------------
# Guardian first, so it does not restart the fan service while we stop it.
foreach ($s in @($services[1], $services[0])) {
    $svc = Get-Service -Name $s.Name -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Step "stop service $($s.Name)" { Stop-Service -Name $s.Name -Force; (Get-Service $s.Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    }
}
# The app only reads status and sends commands over the pipe, so ending it is harmless.
foreach ($proc in @(Get-Process -Name (@('ExcaliburReimagined') + $oldProcesses) -ErrorAction SilentlyContinue)) {
    Step "close $($proc.Name) (pid $($proc.Id))" { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null }
}
# (A dry run stops nothing, so the installed services would trip this check.)
if (-not $DryRun) { foreach ($p in 'ExcaliburFanService', 'ExcaliburFanGuardian') {
    if (Get-Process -Name $p -ErrorAction SilentlyContinue) {
        throw "$p.exe is still running as a console app. Stop it with Ctrl+C first (so it hands the fans to the EC)."
    }
}
}

# --- 3. publish ---------------------------------------------------------------------------
foreach ($old in $oldFolders) {
    $dir = Join-Path $installDir $old
    if (Test-Path $dir) { Step "remove old $dir" { Remove-Item $dir -Recurse -Force } }
}
$all = @($services[0], $services[1], $app)
foreach ($a in $all) {
    $dest = Join-Path $installDir (Split-Path $a.Project -Leaf)
    Step "publish $($a.Project) -> $dest" {
        # Start clean: the test package installs self-contained copies here, and a leftover hostfxr.dll makes this
        # framework-dependent build look for .NET inside the folder ("No frameworks were found", 2026-10-24).
        # A process closed a moment ago (or a virus scan) can hold a file for a few seconds more.
        for ($try = 1; Test-Path $dest; $try++) {
            try { Remove-Item $dest -Recurse -Force -ErrorAction Stop }
            catch { if ($try -ge 10) { throw }; Start-Sleep -Seconds 1 }
        }
        & dotnet publish (Join-Path $repo $a.Project) -c Release -r win-x64 --self-contained false -o $dest --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($a.Project)" }
    }
}

# --- 4. data directory ACL ----------------------------------------------------------------
Step "lock $dataDir (SYSTEM/Administrators full, Users read; \sim user-writable)" {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    # Administrators own everything (so the ACL can always be repaired), the inheritable rules are
    # set on the directory only, and every child is reset to inherit them. (Granting (OI)(CI) rules
    # with /T directly onto files leaves them with an empty ACL: that broke the first install.)
    & icacls $dataDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    & icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /C /Q | Out-Null
    if (Get-ChildItem -LiteralPath $dataDir -Force) { & icacls (Join-Path $dataDir '*') /reset /T /C /Q | Out-Null }
    $sim = Join-Path $dataDir 'sim'
    New-Item -ItemType Directory -Force -Path $sim | Out-Null
    & icacls $sim /grant '*S-1-5-32-545:(OI)(CI)M' /C /Q | Out-Null
    if (Get-ChildItem -LiteralPath $sim -Force) { & icacls (Join-Path $sim '*') /reset /T /C /Q | Out-Null }
}

# --- 5. NoteBook FanControl ---------------------------------------------------------------
$nbfc = Get-CimInstance Win32_Service -Filter "Name='NbfcService'" -ErrorAction SilentlyContinue
if ($nbfc) {
    Step "disable NbfcService (was $($nbfc.StartMode), $($nbfc.State)); saved to $stateFile" {
        if (-not (Test-Path $stateFile)) {
            @{ NbfcStartMode = $nbfc.StartMode; NbfcState = $nbfc.State } | ConvertTo-Json | Set-Content -Path $stateFile -Encoding UTF8
        }
        if ($nbfc.State -ne 'Stopped') { Stop-Service -Name 'NbfcService' -Force }
        Set-Service -Name 'NbfcService' -StartupType Disabled
    }
}

# --- 6. services --------------------------------------------------------------------------
foreach ($s in $services) {
    $exe = Join-Path (Join-Path $installDir (Split-Path $s.Project -Leaf)) $s.Exe
    $existing = Get-Service -Name $s.Name -ErrorAction SilentlyContinue
    Step "create/update service $($s.Name) -> $exe (automatic, LocalSystem, restart on failure)" {
        if ($existing) {
            & sc.exe config $s.Name binPath= "`"$exe`"" start= auto DisplayName= $s.Display | Out-Null
        } else {
            New-Service -Name $s.Name -BinaryPathName "`"$exe`"" -DisplayName $s.Display -StartupType Automatic | Out-Null
        }
        & sc.exe description $s.Name $s.Description | Out-Null
        # Restart 5 s after each failure; the counter resets after a day. failureflag 1 also treats
        # "stopped with a non-zero exit code" (our unexpected-error path) as a failure.
        & sc.exe failure $s.Name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
        & sc.exe failureflag $s.Name 1 | Out-Null
    }
}
foreach ($s in $services) {
    Step "start service $($s.Name)" { Start-Service -Name $s.Name; (Get-Service $s.Name).WaitForStatus('Running', [TimeSpan]::FromSeconds(20)) }
}

# --- 7. shortcuts -------------------------------------------------------------------------
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$startup = [Environment]::GetFolderPath('Startup')
$appExe = Join-Path (Join-Path $installDir (Split-Path $app.Project -Leaf)) $app.Exe
$links = @(
    @{ Dir = $startMenu; Arguments = '--window'; Description = 'Excalibur Reimagined' },
    @{ Dir = $startup; Arguments = ''; Description = 'Excalibur Reimagined (tray)' }
)
foreach ($link in $links) {
    $lnk = Join-Path $link.Dir $app.Link
    Step "shortcut $lnk -> $appExe $($link.Arguments)" {
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnk)
        $sc.TargetPath = $appExe
        $sc.Arguments = $link.Arguments
        $sc.WorkingDirectory = Split-Path $appExe
        $sc.Description = $link.Description
        $sc.Save()
    }
}
foreach ($dir in $startMenu, $startup) {
    foreach ($old in $oldLinks) {
        $lnk = Join-Path $dir $old
        if (Test-Path $lnk) { Step "remove old shortcut $lnk" { Remove-Item $lnk -Force } }
    }
}

if ($DryRun) { Write-Host "`nDry run only: nothing was changed." -ForegroundColor Cyan; return }

Write-Host "`nInstalled. Current state:"
Get-Service -Name ($services | ForEach-Object { $_.Name }) | Format-Table Name, Status, StartType -AutoSize
Write-Host "Logs: $dataDir\logs   Uninstall: tools\scripts\uninstall-fan.ps1"
# Through explorer, so the tray runs as the normal (non-elevated) user.
Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$appExe`""
