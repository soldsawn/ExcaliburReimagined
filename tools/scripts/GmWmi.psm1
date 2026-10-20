# Thin wrapper over the Excalibur EC gateway root\wmi:RW_GMWMI.BufferBytes (see docs/02-donanim-protokolu.md).
# Layout (little-endian): u16 a0 (0xFA00=GET, 0xFB00=SET) | u16 a1 (function code) | u32 a2..a6 | u32 rev0..rev1

function Invoke-GmWmi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Get', 'Set')][string]$Op,
        [Parameter(Mandatory)][uint16]$Code,
        [uint32[]]$Arguments = @()
    )
    if ($Arguments.Count -gt 5) { throw 'At most 5 arguments (a2..a6).' }

    $buf = New-Object byte[] 32
    $a0 = if ($Op -eq 'Get') { [uint16]0xFA00 } else { [uint16]0xFB00 }
    [BitConverter]::GetBytes($a0).CopyTo($buf, 0)
    [BitConverter]::GetBytes($Code).CopyTo($buf, 2)
    for ($i = 0; $i -lt $Arguments.Count; $i++) { [BitConverter]::GetBytes($Arguments[$i]).CopyTo($buf, 4 + 4 * $i) }
    Write-Verbose ("REQ " + (($buf | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))

    $inst = Get-CimInstance -Namespace root/wmi -ClassName RW_GMWMI
    Set-CimInstance -InputObject $inst -Property @{ BufferBytes = $buf } | Out-Null
    $out = (Get-CimInstance -Namespace root/wmi -ClassName RW_GMWMI).BufferBytes

    [pscustomobject]@{
        Op   = $Op
        Code = '0x{0:X4}' -f $Code
        a0   = '0x{0:X4}' -f [BitConverter]::ToUInt16($out, 0)
        a1   = '0x{0:X4}' -f [BitConverter]::ToUInt16($out, 2)
        a2   = [BitConverter]::ToUInt32($out, 4)
        a3   = [BitConverter]::ToUInt32($out, 8)
        a4   = [BitConverter]::ToUInt32($out, 12)
        a5   = [BitConverter]::ToUInt32($out, 16)
        a6   = [BitConverter]::ToUInt32($out, 20)
    }
}

function Get-GmSensors {
    $r = Invoke-GmWmi -Op Get -Code 0x0200
    [pscustomobject]@{ CpuC = $r.a2; GpuC = $r.a3; CpuRpm = $r.a4; GpuRpm = $r.a5; KbAlpha = $r.a6 }
}

Export-ModuleMember -Function Invoke-GmWmi, Get-GmSensors
