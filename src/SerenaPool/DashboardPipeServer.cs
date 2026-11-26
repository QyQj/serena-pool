using System.Text.Json;

namespace SerenaPool;

internal enum DashboardOperation { Unknown, Workers, Calls, Release, Recycle }

internal sealed record DashboardRequest(DashboardOperation Operation, long Number);
internal sealed record DashboardReply(int Status, JsonElement? Payload = null);

internal sealed class DashboardPipeServer(LocalPipeAddress address, InstancePool pool, ILogger<DashboardPipeServer> log)
    : BackgroundService
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var listener = address.Listen(dashboard: true);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { await listener.WaitForConnectionAsync(stop); }
                catch (UnauthorizedAccessException error)
                {
                    log.LogWarning(error, "Dashboard pipe rejected a client");
                    continue;
                }

                using var connection = listener;
                // Keep the listening socket open while another HTTP request connects on Unix.
                listener = address.Listen(dashboard: true);
                await ServeAsync(connection, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { listener.Dispose(); }
    }

    private async Task ServeAsync(Stream pipe, CancellationToken stop)
    {
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var line = await reader.ReadLineAsync(readTimeout.Token);
            if (line is null) return;
            var request = JsonSerializer.Deserialize<DashboardRequest>(line, Json);
            var reply = await ExecuteRequestAsync(request);
            await JsonSerializer.SerializeAsync(pipe, reply, Json, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
        {
            log.LogWarning(error, "Dashboard pipe request failed");
        }
    }

    private async Task<DashboardReply> ExecuteRequestAsync(DashboardRequest? request)
    {
        try
        {
            return request switch
            {
                { Operation: DashboardOperation.Workers } => new(200, JsonSerializer.SerializeToElement(pool.Snapshot(), Json)),
                { Operation: DashboardOperation.Calls } => new(200, JsonSerializer.SerializeToElement(pool.RecentCalls(), Json)),
                { Operation: DashboardOperation.Release, Number: > 0 } => new(pool.ReleaseByNumber(request.Number) ? 204 : 404),
                { Operation: DashboardOperation.Recycle, Number: > 0 } => new(await pool.RecycleAsync(request.Number) ? 204 : 409),
                _ => new(400)
            };
        }
        catch (Exception error)
        {
            log.LogError(error, "Dashboard operation failed");
            return new(500);
        }
    }

    public static async Task<DashboardReply> SendAsync(LocalPipeAddress address, DashboardRequest request, CancellationToken token)
    {
        using var pipe = address.ConnectClient(dashboard: true);
        await pipe.ConnectAsync(1000, token);
        await using (var writer = new StreamWriter(pipe, leaveOpen: true))
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), token);
        return await JsonSerializer.DeserializeAsync<DashboardReply>(pipe, Json, token)
            ?? throw new InvalidDataException("Missing dashboard pipe response.");
    }
}
