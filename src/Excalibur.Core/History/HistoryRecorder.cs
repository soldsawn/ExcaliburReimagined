using System.Globalization;
using System.IO.Compression;
using Excalibur.Core.Fan;
using Excalibur.Core.Settings;

namespace Excalibur.Core.History;

/// <summary>
/// Keeps the fan history the service records once a second (docs/09 A2):
/// <list type="bullet">
///   <item>the last hour always in memory (a ring), so the UI's recent trail never touches the disk;</item>
///   <item>in <see cref="HistoryMode.Days7"/> also on disk: one file per UTC day, <c>yyyyMMdd.bin</c> while
///   it is written (buffered, flushed every <see cref="FlushEvery"/> samples), gzipped to <c>.bin.gz</c>
///   once the day is over, deleted after <see cref="Retention"/>.</item>
/// </list>
/// Disk problems are reported through the log callback and never thrown: history must not take the
/// fan loop down. Thread-safe.
/// </summary>
public sealed class HistoryRecorder : IDisposable
{
    public const int RingSeconds = 3600;
    public const int FlushEvery = 30;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly byte[] Header = "EXRH"u8.ToArray().Concat(new byte[] { 1, HistorySample.Size, 0, 0 }).ToArray();

    private readonly string _dir;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly HistorySample[] _ring = new HistorySample[RingSeconds];
    private int _ringStart, _ringCount;
    private HistoryMode _mode;
    private FileStream? _file;
    private DateTime _fileDay;
    private int _unflushed;
    private bool _diskFailed;
    private readonly byte[] _record = new byte[HistorySample.Size];

    public HistoryRecorder(string directory, HistoryMode mode, Action<string>? log = null)
    {
        _dir = directory;
        _log = log;
        _mode = mode;
        if (mode == HistoryMode.Days7) Maintain(DateTime.UtcNow);
    }

    public HistoryMode Mode
    {
        get { lock (_gate) return _mode; }
        set
        {
            lock (_gate)
            {
                if (value == _mode) return;
                _mode = value;
                if (value != HistoryMode.Days7) CloseFile();
                if (value == HistoryMode.Off) _ringCount = 0;
                _diskFailed = false;
            }
            if (value == HistoryMode.Days7) Maintain(DateTime.UtcNow);
        }
    }

    public void Record(FanStatus status, DateTimeOffset now)
    {
        if (HistorySample.From(status, now.UtcDateTime) is { } sample) Record(sample);
    }

    public void Record(HistorySample sample)
    {
        DateTime? closedDay = null;
        lock (_gate)
        {
            if (_mode == HistoryMode.Off) return;
            // One sample per second; a second tick in the same second replaces nothing, it is skipped.
            if (_ringCount > 0 && Last().AtUtc >= sample.AtUtc) return;
            _ring[(_ringStart + _ringCount) % RingSeconds] = sample;
            if (_ringCount < RingSeconds) _ringCount++;
            else _ringStart = (_ringStart + 1) % RingSeconds;

            if (_mode != HistoryMode.Days7 || _diskFailed) return;
            try
            {
                if (_file is null || _fileDay != sample.AtUtc.Date)
                {
                    if (_file is not null) closedDay = _fileDay;
                    OpenFile(sample.AtUtc.Date);
                }
                sample.Write(_record);
                _file!.Write(_record);
                if (++_unflushed >= FlushEvery) Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diskFailed = true; // retried after a mode change or a restart
                CloseFile();
                _log?.Invoke($"history: recording to disk stopped ({ex.Message}); the last hour is still kept in memory");
            }
        }
        if (closedDay is not null) Task.Run(() => Maintain(sample.AtUtc));
    }

    /// <summary>Samples in [from, to), oldest first: the ring for the last hour, the files before it.</summary>
    public List<HistorySample> Samples(DateTime fromUtc, DateTime toUtc)
    {
        List<HistorySample> recent;
        DateTime ringFrom;
        HistoryMode mode;
        lock (_gate)
        {
            Flush();
            mode = _mode;
            recent = Enumerable.Range(0, _ringCount).Select(i => _ring[(_ringStart + i) % RingSeconds])
                .Where(s => s.AtUtc >= fromUtc && s.AtUtc < toUtc).ToList();
            ringFrom = _ringCount > 0 ? _ring[_ringStart].AtUtc : DateTime.MaxValue;
        }
        if (mode != HistoryMode.Days7 || fromUtc >= ringFrom) return recent;

        var older = new List<HistorySample>();
        DateTime diskTo = toUtc < ringFrom ? toUtc : ringFrom;
        for (var day = fromUtc.Date; day <= diskTo.Date; day = day.AddDays(1))
            older.AddRange(ReadDay(day).Where(s => s.AtUtc >= fromUtc && s.AtUtc < diskTo));
        older.AddRange(recent);
        return older;
    }

    public HistorySeries Query(DateTime fromUtc, DateTime toUtc, int maxPoints) =>
        HistorySeries.Reduce(Samples(fromUtc, toUtc), fromUtc, toUtc, maxPoints);

    public void Flush()
    {
        lock (_gate)
        {
            if (_file is null || _unflushed == 0) return;
            try { _file.Flush(); }
            catch (IOException ex) { _log?.Invoke($"history: flush failed ({ex.Message})"); }
            _unflushed = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate) CloseFile();
    }

    private HistorySample Last() => _ring[(_ringStart + _ringCount - 1) % RingSeconds];

    private string RawPath(DateTime day) => Path.Combine(_dir, day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".bin");

    private void OpenFile(DateTime day)
    {
        CloseFile();
        Directory.CreateDirectory(_dir);
        string path = RawPath(day);
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096);
        if (file.Length < Header.Length)
        {
            file.SetLength(0);
            file.Write(Header);
        }
        else
        {
            // A crash can leave half a record at the end; cut it off so the next one lines up.
            long whole = Header.Length + (file.Length - Header.Length) / HistorySample.Size * HistorySample.Size;
            if (whole != file.Length) file.SetLength(whole);
        }
        file.Seek(0, SeekOrigin.End);
        _file = file;
        _fileDay = day;
    }

    private void CloseFile()
    {
        if (_file is null) return;
        try { _file.Dispose(); }
        catch (IOException) { }
        _file = null;
        _unflushed = 0;
    }

    /// <summary>Compresses finished days and deletes those past the retention. Runs off the fan loop.</summary>
    private void Maintain(DateTime nowUtc)
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (string path in Directory.EnumerateFiles(_dir, "*.bin*"))
            {
                if (DayOf(path) is not { } day) continue;
                if (nowUtc.Date - day > Retention)
                {
                    File.Delete(path);
                }
                else if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && day < nowUtc.Date)
                {
                    using (var src = File.OpenRead(path))
                    using (var dst = File.Create(path + ".gz.tmp"))
                    using (var gz = new GZipStream(dst, CompressionLevel.SmallestSize))
                        src.CopyTo(gz);
                    File.Move(path + ".gz.tmp", path + ".gz", overwrite: true);
                    File.Delete(path);
                }
            }
        }
        // Any failure: it also runs on a fire-and-forget task, where nothing would observe it.
        catch (Exception ex)
        {
            _log?.Invoke($"history: maintenance failed ({ex.Message})");
        }
    }

    private List<HistorySample> ReadDay(DateTime day)
    {
        var result = new List<HistorySample>();
        string raw = RawPath(day);
        try
        {
            Stream? stream = File.Exists(raw) ? new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : File.Exists(raw + ".gz") ? new GZipStream(File.OpenRead(raw + ".gz"), CompressionMode.Decompress)
                : null;
            if (stream is null) return result;
            using (stream)
            {
                Span<byte> header = stackalloc byte[8];
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.SequenceEqual(Header))
                    return result;
                var record = new byte[HistorySample.Size];
                while (stream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false) == record.Length)
                    result.Add(HistorySample.Read(record, day));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log?.Invoke($"history: could not read {day:yyyy-MM-dd} ({ex.Message})");
        }
        return result;
    }

    private static DateTime? DayOf(string path)
    {
        string name = Path.GetFileName(path);
        return name.Length >= 8 && DateTime.TryParseExact(name[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) ? day : null;
    }
}
