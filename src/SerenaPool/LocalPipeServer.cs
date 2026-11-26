using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace SerenaPool;

internal sealed class LocalPipeServer(LocalPipeAddress address, IHostApplicationLifetime lifetime, IServiceProvider services,
    IOptions<McpServerOptions> options, InstancePool pool, ILogger<LocalPipeServer> log) : BackgroundService
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(15);
    private int connections;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var pipe = address.Listen();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(IdleCheckInterval);
            try { await pipe.WaitForConnectionAsync(timeout.Token); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                pipe.Dispose();
                if (Volatile.Read(ref connections) == 0 && pool.IsHostIdle())
                {
                    log.LogInformation("No active clients or workers, stopping the core");
                    lifetime.StopApplication();
                }
                continue;
            }
            catch (UnauthorizedAccessException error)
            {
                pipe.Dispose();
                log.LogWarning(error, "MCP pipe rejected a client");
                continue;
            }
            catch { pipe.Dispose(); throw; }
            Interlocked.Increment(ref connections);
            _ = ServeAsync(pipe, stop);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using (pipe)
        try
        {
            await using var transport = new StreamServerTransport(pipe, pipe, "SerenaPool");
            await using var server = McpServer.Create(transport, options.Value, serviceProvider: services);
            await server.RunAsync(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { log.LogWarning("{Message} ({ErrorType})", LocalText("StdioDisconnected"), error.GetType().Name); }
        finally { Interlocked.Decrement(ref connections); }
    }
}

internal sealed class LocalPipeAddress(int port)
{
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    public string Name { get; } = $"serena-pool-core-{UserId()}-{port}";

    public NamedPipeServerStream Listen(bool dashboard = false) => new(GetName(dashboard), PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, Options);

    public NamedPipeClientStream ConnectClient(bool dashboard = false) => new(".", GetName(dashboard), PipeDirection.InOut, Options);

    private string GetName(bool dashboard) => dashboard ? Name + "-web" : Name;

    internal static string UserId()
    {
        if (!OperatingSystem.IsWindows()) return ProcessLauncher.geteuid().ToString();
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }
}
