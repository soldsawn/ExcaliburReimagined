namespace Excalibur.Linux;

/// <summary>
/// The EC's ACPI space: through our excalibur_ec module when it is loaded (<see cref="ExcaliburEcDevice"/>; works with
/// Secure Boot, writes only the fan targets), else through the kernel's ec_sys module (/sys/kernel/debug/ec/ec0/io,
/// 256 bytes; writes need the module's write_support=1). The kernel's EC driver queues every access either way, so our
/// reads and writes never collide with its own (the ACPI 13/15 warnings of Windows have no counterpart here) and reading
/// back is safe.
/// </summary>
internal sealed class LinuxEc : IDisposable
{
    public const string IoPath = "/sys/kernel/debug/ec/ec0/io";
    public const string WriteSupportPath = "/sys/module/ec_sys/parameters/write_support";
    public const string LockdownPath = "/sys/kernel/security/lockdown";

    private readonly FileStream? _io;
    private readonly object _gate = new();

    private LinuxEc(FileStream io) => _io = io;
    private LinuxEc(ExcaliburEcDevice module) => Module = module;

    /// <summary>Our kernel module, when the EC goes through it: the BIOS's own commands are available too.</summary>
    public ExcaliburEcDevice? Module { get; }

    /// <summary>Opens the EC for reading and writing; the reason in Turkish when it cannot.</summary>
    public static LinuxEc? Open(out string? problem)
    {
        if (ExcaliburEcDevice.TryOpen(out problem) is { } module) return new LinuxEc(module);
        if (problem is not null) return null;
        problem = Problem();
        if (problem is not null) return null;
        try
        {
            return new LinuxEc(new FileStream(IoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"EC açılamadı ({ex.Message}). Komutu sudo ile çalıştırın.";
            return null;
        }
    }

    /// <summary>Why the EC cannot be written, or null: Secure Boot's lockdown, the module, its write switch.</summary>
    public static string? Problem()
    {
        // The tar.gz install has no module: only turning Secure Boot off helps there.
        if (LockdownActive() && !Directory.Exists("/var/lib/dkms/excalibur-ec"))
            return "Secure Boot açık (çekirdek kilitli): Linux bu modda EC'ye yazmaya izin vermiyor. .deb paketini kurun "
                + "(Secure Boot'la çalışan modülü içerir) ya da BIOS'ta (açılışta F2 > Security) Secure Boot'u kapatın.";
        if (LockdownActive())
            return "Secure Boot açık ve uygulamanın modülü yüklü değil. Modülün anahtarını bir kez onaylayın: terminalde "
                + "'sudo mokutil --import /var/lib/shim-signed/mok/MOK.der' yazıp bir parola belirleyin (yazarken ekranda "
                + "hiçbir şey görünmez, aynı parolayı iki kez yazın), bilgisayarı yeniden başlatın, mavi ekranda "
                + "Enroll MOK > Continue > Yes seçip parolayı girin. Ya da BIOS'ta (açılışta F2 > Security) Secure Boot'u kapatın.";
        if (!File.Exists(IoPath))
            return "ec_sys modülü yüklü değil. Kurulum betiği yükler; elle: sudo modprobe ec_sys write_support=1";
        if (File.Exists(WriteSupportPath) && File.ReadAllText(WriteSupportPath).Trim() is not ("Y" or "1"))
            return "ec_sys yazma kapalı yüklenmiş. Düzeltmek için: sudo modprobe -r ec_sys && sudo modprobe ec_sys write_support=1";
        return null;
    }

    /// <summary>True when the kernel runs locked down (Secure Boot): "[none]" means not.</summary>
    public static bool LockdownActive()
    {
        try { return File.Exists(LockdownPath) && !File.ReadAllText(LockdownPath).Contains("[none]"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public byte Read(byte address)
    {
        if (Module is not null) return Module.Read(address);
        lock (_gate)
        {
            _io!.Seek(address, SeekOrigin.Begin);
            int b = _io.ReadByte();
            if (b < 0) throw new IOException($"EC 0x{address:X2} could not be read.");
            return (byte)b;
        }
    }

    public void Write(byte address, byte value)
    {
        if (Module is not null)
        {
            Module.Write(address, value);
            return;
        }
        lock (_gate)
        {
            _io!.Seek(address, SeekOrigin.Begin);
            _io.WriteByte(value);
            _io.Flush();
        }
    }

    public void Dispose()
    {
        _io?.Dispose();
        Module?.Dispose();
    }
}
