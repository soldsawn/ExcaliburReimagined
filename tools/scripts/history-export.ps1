# Exports the fan service's recorded history to CSV (docs/09 A2: curve shape analysis).
# Usage:  history-export.ps1 [-Hours 24] [-StepSeconds 10] [-Out history.csv] [-Sim]
#   Asks the service over its pipe (no admin needed); the service keeps 7 days by default.
param([double]$Hours = 24, [int]$StepSeconds = 10, [string]$Out = 'history.csv', [switch]$Sim)

$to = [DateTimeOffset]::Now
$from = $to.AddHours(-$Hours)
$points = [Math]::Min(4000, [Math]::Ceiling($Hours * 3600 / $StepSeconds))
$request = @{ cmd = 'history'; source = 'automation'; from = $from.ToString('o'); to = $to.ToString('o'); maxPoints = [int]$points } | ConvertTo-Json -Compress

$name = if ($Sim) { 'ExcaliburFanSim' } else { 'ExcaliburFan' }
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $pipe.Connect(3000)
    $writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
    $reader = New-Object System.IO.StreamReader($pipe)
    $writer.WriteLine($request)
    $reply = $reader.ReadLine() | ConvertFrom-Json
}
finally { $pipe.Dispose() }

if (-not $reply.ok) { throw "history failed: $($reply.error)" }
$h = $reply.history
$start = [DateTimeOffset]::Parse($h.from).ToLocalTime()
$rows = for ($i = 0; $i -lt $h.points.Count; $i++) {
    $p = $h.points[$i]
    if ($null -eq $p) { continue }
    [pscustomobject]@{
        time = $start.AddSeconds($i * $h.stepSeconds).ToString('yyyy-MM-dd HH:mm:ss')
        cpuC = $p.c; cpuMaxC = $p.cm; gpuC = $p.g; gpuMaxC = $p.gm
        cpuRpm = $p.cr; gpuRpm = $p.gr; cpuPct = $p.ca; gpuPct = $p.ga
        cpuWhy = $p.cw; gpuWhy = $p.gw
    }
}
$rows | Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8
Write-Host "$(@($rows).Count) rows ($($h.stepSeconds) s each) -> $Out"
