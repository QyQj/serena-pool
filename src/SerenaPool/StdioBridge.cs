using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace SerenaPool;

internal static class StdioBridge
{
    public static string PipeName(int port)
    {
        var user = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : ProcessLauncher.geteuid().ToString();
        return $"serena-pool-{user}-{port}";
    }

    public static async Task RunAsync(Uri address)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(address.Port), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(200); }
        catch (TimeoutException)
        {
            var start = ProcessLauncher.Self("--background-serve", "--urls", address.GetLeftPart(UriPartial.Authority));
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.StandardErrorEncoding = Encoding.UTF8;
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            string? diagnostic = null;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Volatile.Write(ref diagnostic, e.Data);
            };
            process.BeginErrorReadLine();
            // The fixed loopback port elects one host if multiple clients cold-start together.
            try { await pipe.ConnectAsync(10000); }
            catch (TimeoutException ex)
            {
                var detail = Volatile.Read(ref diagnostic) ?? LocalText("BridgeStartupHint");
                throw new IOException(LocalText("PoolConnectFailed", address, detail), ex);
            }
        }

        using var stop = new CancellationTokenSource();
        var input = Console.OpenStandardInput().CopyToAsync(pipe, stop.Token);
        var output = pipe.CopyToAsync(Console.OpenStandardOutput(), stop.Token);
        var completed = await Task.WhenAny(input, output);
        try { await completed; }
        finally { stop.Cancel(); }
    }
}
