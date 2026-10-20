# Logs GMC_WMIEvent payloads and fan RPM for a fixed duration (used to identify Fn-key behaviour).
param([int]$Seconds = 300, [string]$Log = "$PSScriptRoot\..\..\analysis\gm-events.log")

Import-Module "$PSScriptRoot\GmWmi.psm1" -Force
Remove-Item $Log -ErrorAction SilentlyContinue
Register-CimIndicationEvent -Namespace root/wmi -ClassName GMC_WMIEvent -SourceIdentifier GmEvt | Out-Null
$end = (Get-Date).AddSeconds($Seconds)
try {
    while ((Get-Date) -lt $end) {
        $e = Wait-Event -SourceIdentifier GmEvt -Timeout 2
        if ($e) {
            $b = $e.SourceEventArgs.NewEvent.EventDetail
            "{0:HH:mm:ss} EVENT type(ev[1])={1} bytes={2}" -f (Get-Date), $b[1], (($b[0..7] | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') | Add-Content $Log
            Remove-Event -EventIdentifier $e.EventIdentifier
        }
        $s = Get-GmSensors
        "{0:HH:mm:ss} CPU {1}C/{2}rpm GPU {3}C/{4}rpm" -f (Get-Date), $s.CpuC, $s.CpuRpm, $s.GpuC, $s.GpuRpm | Add-Content $Log
    }
} finally {
    Unregister-Event -SourceIdentifier GmEvt
}
