using System.IO.Pipes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace SerenaPool;

internal sealed class LocalPipeServer(IServer web, IHostApplicationLifetime lifetime, IServiceProvider services,
    IOptions<McpServerOptions> options, InstancePool pool, ILogger<LocalPipeServer> log) : BackgroundService
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(15);
    private int connections;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stop);
        var address = new Uri(web.Features.Get<IServerAddressesFeature>()!.Addresses.First());
        var name = StdioBridge.PipeName(address.Port);
        while (!stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(IdleCheckInterval);
            try { await pipe.WaitForConnectionAsync(timeout.Token); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                pipe.Dispose();
                if (Volatile.Read(ref connections) == 0 && pool.IsHostIdle(IdleCheckInterval)) lifetime.StopApplication();
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
        catch (Exception ex) { log.LogWarning(ex, LocalText("StdioDisconnected")); }
        finally { Interlocked.Decrement(ref connections); }
    }
}
