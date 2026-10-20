# Sends one JSON request to the Excalibur fan service's control pipe and prints the reply.
# Usage:  fanpipe.ps1 '{"cmd":"status"}'
#         fanpipe.ps1 '{"cmd":"setProfile","profile":"Quiet"}'
#         fanpipe.ps1 '{"cmd":"setManual","cpu":70,"gpu":60,"minutes":1}'
#         fanpipe.ps1 '{"cmd":"release"}'   (hand both fans to the EC)
#         fanpipe.ps1 -Sim '{"cmd":"status"}'   (the --simulate instance)
param([Parameter(Mandatory = $true)][string]$Json, [int]$TimeoutMs = 3000, [switch]$Sim)

$name = if ($Sim) { 'ExcaliburFanSim' } else { 'ExcaliburFan' }

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $pipe.Connect($TimeoutMs)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $reader = New-Object System.IO.StreamReader($pipe)
    $writer.WriteLine($Json)
    $reader.ReadLine()
}
catch {
    $e = $_.Exception
    if ($null -ne $e.InnerException) { $e = $e.InnerException }
    Write-Output "ERROR: $($e.Message)"
}
finally {
    $pipe.Dispose()
}
