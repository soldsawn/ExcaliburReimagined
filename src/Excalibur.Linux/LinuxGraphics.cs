using System.Diagnostics;
using Excalibur.Core.Graphics;

namespace Excalibur.Linux;

/// <summary>
/// What a graphics mode change needs from Linux. Only Hybrid and Discrete are offered here: both are a BIOS setting that
/// starts with the next boot. Integrated would need the NVIDIA GPU switched off while the system runs, which Linux cannot
/// do (the switcher refuses it), so the GPU is always on as far as the switcher can tell.
/// </summary>
internal sealed class LinuxGpuDevices : IGpuDevices
{
    public bool? Enabled => true;
    public void Disable() => throw new GraphicsException("graphics_unsupported", "The NVIDIA GPU cannot be switched off on Linux.");
    public void Enable() { }
}

internal static class LinuxGraphicsSystem
{
    /// <summary>
    /// True when any partition on this computer is BitLocker-encrypted (lsblk's FSTYPE): a Windows next to Linux would ask for
    /// its recovery key after the firmware change, as it would when the change is made from Windows. A failed lsblk counts as
    /// none and is logged.
    /// </summary>
    public static bool BitLockerOn(Action<string> log)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("lsblk", "-rno FSTYPE") { RedirectStandardOutput = true, UseShellExecute = false })!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Split('\n').Any(t => t.Trim().Equals("BitLocker", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"graphics: BitLocker partitions not looked for ({ex.Message}); taken as none");
            return false;
        }
    }

    /// <summary>Restarts the computer as on Windows: 3 s, then systemctl reboot (the user confirmed and was told to save).</summary>
    public static void Restart(Action<string> log)
    {
        log("graphics: restarting in 3 s");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl", "reboot") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GraphicsException("graphics_restart", $"The computer could not be restarted: {ex.Message}", ex);
        }
    }
}

/// <summary>The graphics mode as the window and "exr gpu" word it.</summary>
internal static class GraphicsTexts
{
    public const string RestartNote = "Bilgisayar birkaç saniye içinde yeniden başlayacak; açık işlerinizi şimdi kaydedin. Bu açılış normalden " +
        "uzun sürer (1-2 dakika): bilgisayar bir kez kapanıp açılabilir, güç ışığı yanıp sönebilir. Bir süre görüntü gelmezse güç düğmesine basın.";
    public const string DiscreteNote = "Harici modda NVIDIA kartı hep açık olur: oyunlarda en yüksek hız, ama pil daha kısa gider ve fan daha sık " +
        "çalışabilir. Bu mod Linux'ta henüz denenmedi: açılışta masaüstü gelmezse Windows'u açıp uygulamadan Hibrit'i seçin.";

    public static string Name(GraphicsMode mode) => mode switch
    {
        GraphicsMode.Integrated => "Dahili (yalnız Intel, Windows'ta seçildi)",
        GraphicsMode.Discrete => "Harici (yalnız NVIDIA)",
        _ => "Hibrit (iki ekran kartı)",
    };

    public static string Message(string? code, string? error) => code switch
    {
        "graphics_busy" => "Bir mod değişikliği zaten sürüyor.",
        "graphics_unsupported" => "Bu mod Linux'ta kullanılamıyor.",
        "graphics_bitlocker" => "Bu bilgisayarda BitLocker'lı bir bölüm var: değişiklik Windows açılışında BitLocker kurtarma anahtarını sordurur. " +
            "Önce Windows'ta BitLocker'ı kapatın.",
        "graphics_firmware" => "BIOS mod değişikliğini kabul etmedi (paketi yeniden kurup bilgisayarı yeniden başlatmak gerekebilir).",
        "graphics_restart" => "Mod kaydedildi ama bilgisayar yeniden başlatılamadı; kendiniz yeniden başlatın.",
        "graphics_restart_pending" => "Başka bir mod yeniden başlatmayı bekliyor; önce bilgisayarı yeniden başlatın.",
        _ => error is null ? "Mod değiştirilemedi." : $"Mod değiştirilemedi ({error}).",
    };
}
