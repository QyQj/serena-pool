using System.Diagnostics;
using System.Text;

namespace SerenaPool;

internal static class StdioBridge
{
    public static async Task RunAsync(Uri address, ILogger log)
    {
        using var pipe = new LocalPipeAddress(address.Port).ConnectClient();
        try { await pipe.ConnectAsync(200); }
        catch (TimeoutException)
        {
            log.LogInformation("Core pipe unavailable, attempting to start the core");
            var start = ProcessLauncher.Self("--background-core", "--urls", address.GetLeftPart(UriPartial.Authority));
            start.WorkingDirectory = AppContext.BaseDirectory;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.StandardErrorEncoding = Encoding.UTF8;
            using var process = Process.Start(start)!;
            log.LogInformation("Launched core candidate {Pid}", process.Id);
            process.StandardInput.Close();
            string? diagnostic = null;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Volatile.Write(ref diagnostic, e.Data);
            };
            process.BeginErrorReadLine();
            try { await pipe.ConnectAsync(10000); }
            catch (TimeoutException ex)
            {
                var detail = Volatile.Read(ref diagnostic) ?? LocalText("BridgeStartupHint");
                throw new IOException(LocalText("PoolConnectFailed", address, detail), ex);
            }
        }

        log.LogInformation("Connected to the core pipe");
        using var stop = new CancellationTokenSource();
        var input = Console.OpenStandardInput().CopyToAsync(pipe, stop.Token);
        var output = pipe.CopyToAsync(Console.OpenStandardOutput(), stop.Token);
        var completed = await Task.WhenAny(input, output);
        try
        {
            await completed;
            log.LogInformation("Bridge connection closed by {Side}", completed == input ? "stdio client" : "core");
        }
        finally { stop.Cancel(); }
    }
}
