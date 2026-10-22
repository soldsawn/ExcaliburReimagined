# Excalibur Reimagined deneme paketi: kaldırma. 2-Kaldir.cmd çalıştırır; yönetici izni kendisi ister.
# Hizmetleri durdurur (fan hizmeti dururken fanları dizüstünün kendi ayarına bırakır), siler, dosyaları ve
# kısayolları kaldırır, NoteBook FanControl'ü eski haline getirir. Ayarlar/günlükler ve PawnIO kalır.
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    return
}

$installDir = Join-Path $env:ProgramFiles 'ExcaliburReimagined'
$dataDir = Join-Path $env:ProgramData 'ExcaliburReimagined'
$stateFile = Join-Path $dataDir 'install-state.json'
$linkName = 'Excalibur Reimagined.lnk'

function Say([string]$text) { Write-Host "-> $text" }

try {
    # Önce koruma hizmeti (fan hizmetini yeniden başlatmasın), sonra fan hizmeti.
    foreach ($name in 'ExcaliburFanGuardian', 'ExcaliburFan') {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        if ($svc.Status -ne 'Stopped') {
            Say "$name durduruluyor"
            Stop-Service -Name $name -Force
            (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        Say "$name siliniyor"
        & sc.exe delete $name | Out-Null
    }
    foreach ($proc in @(Get-Process -Name 'ExcaliburReimagined' -ErrorAction SilentlyContinue)) {
        $proc.Kill(); $proc.WaitForExit(5000) | Out-Null
    }
    foreach ($dir in (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'), [Environment]::GetFolderPath('Startup')) {
        $lnk = Join-Path $dir $linkName
        if (Test-Path $lnk) { Remove-Item $lnk -Force }
    }
    if (Test-Path $installDir) {
        Say "$installDir siliniyor"
        Remove-Item $installDir -Recurse -Force
    }
    if (Test-Path $stateFile) {
        $state = Get-Content $stateFile -Raw | ConvertFrom-Json
        $mode = switch ($state.NbfcStartMode) { 'Auto' { 'Automatic' } 'Manual' { 'Manual' } default { 'Disabled' } }
        Say 'NoteBook FanControl eski haline getiriliyor'
        Set-Service -Name 'NbfcService' -StartupType $mode
        if ($state.NbfcState -eq 'Running') { Start-Service -Name 'NbfcService' }
        Remove-Item $stateFile -Force
    }
    Write-Host "`nKaldırıldı. Fanlar dizüstünün kendi ayarında." -ForegroundColor Green
    Write-Host "Ayarlar ve günlükler duruyor: $dataDir (istemezseniz silebilirsiniz)."
    Write-Host "PawnIO sürücüsü duruyor; istemezseniz Ayarlar > Uygulamalar > PawnIO > Kaldır."
} catch {
    Write-Host "`nKALDIRMA DURDU: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host "`nKapatmak için Enter'a basın"
