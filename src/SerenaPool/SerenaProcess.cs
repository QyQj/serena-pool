using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SerenaPool;

internal sealed partial class SerenaProcess(Process launcher, SafeFileHandle job, Process server, McpClient client) : IAsyncDisposable
{
    public int Pid => server.Id;
    public bool HasExited => server.HasExited || client.Completion.IsCompleted;

    // This short-lived launcher starts Serena only after its parent owns the job.
    // Its standard handles pass straight to Serena, including after the launcher exits.
    public static int Launch(string executable, string workdir)
    {
        if (Console.OpenStandardInput().ReadByte() != 1) return -1;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workdir };
        foreach (var arg in new[] { "start-mcp-server", "--transport", "stdio", "--project", workdir,
            "--context", "ide", "--agent-interface", "repl", "--add-mode", "no-memories",
            "--enable-web-dashboard", "false", "--enable-gui-log-window", "false" })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        return child.Id;
    }

    public static async Task<SerenaProcess> StartAsync(string directory, PoolSettings settings, CancellationToken token)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardInput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        start.ArgumentList.Add("--launch-serena");
        start.ArgumentList.Add(settings.ResolveExecutable());
        start.ArgumentList.Add(directory);
        start.Environment.Remove("SerenaPool__Token");
        var launcher = Process.Start(start)!;
        SafeFileHandle? job = null;
        Process? server = null;
        McpClient? client = null;
        string? lastDiagnostic = null;
        try
        {
            job = WindowsJob.Contain(launcher);
            launcher.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Volatile.Write(ref lastDiagnostic, e.Data);
            };
            launcher.BeginErrorReadLine();
            var input = launcher.StandardInput.BaseStream;
            input.WriteByte(1);
            await input.FlushAsync(token);
            // WaitForExitAsync also waits for redirected stderr EOF, which belongs to the live server.
            while (!launcher.HasExited) await Task.Delay(15, token);
            if (launcher.ExitCode <= 0) throw new IOException("无法启动 Serena。");
            server = Process.GetProcessById(launcher.ExitCode);
            server.Exited += (_, _) => job.Dispose();
            server.EnableRaisingEvents = true;
            var transport = new StreamClientTransport(input, launcher.StandardOutput.BaseStream);
            client = await McpClient.CreateAsync(transport, cancellationToken: token);
            var tools = await client.ListToolsAsync(cancellationToken: token);
            if (!tools.Any(t => t.Name == "serena_repl") || !tools.Any(t => t.Name == "initial_instructions"))
                throw new McpException("Serena 未提供所需的 REPL 与初始化工具。");
            return new(launcher, job, server, client);
        }
        catch (Exception ex)
        {
            job?.Dispose();
            launcher.StandardInput.Close();
            if (client != null) await client.DisposeAsync();
            server?.Dispose();
            launcher.Dispose();
            if (ex is OperationCanceledException) throw;
            throw new McpException($"Serena 启动失败：{ex.Message} {Volatile.Read(ref lastDiagnostic)}", ex);
        }
    }

    [GeneratedRegex(@"<session>\s*Your Serena session id is \x60([a-zA-Z0-9_-]+)\x60[^<]*</session>")]
    private static partial Regex SessionTag();

    [GeneratedRegex(@"(?m)^(?:Created and activated a new project|The project) with name '[^\r\n]*' at (.+?)(?: is activated)?\.\r?$")]
    private static partial Regex ProjectLine();

    public async Task<(string Session, string Instructions)> OpenSessionAsync(string directory, CancellationToken token)
    {
        var result = await client.CallToolAsync("initial_instructions", cancellationToken: token);
        var manual = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        if (result.IsError == true) throw new McpException(manual);
        var session = SessionTag().Match(manual);
        var active = ProjectLine().Match(manual);
        if (!session.Success || !active.Success ||
            !StringComparer.OrdinalIgnoreCase.Equals(PoolSettings.ResolveWorkdir(active.Groups[1].Value), directory))
            throw new McpException("无法确认 Serena 会话与活动项目目录。请检查本机 Serena 版本与项目配置。");
        return (session.Groups[1].Value, manual.Remove(session.Index, session.Length).Trim());
    }

    public ValueTask<CallToolResult> CallAsync(string session, string code, CancellationToken token) =>
        client.CallToolAsync("serena_repl", new Dictionary<string, object?> { ["session"] = session, ["code"] = code },
            cancellationToken: token);

    public async ValueTask DisposeAsync()
    {
        job.Dispose();
        try { await client.DisposeAsync(); }
        finally { server.Dispose(); launcher.Dispose(); }
    }
}
