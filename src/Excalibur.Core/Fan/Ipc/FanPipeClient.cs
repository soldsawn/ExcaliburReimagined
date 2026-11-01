using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;

namespace Excalibur.Core.Fan.Ipc;

/// <summary>
/// Client side of <see cref="FanPipe"/>: one connection per request, which keeps it simple
/// and survives service restarts without any reconnect logic. Needs no administrator rights.
/// On Linux the same exchange runs over the service's Unix socket (<see cref="ForUnixSocket"/>).
/// </summary>
public sealed class FanPipeClient
{
    private readonly string? _socketPath;

    public FanPipeClient(string pipeName) => PipeName = pipeName;

    private FanPipeClient(string name, string socketPath)
    {
        PipeName = name;
        _socketPath = socketPath;
    }

    /// <summary>The Linux service's control socket.</summary>
    public static FanPipeClient ForUnixSocket(string path) => new(path, path);

    public string PipeName { get; }

    /// <summary>Sends one request. Throws <see cref="TimeoutException"/> or <see cref="IOException"/> when the service is not reachable.</summary>
    public async Task<FanResponse> SendAsync(FanRequest request, CancellationToken ct = default, int connectTimeoutMs = 1000)
    {
        await using var stream = await ConnectAsync(connectTimeoutMs, ct);
        using var reader = new StreamReader(stream);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, FanPipe.Json).AsMemory(), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        string line = await reader.ReadLineAsync(timeout.Token)
            ?? throw new IOException("The fan service closed the connection.");
        return JsonSerializer.Deserialize<FanResponse>(line, FanPipe.Json)
            ?? throw new IOException("The fan service sent an empty reply.");
    }

    private async Task<Stream> ConnectAsync(int connectTimeoutMs, CancellationToken ct)
    {
        if (_socketPath is null)
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(connectTimeoutMs, ct); }
            catch { await pipe.DisposeAsync(); throw; }
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(connectTimeoutMs);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timeout.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            // Callers treat an unreachable service as IOException, as with the pipe.
            throw new IOException($"The fan service socket {_socketPath} is not reachable ({ex.Message}).", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
