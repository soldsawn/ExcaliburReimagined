using System.Text.RegularExpressions;
using Excalibur.Core.Fan;

namespace Excalibur.App;

/// <summary>
/// User-facing wording shared by the tray and the main window. Plain, everyday Turkish
/// (docs/09): say what happens for the user, not the mechanism (no EC, registers, "floor").
/// </summary>
internal static class Texts
{
    /// <summary>Why a fan runs at its current speed, in one short sentence.</summary>
    public static string Reason(FanReason reason, FanId fan) => reason switch
    {
        FanReason.Curve => "Profildeki eğriyi izliyor.",
        FanReason.Support => fan == FanId.Gpu
            ? "İşlemciye yardım ediyor: iki çip aynı ısı borularını paylaşıyor."
            : "Ekran kartına yardım ediyor: iki çip aynı ısı borularını paylaşıyor.",
        FanReason.Holding => "Sıcaklık düştü ama fan hemen yavaşlamıyor; sesi sürekli değişmesin diye hızını kademeli düşürüyor.",
        FanReason.SafetyFloor => "Sıcaklık yüksek; koruma gereği bu hızın altına inemez.",
        FanReason.Hot => "Çok sıcak; iki fan da tam hızda.",
        FanReason.Boost => "Fn+1 turbo açık; iki fan da tam hızda.",
        FanReason.Manual => "Elle ayarladığınız hızda.",
        _ => "Fanı bilgisayarın orijinal ayarı yönetiyor.",
    };

    /// <summary>The power mode as the user reads it (the number is the graphics card's power limit).</summary>
    public static string PowerMode(Excalibur.Core.Power.PowerMode mode) => mode switch
    {
        Excalibur.Core.Power.PowerMode.Office => "Ofis",
        Excalibur.Core.Power.PowerMode.Gaming => "Oyun",
        _ => "Yüksek",
    };

    public static string Chip(FanId fan) => fan == FanId.Cpu ? "İşlemci" : "Ekran kartı";

    /// <summary>A Windows System log entry the history marks.</summary>
    public static string SystemEvent(SystemEventKind kind) => kind == SystemEventKind.Shutdown ? "Beklenmedik kapanma" : "Donanım iletişim hatası";

    /// <summary>Who drives the fans, for status lines.</summary>
    public static string Driver(bool holdsFans) => holdsFans ? "Fanları uygulama yönetiyor" : "Fanları orijinal ayar yönetiyor";

    /// <summary>The fan turns about 54 rpm per percent (measured).</summary>
    public static int Rpm(int percent) => percent * 54;

    /// <summary>"3 gün 4 saat", "2 saat 5 dakika", "12 dakika", "birkaç saniye".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} gün {t.Hours} saat"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} saat {t.Minutes} dakika"
        : t.TotalMinutes >= 1 ? $"{t.Minutes} dakika"
        : "birkaç saniye";

    public static string Source(string? source) => source switch
    {
        "tray" => "tepsiden",
        "window" => "pencereden",
        "automation" => "otomatik",
        _ => "",
    };

    /// <summary>
    /// The service's event lines (English, for the log file) as the user should read them.
    /// Unknown lines pass through unchanged.
    /// </summary>
    public static string Event(string message)
    {
        Match m;
        if ((m = Regex.Match(message, @"^started, EC \S+, profile (.+)$")).Success)
            return $"Fan kontrolü başladı: {Tray.ProfileNames.Label(m.Groups[1].Value)}";
        if (message == "fans handed to the EC (start)")
            return "Başlarken fanlar önce orijinal ayara alındı";
        if (message.StartsWith("EC firmware", StringComparison.Ordinal))
            return "Bu dizüstünün fan yazılımı tanınmadı; fanlar yalnız izleniyor";
        if ((m = Regex.Match(message, @"^profile (.+?)(?: \((automation|tray|window|service)\))?$")).Success)
        {
            string from = Source(m.Groups[2].Value);
            return $"Profil: {Tray.ProfileNames.Label(m.Groups[1].Value)}" + (from.Length > 0 ? $" ({from})" : "");
        }
        if ((m = Regex.Match(message, @"^manual CPU (\d+)% GPU (\d+)% until (\d\d:\d\d)")).Success)
            return $"Manuel: işlemci %{m.Groups[1].Value}, ekran kartı %{m.Groups[2].Value}, {m.Groups[3].Value}'e kadar";
        if ((m = Regex.Match(message, @"^manual mode ended, back to (.+)$")).Success)
            return $"Manuel süre bitti, {Tray.ProfileNames.Label(m.Groups[1].Value)} profiline dönüldü";
        if ((m = Regex.Match(message, @"^custom profile saved: (.+)$")).Success)
            return $"Profil kaydedildi: {m.Groups[1].Value}";
        if ((m = Regex.Match(message, @"^custom profile deleted: (.+)$")).Success)
            return $"Profil silindi: {m.Groups[1].Value}";
        if (message == "fans written through WMI 0x0205")
            return "Fanlar BIOS üzerinden yönetiliyor";
        if (message.StartsWith("EC trial: the fans do not follow WMI", StringComparison.Ordinal))
            return "Deneme: fanlar BIOS üzerinden ayarlanamadı, doğrudan deneniyor";
        if (message.StartsWith("EC trial: both fans follow WMI", StringComparison.Ordinal))
            return "Deneme başarılı: fanlar BIOS üzerinden yönetilecek";
        if (message == "safety: released")
            return "Sıcaklık normale döndü";
        if (message.StartsWith("safety:", StringComparison.Ordinal))
            return message.Contains(", hot)") ? "Çok ısındı: iki fan da tam hıza çıktı" : "Isındı: fanlar güvenlik için hızlandı";
        if (message.StartsWith("FAULT:", StringComparison.Ordinal))
            return "Sorun: fan bilgileri güvenle okunamadı, fanlar orijinal ayara bırakıldı";
        if (message == "fault cleared")
            return "Sorun giderildi, fan kontrolü yeniden başladı";
        if (message.Contains("did not read back", StringComparison.Ordinal))
            return "Fan hızı ayarlanamadı, yeniden deneniyor";
        if (message.StartsWith("!!", StringComparison.Ordinal))
            return "Uyarı: fanlar orijinal ayara bırakılamadı; koruma hizmeti yeniden deneyecek";
        return message;
    }
}
