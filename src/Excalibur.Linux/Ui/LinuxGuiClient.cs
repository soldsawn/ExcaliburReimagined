using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Excalibur.Linux.Ui;

/// <summary>
/// Talks to the Linux daemon over the Unix control socket and keeps the latest <see cref="LinuxStatus"/> for the Avalonia
/// window and tray icon. Requests run one after another on a background queue, so a slow service never freezes the window;
/// <see cref="Changed"/> fires on that background thread after each answer.
/// </summary>
internal sealed class LinuxGuiClient(LinuxPaths paths, string version)
{
    private readonly object _queueGate = new();
    private Task _tail = Task.CompletedTask;
    private int _pollQueued;
    private volatile LinuxStatus? _status;

    public LinuxPaths Paths => paths;
    public string Version => version;
    public LinuxStatus? Status => _status;
    public bool Connected { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public int HistoryMinutes { get; set; } = 60;

    public event Action? Changed;

    /// <summary>Queues a status refresh; skipped while one is still waiting, so a slow service does not pile them up.</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _pollQueued, 1) == 1) return;
        Enqueue(() =>
        {
            Volatile.Write(ref _pollQueued, 0);
            SendNow(new LinuxRequest("guiStatus", Minutes: HistoryMinutes));
        });
    }

    /// <summary>Queues a command; commands keep their order (e.g. save a profile, then select it).</summary>
    public void Send(LinuxRequest request) => Enqueue(() => SendNow(request));

    /// <summary>Sends now on the calling thread and returns the answer; for snapshots and background work only.</summary>
    public LinuxStatus? SendNow(LinuxRequest request)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 5_000;
            socket.Connect(new UnixDomainSocketEndPoint(paths.Socket));
            using var stream = new NetworkStream(socket);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(request, Daemon.Json));
            var reply = JsonSerializer.Deserialize<LinuxStatus>(reader.ReadLine() ?? "", Daemon.Json);
            if (reply is not null)
            {
                // Keep cached profiles/battery/history when a quick command returns a compact status.
                var prev = _status;
                _status = reply with
                {
                    Profiles = reply.Profiles ?? prev?.Profiles,
                    Battery = reply.Battery ?? prev?.Battery,
                    History = reply.History ?? prev?.History,
                };
                Connected = true;
                LastUpdated = DateTimeOffset.Now;
                Changed?.Invoke();
            }
            return reply is null ? null : _status;
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException)
        {
            Connected = false;
            Changed?.Invoke();
            return null;
        }
    }

    private void Enqueue(Action work)
    {
        lock (_queueGate)
            _tail = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
