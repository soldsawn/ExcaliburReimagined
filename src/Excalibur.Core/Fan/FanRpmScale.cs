using System.Globalization;

namespace Excalibur.Core.Fan;

/// <summary>
/// RPM per target percent of each fan, which differs between models: the G870 (EC P008B0) drives the CPU fan at
/// 54.5 RPM per percent and the GPU fan at 52.5, a tester's G770 (EC P003K0, 2026-10-23 history) at 58.0 and 59.0 on
/// every level from 25% to 100%. A wrong scale showed fans 7-10 points faster than their target ("!!" warnings,
/// the curve point above the curve, a laxer guardian). Learnt while running: once a target between
/// <see cref="MinLearnPercent"/> and <see cref="MaxLearnPercent"/> has stood <see cref="Steady"/>, each reading moves
/// the scale by <see cref="Weight"/> toward RPM / target (the G870 saturates near 100%, so the top is left out).
/// The scale is kept in a file with the EC version, which the guardian reads too.
/// </summary>
public sealed class FanRpmScale
{
    public const string FileName = "fan-rpm-scale.txt";
    public const double DefaultCpu = 54.5, DefaultGpu = 52.5;
    public const int MinLearnPercent = 30, MaxLearnPercent = 90;
    public const double Weight = 0.05;
    public static readonly TimeSpan Steady = TimeSpan.FromSeconds(8);
    private const double MinScale = 40, MaxScale = 80, SaveDelta = 0.3;
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromSeconds(60);

    /// <summary>Measured scales by EC version, the start before anything is learnt on this machine.</summary>
    public static IReadOnlyDictionary<string, (double Cpu, double Gpu)> Known { get; } = new Dictionary<string, (double, double)>
    {
        ["P008B0"] = (54.5, 52.5),
        ["P003K0"] = (58.0, 59.0),
    };

    private readonly string? _path;
    private readonly double[] _scale = [DefaultCpu, DefaultGpu];
    private readonly double[] _saved = [DefaultCpu, DefaultGpu];
    private readonly (FanTarget Target, TimeSpan Since)?[] _steady = new (FanTarget, TimeSpan)?[2];
    private string? _ecVersion;
    private TimeSpan? _loadedAt;

    /// <param name="path">Where the scale is kept; null keeps it in memory only.</param>
    public FanRpmScale(string? path = null)
    {
        _path = path;
        if (Load() is { } saved) Set(saved.Cpu, saved.Gpu);
    }

    public double Of(FanId fan) => _scale[(int)fan];

    /// <summary>The percent a fan runs at, from its RPM; clamped to 0..100.</summary>
    public int PercentOf(FanId fan, int rpm) => Math.Clamp((int)Math.Round(rpm / Of(fan)), 0, 100);

    /// <summary>
    /// The EC version is known (the service, at start): a scale saved under it is kept, else the known one or the
    /// default, saved at once so the guardian has it too.
    /// </summary>
    public void UseEcVersion(string? ecVersion)
    {
        _ecVersion = ecVersion;
        if (Load() is { } saved && saved.Version == ecVersion) Set(saved.Cpu, saved.Gpu);
        else
        {
            var (cpu, gpu) = ecVersion is not null && Known.TryGetValue(ecVersion, out var known) ? known : (DefaultCpu, DefaultGpu);
            Set(cpu, gpu);
            Save();
        }
    }

    /// <summary>One reading of a fan running at <paramref name="target"/> (the service, every read).</summary>
    public void Observe(FanId fan, FanTarget target, int rpm, bool boost, TimeSpan now)
    {
        int i = (int)fan;
        if (boost || target.IsAuto || target.Percent is < MinLearnPercent or > MaxLearnPercent)
        {
            _steady[i] = null;
            return;
        }
        if (_steady[i] is not { } steady || steady.Target != target)
        {
            _steady[i] = (target, now);
            return;
        }
        if (now - steady.Since < Steady) return;
        double sample = (double)rpm / target.Percent;
        if (sample is < MinScale or > MaxScale) return;
        _scale[i] += Weight * (sample - _scale[i]);
        if (Math.Abs(_scale[i] - _saved[i]) >= SaveDelta) Save();
    }

    /// <summary>Reads the file again at most every minute (the guardian, which learns nothing itself).</summary>
    public void Refresh(TimeSpan now)
    {
        if (_loadedAt is { } at && now - at < ReloadEvery) return;
        _loadedAt = now;
        if (Load() is { } saved) Set(saved.Cpu, saved.Gpu);
    }

    private void Set(double cpu, double gpu)
    {
        (_scale[0], _scale[1]) = (cpu, gpu);
        (_saved[0], _saved[1]) = (cpu, gpu);
    }

    private (string Version, double Cpu, double Gpu)? Load()
    {
        if (_path is null) return null;
        try
        {
            var parts = File.ReadAllText(_path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is [var version, var c, var g]
                && double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out double cpu) && cpu is >= MinScale and <= MaxScale
                && double.TryParse(g, NumberStyles.Float, CultureInfo.InvariantCulture, out double gpu) && gpu is >= MinScale and <= MaxScale)
                return (version, cpu, gpu);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private void Save()
    {
        (_saved[0], _saved[1]) = (_scale[0], _scale[1]);
        if (_path is null || _ecVersion is null) return;
        try { File.WriteAllText(_path, string.Create(CultureInfo.InvariantCulture, $"{_ecVersion} {_scale[0]:F1} {_scale[1]:F1}")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
