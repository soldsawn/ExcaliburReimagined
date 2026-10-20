namespace Excalibur.Core.Fan;

public enum FanId { Cpu, Gpu }

/// <summary>
/// A fan target as the EC stores it in 0x2C (CPU) / 0x2D (GPU): a percent 0-100, or
/// 0xFF meaning "EC automatic" (the stock curve). See docs/06 phase 3.7.
/// </summary>
public readonly record struct FanTarget
{
    public const byte AutoValue = 0xFF;

    public byte Value { get; }

    private FanTarget(byte value) => Value = value;

    public static FanTarget Auto { get; } = new(AutoValue);

    public bool IsAuto => Value == AutoValue;

    /// <summary>The percent; only meaningful when <see cref="IsAuto"/> is false.</summary>
    public int Percent => IsAuto ? throw new InvalidOperationException("Auto has no percent.") : Value;

    public static FanTarget FromPercent(int percent)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Fan percent must be 0-100.");
        return new FanTarget((byte)percent);
    }

    /// <summary>Interprets a raw register byte; null when it is neither 0-100 nor 0xFF (a corrupted read).</summary>
    public static FanTarget? FromRaw(byte raw) => raw is <= 100 or AutoValue ? new FanTarget(raw) : null;

    public override string ToString() => IsAuto ? "Auto" : $"{Value}%";
}
