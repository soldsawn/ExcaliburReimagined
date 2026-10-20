# READ-ONLY dump of the ACPI Embedded Controller RAM (0x00-0xFF) through PawnIO + LpcACPIEC.
# Uses the standard ACPI EC protocol: RD_EC (0x80) on command port 0x66, data on 0x62.
# This script deliberately contains no EC write path. Requires an elevated shell.
param(
    [int]$Samples = 3,
    [int]$IntervalMs = 1000,
    [string]$Label = 'dump',
    [string]$OutDir = "$PSScriptRoot\..\..\analysis\ec"
)

$ErrorActionPreference = 'Stop'
$module = "$PSScriptRoot\..\pawnio-modules\LpcACPIEC.bin"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public static class EcReader
{
    [DllImport(@"C:\Program Files\PawnIO\PawnIOLib.dll")] static extern int pawnio_open(out IntPtr h);
    [DllImport(@"C:\Program Files\PawnIO\PawnIOLib.dll")] static extern int pawnio_load(IntPtr h, byte[] blob, UIntPtr size);
    [DllImport(@"C:\Program Files\PawnIO\PawnIOLib.dll")] static extern int pawnio_execute(IntPtr h, string name, ulong[] inp, UIntPtr inSize, ulong[] outp, UIntPtr outSize, out UIntPtr ret);
    [DllImport(@"C:\Program Files\PawnIO\PawnIOLib.dll")] static extern int pawnio_close(IntPtr h);

    const ushort DATA = 0x62, CMD = 0x66;
    const byte OBF = 0x01, IBF = 0x02, RD_EC = 0x80;
    static IntPtr h;

    public static void Open(byte[] blob)
    {
        Check(pawnio_open(out h), "pawnio_open");
        Check(pawnio_load(h, blob, (UIntPtr)blob.Length), "pawnio_load");
    }
    public static void Close() { if (h != IntPtr.Zero) pawnio_close(h); h = IntPtr.Zero; }

    static void Check(int h