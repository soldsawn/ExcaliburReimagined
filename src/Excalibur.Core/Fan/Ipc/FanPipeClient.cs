using System.IO.Pipes;
using System.Text.Json;

namespace Excalibur.Core.Fan.Ipc;

/// <summary>
/// Client side of <see cref="FanPipe"/>: one connection per request, which keeps it simple
/// and survives service restarts without any reconnect logic. Needs no administrator rights.
/// </summary>
public sealed class FanPipeClient(string pipeName)
{
    public string PipeName { get; } = pipeName;

    /// <summary>Sends one request. Throws <see cref="TimeoutException"/> when the service is not reachable.</summary>
    public async Task<FanResponse> SendAsync(FanRequest request, CancellationToken ct = default, int connectTimeoutMs = 1000)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMs, ct);
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, FanPipe.Json).AsMemory(), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        string line = await reader.ReadLineAsync(timeout.Token)
            ?? throw new IOException("The fan service closed the connection.");
        return JsonSerializer.Deserialize<FanResponse>(line, FanPipe.Json)
            ?? throw new IOException("The fan service sent an empty reply.");
    }
}
