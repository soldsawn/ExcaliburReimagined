using Excalibur.Core.Power;

namespace Excalibur.Core.Battery;

/// <summary>The average power over one finished interval on battery.</summary>
public sealed record BatteryAverage(DateTimeOffset At, TimeSpan Span, double Watts);

/// <summary>A kept interval: the average, the battery percent at its end, and the power mode, fan profile and brightness it ran with.</summary>
public sealed record BatteryInterval(BatteryAverage Average, int? Percent, PowerMode? Mode, string Profile, int? Brightness);

/// <summary>One reading at the start or end of an interval.</summary>
public readonly record struct BatteryReading(int RemainingMWh, int? Brightness);

/// <summary>What the slow battery reads need from the hardware; both may take a moment (WMI).</summary>
public interface IBatteryMeter
{
    /// <summary>Energy left in the battery (root\wmi BatteryStatus.RemainingCapacity); null when unreadable.</summary>
    BatteryReading? Read();
}

/// <summary>
/// The average power on battery, from as few battery reads as possible (the battery answers through the EC): the energy left
/// is read at the start and end of an interval (<see cref="Interval"/>, 10 min by default) and the drop over the time is
/// the average. Nothing is read on the wall adapter. The first read waits <see cref="FirstReadDelay"/> after the charger
/// comes out (the EC is busy then) and after sleep. An interval is dropped when the power mode or fan profile changed in
/// it, the screen brightness differs by 10 points or more between its ends, or the laptop slept in it; a change never
/// causes an extra read. Each kept interval goes to <see cref="Recorded"/> (the csv log).
/// </summary>
public sealed class BatteryRecorder(IBatteryMeter meter)
{
    public static readonly TimeSpan FirstReadDelay = TimeSpan.FromMinutes(1);
    /// <summary>The choices the settings offer, in minutes.</summary>
    public static readonly IReadOnlyList<int> IntervalChoices = [2, 5, 10, 30];
    public const int DefaultMinutes = 10;
    /// <summary>A pause between ticks this long means the laptop slept.</summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(60);
    private const int BrightnessStep = 10;

    private sealed record Start(DateTimeOffset At, BatteryReading Reading, PowerMode? Mode, string Profile);

    private Start? _start;
    private DateTimeOffset? _firstAt, _lastTick;
    private bool _changed;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(DefaultMinutes);

    /// <summary>The last kept interval of this time on battery; null on the wall adapter and until one finishes.</summary>
    public BatteryAverage? Last { get; private set; }

    public event Action<BatteryInterval>? Recorded;

    /// <summary>Called every few seconds. <paramref name="ecBusy"/> postpones a due read (the EC is held quiet).</summary>
    public void Tick(DateTimeOffset now, BatteryLive? live, PowerMode? mode, string profile, bool ecBusy)
    {
        bool slept = _lastTick is { } last && now - last >= SleepGap;
        _lastTick = now;
        if (live is not { State: BatteryState.OnBattery })
        {
            _start = null;
            _firstAt = null;
            Last = null;
            return;
        }
        if (slept)
        {
            _start = null;
            _firstAt = now + FirstReadDelay;
        }

        if (_start is not { } start)
        {
            _firstAt ??= now + FirstReadDelay;
            if (now >= _firstAt && !ecBusy) Begin(now, mode, profile);
            return;
        }
        if (start.Mode != mode || start.Profile != profile) _changed = true;
        if (now - start.At < Interval || ecBusy) return;

        var end = meter.Read();
        if (end is not { } e)
        {
            // Unreadable: start over one interval later rather than asking again at every tick.
            _start = null;
            _firstAt = now + Interval;
            return;
        }
        int drop = start.Reading.RemainingMWh - e.RemainingMWh;
        bool brightnessMoved = start.Reading.Brightness is { } b0 && e.Brightness is { } b1 && Math.Abs(b1 - b0) >= BrightnessStep;
        if (!_changed && !brightnessMoved && drop >= 0)
        {
            var span = now - start.At;
            Last = new BatteryAverage(now, span, drop / 1000.0 / span.TotalHours);
            Recorded?.Invoke(new BatteryInterval(Last, live.Value.Percent, start.Mode, start.Profile, e.Brightness));
        }
        // The end of one interval is the start of the next.
        _start = new Start(now, e, mode, profile);
        _changed = false;
    }

    private void Begin(DateTimeOffset now, PowerMode? mode, string profile)
    {
        if (meter.Read() is { } r)
        {
            _start = new Start(now, r, mode, profile);
            _changed = false;
        }
        else _firstAt = now + Interval;
    }
}
