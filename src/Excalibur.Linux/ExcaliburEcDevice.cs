using System.Runtime.InteropServices;
using Excalibur.Core.Hardware;
using Microsoft.Win32.SafeHandles;

namespace Excalibur.Linux;

/// <summary>
/// Our kernel module's device (/dev/excalibur_ec, tools/linux/excalibur-ec): EC reads, the two fan target writes, and the
/// BIOS's own 32-byte WMI command (set = WSAA, query = WQAA), as the Windows app sends it through RW_GMWMI. The ioctl
/// numbers match excalibur_ec.h.
/// </summary>
internal sealed class ExcaliburEcDevice : IDisposable
{
    public const string DevicePath = "/dev/excalibur_ec";

    private const nuint IocVersion = 0x80047800, IocEcRead = 0xC0027801, IocEcWrite = 0x40027802, IocWmiCall = 0xC0207803;
    private const int AbiVersion = 1;

    private readonly SafeFileHandle _handle;

    private ExcaliburEcDevice(SafeFileHandle handle) => _handle = handle;

    /// <summary>The device when the module is loaded and speaks our version; null otherwise (with the reason).</summary>
    public static ExcaliburEcDevice? TryOpen(out string? problem)
    {
        problem = null;
        if (!File.Exists(DevicePath)) return null;
        SafeFileHandle handle;
        try { handle = File.OpenHandle(DevicePath, FileMode.Open, FileAccess.ReadWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{DevicePath} açılamadı ({ex.Message}). Komutu sudo ile çalıştırın.";
            return null;
        }
        var device = new ExcaliburEcDevice(handle);
        var version = new byte[4];
        if (device.Ioctl(IocVersion, version) != 0 || BitConverter.ToInt32(version) != AbiVersion)
        {
            device.Dispose();
            problem = "excalibur_ec modülünün sürümü bu uygulamayla uyuşmuyor; paketi yeniden kurun.";
            return null;
        }
        return device;
    }

    public byte Read(byte address)
    {
        var reg = new[] { address, (byte)0 };
        if (Ioctl(IocEcRead, reg) is var err and not 0) throw new IOException($"EC 0x{address:X2} could not be read (errno {err}).");
        return reg[1];
    }

    /// <summary>Only the fan targets (0x2C/0x2D); the module refuses any other register.</summary>
    public void Write(byte address, byte value)
    {
        if (Ioctl(IocEcWrite, [address, value]) is var err and not 0)
            throw new IOException($"EC 0x{address:X2} could not be written (errno {err}).");
    }

    /// <summary>One BIOS command; the reply is what the BIOS left in its block.</summary>
    public SmiCommand Call(SmiCommand request)
    {
        var buffer = request.ToBytes();
        if (Ioctl(IocWmiCall, buffer) is var err and not 0)
            throw new IOException($"BIOS command 0x{request.A0:X4}/0x{request.A1:X4} failed (errno {err}).");
        return SmiCommand.FromBytes(buffer);
    }

    public void Dispose() => _handle.Dispose();

    private int Ioctl(nuint request, byte[] data)
    {
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            return ioctl((int)_handle.DangerousGetHandle(), request, data) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (added) _handle.DangerousRelease();
        }
    }

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, byte[] data);
}

/// <summary>The BIOS's commands through the module (keyboard light, power mode, sensors), as on Windows.</summary>
internal sealed class ModuleSmiChannel(ExcaliburEcDevice device) : ISmiChannel
{
    public SmiCommand Execute(SmiCommand request) => device.Call(request);
}
