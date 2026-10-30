# The service part of the Excalibur Reimagined setup (ExcaliburReimagined.iss runs it hidden, elevated). The setup itself copies
# the files, makes the shortcuts and installs PawnIO; this does what Inno Setup cannot do neatly, as tools\package\kurulum\Kur.ps1:
#   -Action Stop        stop both services (guardian first: the fan service hands the fans to the EC when it stops) and the app
#   -Action Install     data folder rights, NoteBook FanControl off (only with -DisableNbfc), both services created and started
#   -Action Uninstall   Stop, delete both services, NoteBook FanControl back as it was (when it is still installed)
# Writes what it did to %TEMP%\ExcaliburReimagined-setup.log; exit code 0 = done, 1 = failed (the message is the last log line).
param(
    [Parameter(Mandatory)][ValidateSet('Stop', 'Install', 'Uninstall')][string]$Action,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ExcaliburReimagined'),
    [switch]$DisableNbfc
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:ProgramData 'ExcaliburReimagined'
$stateFile = Join-Path $dataDir 'install-state.json'
$log = Join-Path ([IO.Path]::GetTempPath()) 'ExcaliburReimagined-setup.log'
$services = @(
    @{ Name = 'ExcaliburFan'; Display = 'Excalibur Reimagined Fan Service'; Folder = 'Excalibur.FanService'; Exe = 'ExcaliburFanService.exe';
       Description = 'Excalibur fan curve control. Hands the fans back to the EC when stopped.' },
    @{ Name = 'ExcaliburFanGuardian'; Display = 'Excalibur Reimagined Fan Guardian'; Folder = 'Excalibur.FanGuardian'; Exe = 'ExcaliburFanGuardian.exe';
       Description = 'Hands the fans back to the EC if the Excalibur fan service dies while holding them.' }
)

function Say([string]$text) { Add-Content -LiteralPath $log -Value "$(Get-Date -Format 'HH:mm:ss')  $text" -Encoding UTF8 }

function Stop-All {
    foreach ($s in @($services[1], $services[0])) {
        $svc = Get-Service -Name $s.Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') {
            Say "stop $($s.Name)"
            Stop-Service -Name $s.Name -Force
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    }
    # The app only talks to the service over the pipe; ending it loses nothing. (Not the services' exes: stopped above.)
    foreach ($proc in @(Get-Process -Name 'ExcaliburReimagined' -ErrorAction SilentlyContinue)) {
        Say "close ExcaliburReimagined (pid $($proc.Id))"
        $proc.Kill(); $proc.WaitForExit(5000) | Out-Null
    }
}

try {
    Say "--- $Action ($InstallDir)"
    switch ($Action) {
        'Stop' { Stop-All }

        'Install' {
            # The data folder: SYSTEM and Administrators write, users read; \sim stays user-writable (the simulated service).
            Say "data folder $dataDir"
            New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
            & icacls $dataDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
            & icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /C /Q | Out-Null
            if (Get-ChildItem -LiteralPath $dataDir -Force) { & icacls (Join-Path $dataDir '*') /reset /T /C /Q | Out-Null }
            $sim = Join-Path $dataDir 'sim'
            New-Item -ItemType Directory -Force -Path $sim | Out-Null
            & icacls $sim /grant '*S-1-5-32-545:(OI)(CI)M' /C /Q | Out-Null

            # NoteBook FanControl writes the same fan registers: off, as it was saved, only when the user agreed in the setup.
            $nbfc = Get-CimInstance Win32_Service -Filter "Name='NbfcService'" -ErrorAction SilentlyContinue
            if ($nbfc -and $DisableNbfc) {
                Say "NbfcService off (was $($nbfc.StartMode), $($nbfc.State))"
                if (-not (Test-Path $stateFile)) {
                    @{ NbfcStartMode = $nbfc.StartMode; NbfcState = $nbfc.State } | ConvertTo-Json | Set-Content -Path $stateFile -Encoding UTF8
                }
                if ($nbfc.State -ne 'Stopped') { Stop-Service -Name 'NbfcService' -Force }
                Set-Service -Name 'NbfcService' -StartupType Disabled
            }

            foreach ($s in $services) {
                $exe = Join-Path (Join-Path $InstallDir $s.Folder) $s.Exe
                if (-not (Test-Path $exe)) { throw "$exe is missing" }
                Say "service $($s.Name) -> $exe"
                if (Get-Service -Name $s.Name -ErrorAction SilentlyContinue) {
                    & sc.exe config $s.Name binPath= "`"$exe`"" start= auto DisplayName= $s.Display | Out-Null
                } else {
                    New-Service -Name $s.Name -BinaryPathName "`"$exe`"" -DisplayName $s.Display -StartupType Automatic | Out-Null
                }
                & sc.exe description $s.Name $s.Description | Out-Null
                # Restart 5 s after each failure (the count resets after a day); a non-zero exit code counts as a failure.
                & sc.exe failure $s.Name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
                & sc.exe failureflag $s.Name 1 | Out-Null
            }
            foreach ($s in $services) {
                Say "start $($s.Name)"
                Start-Service -Name $s.Name
                (Get-Service $s.Name).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
            }
        }

        'Uninstall' {
            Stop-All
            foreach ($s in @($services[1], $services[0])) {
                if (Get-Service -Name $s.Name -ErrorAction SilentlyContinue) { Say "delete $($s.Name)"; & sc.exe delete $s.Name | Out-Null }
            }
            if (Test-Path $stateFile) {
                $state = Get-Content $stateFile -Raw | ConvertFrom-Json
                # It may have been uninstalled since (2026-10-30 on the developer's machine): then there is nothing to put back.
                if (Get-Service -Name 'NbfcService' -ErrorAction SilentlyContinue) {
                    $mode = switch ($state.NbfcStartMode) { 'Auto' { 'Automatic' } 'Manual' { 'Manual' } default { 'Disabled' } }
                    Say "NbfcService back to $mode"
                    Set-Service -Name 'NbfcService' -StartupType $mode
                    if ($state.NbfcState -eq 'Running') { Start-Service -Name 'NbfcService' }
                }
                Remove-Item $stateFile -Force
            }
        }
    }
    Say 'done'
    exit 0
} catch {
    Say "FAILED: $($_.Exception.Message)"
    exit 1
}
