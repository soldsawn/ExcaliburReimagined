# Read-only probe for the Excalibur EC gateway: root\wmi:RW_GMWMI.BufferBytes (32 bytes).
# Layout (little-endian): u16 a0 (0xFA00=GET, 0xFB00=SET) | u16 a1 (function code) | u32 a2..a6 | u32 rev0..rev1
# This script only ever sends a0=0xFA00 (GET). Requires an elevated shell.
param(
    [int[]]$Codes = @(0x0100, 0x0200, 0x0201, 0x0202, 0x0203, 0x0204, 0x0205, 0x0206, 0x0207, 0x0208, 0x0300)
)

function Invoke-GmwmiGet([uint16]$Code) {
    $buf = New-Object byte[] 32
    [BitConverter]::GetBytes([uint16]0xFA00).CopyTo($buf, 0)
    [BitConverter]::GetBytes($Code).CopyTo($buf, 2)

    $inst = Get-CimInstance -Namespace root/wmi -ClassName RW_GMWMI
    Set-CimInstance -InputObject $inst -Property @{ BufferBytes = $buf } | Out-Null
    $out = (Get-CimInstance -Namespace root/wmi -ClassName RW_GMWMI).BufferBytes

    [pscustomobject]@{
        Code = '0x{0:X4}' -f $Code
        a0   = '0x{0:X4}' -f [BitConverter]::ToUInt16($out, 0)
        a1   = '0x{0:X4}' -f [BitConverter]::ToUInt16($out, 2)
        a2   = [BitConverter]::ToUInt32($out, 4)
        a3   = [BitConverter]::ToUInt32($out, 8)
        a4   = [BitConverter]::ToUInt32($out, 12)
        a5   = [BitConverter]::ToUInt32($out, 16)
        a6   = [BitConverter]::ToUInt32($out, 20)
        Raw  = ($out | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    }
}

foreach ($c in $Codes) {
    try { Invoke-GmwmiGet ([uint16]$c) }
    catch { [pscustomobject]@{ Code = '0x{0:X4}' -f $c; a0 = 'ERR'; Raw = $_.Exception.Message } }
}
