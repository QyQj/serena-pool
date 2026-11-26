using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SerenaPool;

internal sealed partial class SerenaProcess(Process launcher, Action terminate, Process server, McpClient client) : IAsyncDisposable
{
    public int Pid => server.Id;
    public bool HasExited => server.HasExited || client.Completion.IsCompleted;

    // This short-lived launcher starts Serena only after its parent owns the job.
    // Its standard handles pass straight to Serena, including after the launcher exits.
    public static int Launch(string executable, string workdir)
    {
        if (Console.OpenStandardInput().ReadByte() != 1) return -1;
        using var child = Spawn(executable, workdir);
        return child.Id;
    }

    internal static Process Spawn(string executable, string workdir)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workdir };
        foreach (var arg in new[] { "start-mcp-server", "--transport", "stdio", "--project", workdir,
            "--context", "ide", "--agent-interface", "repl", "--add-mode", "no-memories",
            "--enable-web-dashboard", "false", "--enable-gui-log-window", "false" })
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    public static async Task<SerenaProcess> StartAsync(string directory, PoolSettings settings, CancellationToken token)
    {
        var executable = settings.ResolveExecutable();
        var start = OperatingSystem.IsWindows() ? ProcessLauncher.Self("--launch-serena", executable, directory)
            : ProcessLauncher.Self("--launch-unix", Environment.ProcessId.ToString(), executable, directory);
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        var launcher = Process.Start(start)!;
        Action? cleanup = null;
        void Terminate() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
        Process? server = null;
        McpClient? client = null;
        string? lastDiagnostic = null;
        try
        {
            launcher.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Volatile.Write(ref lastDiagnostic, e.Data);
            };
            launcher.BeginErrorReadLine();
            var input = launcher.StandardInput.BaseStream;
            int pid;
            if (OperatingSystem.IsWindows())
            {
                cleanup = WindowsJob.Contain(launcher).Dispose;
                input.WriteByte(1);
                await input.FlushAsync(token);
                // Redirected stderr stays open in Serena after the Windows launcher exits.
                while (!launcher.HasExited) await Task.Delay(15, token);
                pid = launcher.ExitCode;
            }
            else
            {
                var leader = launcher.Id;
                cleanup = () => ProcessLauncher.KillGroup(leader);
                if (!int.TryParse(await launcher.StandardOutput.ReadLineAsync(token), out pid))
                    throw new IOException("未收到 Serena 启动进程 ID。");
                launcher.Exited += (_, _) => Terminate();
                launcher.EnableRaisingEvents = true;
            }
            if (pid <= 0) throw new IOException("无法启动 Serena。");
            server = Process.GetProcessById(pid);
            server.Exited += (_, _) => Terminate();
            server.EnableRaisingEvents = true;
            var transport = new StreamClientTransport(input, launcher.StandardOutput.BaseStream);
            client = await McpClient.CreateAsync(transport, cancellationToken: token);
            var tools = await client.ListToolsAsync(cancellationToken: token);
            if (!tools.Any(t => t.Name == "serena_repl") || !tools.Any(t => t.Name == "initial_instructions"))
                throw new McpException("Serena 未提供所需的 REPL 与初始化工具。");
            return new(launcher, Terminate, server, client);
        }
        catch (Exception ex)
        {
            launcher.StandardInput.Close();
            if (!OperatingSystem.IsWindows() && !launcher.HasExited) launcher.Kill();
            Terminate();
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
            !PoolSettings.PathComparer.Equals(PoolSettings.ResolveWorkdir(active.Groups[1].Value), directory))
            throw new McpException("无法确认 Serena 会话与活动项目目录。请检查本机 Serena 版本与项目配置。");
        return (session.Groups[1].Value, manual.Remove(session.Index, session.Length).Trim());
    }

    public ValueTask<CallToolResult> CallAsync(string session, string code, CancellationToken token) =>
        client.CallToolAsync("serena_repl", new Dictionary<string, object?> { ["session"] = session, ["code"] = code },
            cancellationToken: token);

    public async ValueTask DisposeAsync()
    {
        terminate();
        try { await client.DisposeAsync(); }
        finally { server.Dispose(); launcher.Dispose(); }
    }
}
