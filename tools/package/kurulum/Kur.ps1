# Excalibur Reimagined deneme paketi: kurulum. 1-Kur.cmd çalıştırır; yönetici izni kendisi ister.
# Depodaki tools\scripts\install-fan.ps1 ile aynı işi yapar, ama derlemez: paketteki program\ klasörünü kopyalar
# (.NET içinde gelir) ve PawnIO yoksa resmi sürümünü indirip SHA-256 ile doğrulayarak kurdurur.
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    return
}

$root = Split-Path $PSScriptRoot -Parent
$programSrc = Join-Path $root 'program'
$installDir = Join-Path $env:ProgramFiles 'ExcaliburReimagined'
$dataDir = Join-Path $env:ProgramData 'ExcaliburReimagined'
$stateFile = Join-Path $dataDir 'install-state.json'
$pawnIoLib = 'C:\Program Files\PawnIO\PawnIOLib.dll'
$pawnIoUrl = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
$pawnIoSha256 = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032'
$services = @(
    @{ Name = 'ExcaliburFan'; Display = 'Excalibur Reimagined Fan Service'; Folder = 'Excalibur.FanService'; Exe = 'ExcaliburFanService.exe';
       Description = 'Excalibur fan curve control. Hands the fans back to the EC when stopped.' },
    @{ Name = 'ExcaliburFanGuardian'; Display = 'Excalibur Reimagined Fan Guardian'; Folder = 'Excalibur.FanGuardian'; Exe = 'ExcaliburFanGuardian.exe';
       Description = 'Hands the fans back to the EC if the Excalibur fan service dies while holding them.' }
)
$appExe = Join-Path $installDir 'Excalibur.App\ExcaliburReimagined.exe'
$linkName = 'Excalibur Reimagined.lnk'

function Say([string]$text) { Write-Host "-> $text" }

try {
    Write-Host "Excalibur Reimagined kuruluyor...`n" -ForegroundColor Cyan
    if (-not (Test-Path (Join-Path $programSrc 'Excalibur.App\ExcaliburReimagined.exe'))) {
        throw "Paketteki 'program' klasörü bulunamadı. Zip'in tamamını bir klasöre çıkarıp 1-Kur.cmd'yi oradan çalıştırın."
    }

    # İnternetten inen dosyalardaki "engellendi" işaretini kaldır (yoksa Windows programı açarken uyarır).
    Say 'Paket dosyalarının engeli kaldırılıyor'
    Get-ChildItem -LiteralPath $root -Recurse -File | Unblock-File

    # --- 1. PawnIO ----------------------------------------------------------------------------
    if (Test-Path $pawnIoLib) {
        Say 'PawnIO zaten kurulu'
    } else {
        $setup = Join-Path ([IO.Path]::GetTempPath()) 'PawnIO_setup.exe'
        Say 'PawnIO indiriliyor (resmi sayfa: github.com/namazso/PawnIO.Setup)'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $pawnIoUrl -OutFile $setup -UseBasicParsing
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash.ToLowerInvariant()
        if ($hash -ne $pawnIoSha256) {
            Remove-Item $setup -Force
            throw "İndirilen PawnIO dosyası beklenen dosya değil (SHA-256 uyuşmadı). Kurulmadı."
        }
        Say 'PawnIO kurulum penceresi açılıyor: "Install" deyin, bitince pencereyi kapatın'
        Start-Process -FilePath $setup -Wait
        Remove-Item $setup -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $pawnIoLib)) { throw 'PawnIO kurulmadı. 1-Kur.cmd ile tekrar deneyin ve PawnIO penceresinde "Install" deyin.' }
        Say 'PawnIO kuruldu'
    }

    # --- 2. çalışanları durdur (fan hizmeti dururken fanları dizüstüne bırakır) -------------------
    foreach ($s in @($services[1], $services[0])) {
        $svc = Get-Service -Name $s.Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') {
            Say "Eski $($s.Name) hizmeti durduruluyor"
            Stop-Service -Name $s.Name -Force
            (Get-Service $s.Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    }
    foreach ($proc in @(Get-Process -Name 'ExcaliburReimagined' -ErrorAction SilentlyContinue)) {
        $proc.Kill(); $proc.WaitForExit(5000) | Out-Null
    }

    # --- 3. dosyalar ----------------------------------------------------------------------------
    Say "Program dosyaları kopyalanıyor: $installDir"
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    foreach ($folder in 'Excalibur.FanService', 'Excalibur.FanGuardian', 'Excalibur.App') {
        $dest = Join-Path $installDir $folder
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Copy-Item -LiteralPath (Join-Path $programSrc $folder) -Destination $dest -Recurse
    }

    # --- 4. veri klasörü: yalnız SYSTEM/Yöneticiler yazar, kullanıcılar okur (\sim yazılabilir) -----
    Say "Ayar ve günlük klasörü hazırlanıyor: $dataDir"
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    & icacls $dataDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    & icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /C /Q | Out-Null
    if (Get-ChildItem -LiteralPath $dataDir -Force) { & icacls (Join-Path $dataDir '*') /reset /T /C /Q | Out-Null }
    $sim = Join-Path $dataDir 'sim'
    New-Item -ItemType Directory -Force -Path $sim | Out-Null
    & icacls $sim /grant '*S-1-5-32-545:(OI)(CI)M' /C /Q | Out-Null

    # --- 5. NoteBook FanControl varsa kapat (iki program aynı fana yazmasın; kaldırınca geri gelir) ---
    $nbfc = Get-CimInstance Win32_Service -Filter "Name='NbfcService'" -ErrorAction SilentlyContinue
    if ($nbfc) {
        Say 'NoteBook FanControl hizmeti kapatılıyor (kaldırınca eski haline döner)'
        if (-not (Test-Path $stateFile)) {
            @{ NbfcStartMode = $nbfc.StartMode; NbfcState = $nbfc.State } | ConvertTo-Json | Set-Content -Path $stateFile -Encoding UTF8
        }
        if ($nbfc.State -ne 'Stopped') { Stop-Service -Name 'NbfcService' -Force }
        Set-Service -Name 'NbfcService' -StartupType Disabled
    }

    # --- 6. hizmetler ---------------------------------------------------------------------------
    foreach ($s in $services) {
        $exe = Join-Path (Join-Path $installDir $s.Folder) $s.Exe
        Say "$($s.Name) hizmeti kuruluyor"
        if (Get-Service -Name $s.Name -ErrorAction SilentlyContinue) {
            & sc.exe config $s.Name binPath= "`"$exe`"" start= auto DisplayName= $s.Display | Out-Null
        } else {
            New-Service -Name $s.Name -BinaryPathName "`"$exe`"" -DisplayName $s.Display -StartupType Automatic | Out-Null
        }
        & sc.exe description $s.Name $s.Description | Out-Null
        & sc.exe failure $s.Name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
        & sc.exe failureflag $s.Name 1 | Out-Null
    }
    foreach ($s in $services) {
        Start-Service -Name $s.Name
        (Get-Service $s.Name).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    }

    # --- 7. kısayollar: Başlat menüsü (pencere) ve oturum açılışı (saat yanındaki simge) -----------
    $startMenuLink = Join-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') $linkName
    $shell = New-Object -ComObject WScript.Shell
    foreach ($link in @(@{ Path = $startMenuLink; Args = '--window' }, @{ Path = (Join-Path ([Environment]::GetFolderPath('Startup')) $linkName); Args = '' })) {
        $sc = $shell.CreateShortcut($link.Path)
        $sc.TargetPath = $appExe
        $sc.Arguments = $link.Args
        $sc.WorkingDirectory = Split-Path $appExe
        $sc.Save()
    }
    Say 'Kısayollar eklendi (Başlat menüsü ve Windows açılışı)'

    # Normal kullanıcı olarak aç (explorer üzerinden): önce simge, sonra pencere.
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$appExe`""
    Start-Sleep -Seconds 2
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$startMenuLink`""

    Write-Host "`nKurulum bitti. Uygulama penceresi açılıyor; 'BENI OKU.txt' içindeki DENEME adımlarına geçebilirsiniz." -ForegroundColor Green
} catch {
    Write-Host "`nKURULUM DURDU: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host "`nKapatmak için Enter'a basın"
