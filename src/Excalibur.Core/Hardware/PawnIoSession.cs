using System.Runtime.InteropServices;

namespace Excalibur.Core.Hardware;

/// <summary>
/// Managed wrapper around the signed PawnIO driver (https://pawnio.eu) via
/// <c>PawnIOLib.dll</c>. A session loads one compiled module blob (e.g. LpcACPIEC)
/// and calls its exported ioctl functions. Requires PawnIO to be installed and the
/// process to run elevated. Disposing closes the kernel handle.
/// </summary>
public sealed class PawnIoSession : IDisposable
{
    private const string Lib = @"C:\Program Files\PawnIO\PawnIOLib.dll";

    [DllImport(Lib)] private static extern int pawnio_open(out IntPtr handle);
    [DllImport(Lib)] private static extern int pawnio_load(IntPtr handle, byte[] blob, nuint size);
    [DllImport(Lib)] private static extern int pawnio_execute(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inSize, ulong[] output, nuint outSize, out nuint returnSize);
    [DllImport(Lib)] private static extern int pawnio_close(IntPtr handle);

    private IntPtr _handle;

    private PawnIoSession(IntPtr handle) => _handle = handle;

    /// <summary>Opens a session and loads a compiled module blob (a *.bin file's bytes).</summary>
    public static PawnIoSession Load(byte[] moduleBlob)
    {
        ThrowIf(pawnio_open(out var handle), nameof(pawnio_open));
        try
        {
            ThrowIf(pawnio_load(handle, moduleBlob, (nuint)moduleBlob.Length), nameof(pawnio_load));
        }
        catch
        {
            pawnio_close(handle);
            throw;
        }
        return new PawnIoSession(handle);
    }

    /// <summary>Calls one ioctl exported by the loaded module.</summary>
    public ulong[] Execute(string function, ulong[] input, int outputCount)
    {
        var output = new ulong[Math.Max(outputCount, 1)];
        ThrowIf(pawnio_execute(_handle, function, input, (nuint)input.Length,
            output, (nuint)outputCount, out var written), function);
        return output[..(int)written];
    }

    private static void ThrowIf(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"PawnIO call '{what}' failed (HRESULT 0x{hr:X8}).");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            pawnio_close(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
