using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Excalibur.Linux.Ui;

/// <summary>
/// Talks to the Linux daemon over the Unix control socket and keeps the latest <see cref="LinuxStatus"/> for the Avalonia
/// window and tray icon.
/// </summary>
internal sealed class LinuxGuiClient(LinuxPaths paths, string version)
{
    public LinuxPaths Paths => paths;
    public string Version => version;
    public LinuxStatus? Status { get; private set; }
    public bool Connected { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public int HistoryMinutes { get; set; } = 60;

    public event Action? Changed;

    public LinuxStatus? Poll() => Send(new LinuxRequest("guiStatus", Minutes: HistoryMinutes));

    public LinuxStatus? Send(LinuxRequest request)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = request.Cmd.Equals("trial", StringComparison.OrdinalIgnoreCase) ? 60_000 : 5_000;
            socket.Connect(new UnixDomainSocketEndPoint(paths.Socket));
            using var stream = new NetworkStream(socket);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(request, Daemon.Json));
            var reply = JsonSerializer.Deserialize<LinuxStatus>(reader.ReadLine() ?? "", Daemon.Json);
            if (reply is not null)
            {
                // Keep cached profiles/battery/history when a quick command returns a compact status.
                var prev = Status;
                Status = reply with
                {
                    Profiles = reply.Profiles ?? prev?.Profiles,
                    Battery = reply.Battery ?? prev?.Battery,
                    History = reply.History ?? prev?.History,
                };
                Connected = true;
                LastUpdated = DateTimeOffset.Now;
                Changed?.Invoke();
            }
            return Status;
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException)
        {
            Connected = false;
            Changed?.Invoke();
            return null;
        }
    }
}
