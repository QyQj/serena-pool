using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args is not [var scratchRoot] || !Path.IsPathFullyQualified(scratchRoot) || !Directory.Exists(scratchRoot))
    throw new ArgumentException("请传入一个已存在的临时目录绝对路径。");
var sandbox = Directory.CreateDirectory(Path.Combine(scratchRoot, $"serena-pool-test-{Guid.NewGuid():N}")).FullName;
var primary = Path.Combine(sandbox, "主工作区");
var secondary = Path.Combine(sandbox, "worktree");
var app = Path.GetFullPath("src/SerenaPool");
var binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SerenaPool.exe" : "SerenaPool");
var url = new Uri($"http://127.0.0.1:{ReservePort()}");
var start = new ProcessStartInfo(binary)
{
    UseShellExecute = false, CreateNoWindow = true,
    WorkingDirectory = app, ArgumentList = { "--background-serve", "--urls", url.GetLeftPart(UriPartial.Authority) }
};
start.Environment["SERENA_HOME"] = Path.Combine(sandbox, "serena-home");
start.Environment["Pool__IdleTimeout"] = "00:00:04";
start.Environment["Pool__WebDashboard"] = "true";
start.Environment["Pool__WebDashboardOpenOnLaunch"] = "false";
using var host = new Process { StartInfo = start };
bool hostStarted = false;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var ct = deadline.Token;
try
{
    Directory.CreateDirectory(primary);
    await Git("init", "-q", primary);
    await File.WriteAllTextAsync(Path.Combine(primary, "sample.py"), "class Marker:\r\n    branch = 'primary'\r\n", ct);
    await Git("-C", primary, "add", "sample.py");
    await Git("-C", primary, "-c", "user.name=Pool Smoke", "-c", "user.email=smoke@localhost",
        "-c", "commit.gpgsign=false", "commit", "-qm", "fixture");
    await Git("-C", primary, "worktree", "add", "--quiet", "--detach", secondary);
    await File.WriteAllTextAsync(Path.Combine(secondary, "sample.py"), "class Marker:\r\n    branch = 'secondary'\r\n", ct);

    hostStarted = host.Start();
    using var http = new HttpClient { BaseAddress = url };
    using var dashboard = new HttpClient { BaseAddress = url };
    using (var ready = CancellationTokenSource.CreateLinkedTokenSource(ct))
    {
        ready.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            Check(!host.HasExited, "background host startup");
            try
            {
                using var response = await http.GetAsync("/", ready.Token);
                response.EnsureSuccessStatusCode();
                break;
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
            {
                await Task.Delay(50, ready.Token);
            }
        }
    }
    Check(JsonNode.Parse(await dashboard.GetStringAsync("/api/workers", ct)) is JsonArray { Count: 0 },
        "dashboard status is available without a token");
    using var foreign = new HttpRequestMessage(HttpMethod.Post, "/mcp");
    foreign.Headers.Add("Origin", "http://example.org");
    using var blocked = await http.SendAsync(foreign, ct);
    Check(blocked.StatusCode == HttpStatusCode.Forbidden, "foreign origin rejected");
    foreach (var path in new[] { "/", "/app.js", "/app.css" })
        Check((await http.GetStringAsync(path, ct)).Length > 0, $"embedded asset {path}");

    using var favicon = await http.GetAsync("/favicon.ico", ct);
    favicon.EnsureSuccessStatusCode();
    var icon = await favicon.Content.ReadAsByteArrayAsync(ct);
    Check(favicon.Content.Headers.ContentType?.MediaType == "image/x-icon" &&
        icon.AsSpan().StartsWith(new byte[] { 0, 0, 1, 0 }), "dashboard favicon");

    var transport = new HttpClientTransport(new() { Endpoint = new Uri(url, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
    var tools = await client.ListToolsAsync(cancellationToken: ct);
    Check(tools.Select(t => t.Name).Order().SequenceEqual(new[] { "acquire", "call", "list", "release" }), "MCP tools");
    var stdio = new StdioClientTransport(new()
    {
        Command = binary, Arguments = ["mcp", "--urls", url.GetLeftPart(UriPartial.Authority)],
        WorkingDirectory = sandbox
    });
    await using var pipeClient = await McpClient.CreateAsync(stdio, cancellationToken: ct);
    var pipeTools = await pipeClient.ListToolsAsync(cancellationToken: ct);
    Check(pipeTools.Select(t => t.Name).Order().SequenceEqual(tools.Select(t => t.Name).Order()), "stdio tools");

    async Task<CallToolResult> Tool(string name, params (string, object?)[] arguments) =>
        await client.CallToolAsync(name, arguments.ToDictionary(pair => pair.Item1, pair => pair.Item2), cancellationToken: ct);
    static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    async Task<string> Acquire(string dir)
    {
        var result = await Tool("acquire", ("workdir", dir));
        Check(result.IsError != true, Text(result));
        return JsonNode.Parse(Text(result))!["instance_id"]!.GetValue<string>();
    }
    Task<CallToolResult> Call(string id, string code) => Tool("call", ("instance_id", id), ("code", code));
    async Task<JsonArray> Snapshot() => JsonNode.Parse(await dashboard.GetStringAsync("/api/workers", ct))!.AsArray();
    async Task WaitUntil(Func<Task<bool>> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!await predicate())
        {
            Check(watch.Elapsed < TimeSpan.FromSeconds(12), "instance state transition");
            await Task.Delay(50, ct);
        }
    }

    var ids = await Task.WhenAll(Acquire(primary), Acquire(primary), Acquire(secondary));
    var third = ids[2];
    var all = await Snapshot();
    Check(all.Count == 2 && all.Select(w => (int)w!["pid"]!).Distinct().Count() == 2, "one process per worktree");
    var main = all.Single(w => w!["leases"]!.AsArray().Count == 2)!;
    primary = (string)main["workdir"]!;
    secondary = (string)all.Single(w => w != main)!["workdir"]!;
    Check(main["leases"]!.AsArray().Count == 2 && !Text(await Tool("list")).Contains(ids[0]), "shared process, private handles");
    var fromStdio = await pipeClient.CallToolAsync("call", new Dictionary<string, object?>
        { ["instance_id"] = ids[0], ["code"] = "marker = 731\nmarker" }, cancellationToken: ct);
    Check(Text(fromStdio).Contains("731"), "stdio and HTTP share the same backend and lease");
    Check(Text(await Call(ids[1], "'independent' if 'marker' not in globals() else 'contaminated'")).Contains("independent"), "independent namespace");
    Check(Text(await Call(ids[0], "marker")).Contains("731"), "persisted namespace");
    const string symbol = "s.lsp.find_symbol('Marker', relative_path='sample.py', include_body=True)";
    var symbols = await Task.WhenAll(Call(ids[0], symbol), Call(third, symbol));
    for (var i = 0; i < symbols.Length; i++)
    {
        var result = Text(symbols[i]);
        Check(result.Contains(i == 0 ? "primary" : "secondary"), "worktree symbol lookup: " + result);
    }
    Console.WriteLine("Passed: MCP access, process reuse, private sessions, both worktree symbol lookups.");

    var running = Call(ids[0], "import time\ntime.sleep(2)\n'finished'");
    await WaitUntil(async () => (await Snapshot()).Any(w => (bool)w!["running"]!));
    var queued = Call(ids[0], "'should not execute'");
    await WaitUntil(async () => (await Snapshot()).Any(w => (int)w!["queued"]! == 1));
    await Tool("release", ("instance_id", ids[0]));
    Check((await queued).IsError == true && Text(await running).Contains("finished"), "release drains only running work");
    Check((await Call(ids[0], "1")).IsError == true, "revoked handle rejected");
    await Tool("release", ("instance_id", ids[1]));
    var warm = await Acquire(primary);
    Check((await Snapshot()).Any(w => (int)w!["pid"]! == (int)main["pid"]!), "warm worker retained");
    var leaseNumber = (long)(await Snapshot()).Single(w => (string?)w!["workdir"] == primary)!["leases"]![0]!["number"]!;
    var releasePath = "/api/leases/" + leaseNumber;
    using var foreignRelease = new HttpRequestMessage(HttpMethod.Delete, releasePath);
    foreignRelease.Headers.Add("Origin", "http://example.org");
    using var blockedRelease = await dashboard.SendAsync(foreignRelease, ct);
    Check(blockedRelease.StatusCode == HttpStatusCode.Forbidden, "foreign origin cannot release leases");
    using var released = await dashboard.DeleteAsync(releasePath, ct);
    Check(released.IsSuccessStatusCode && (await Call(warm, "1")).IsError == true, "dashboard release without a token");
    await WaitUntil(async () => (await Snapshot()).Count == 1);
    Console.WriteLine("Passed: queued cancellation, in-flight completion, warm reuse, admin release, timed recycling.");

    var doomed = await Acquire(primary);
    var interrupted = Call(doomed, "import time\ntime.sleep(30)\n'finished'");
    await WaitUntil(async () => (await Snapshot()).Any(w => (string?)w!["workdir"] == primary && (bool)w["running"]!));
    var worker = (await Snapshot()).Single(w => (string?)w!["workdir"] == primary)!;
    using (var process = Process.GetProcessById((int)worker["pid"]!))
        process.Kill();
    var unknown = await interrupted.WaitAsync(TimeSpan.FromSeconds(10), ct);
    Check(unknown.IsError == true && Text(unknown).Contains("执行结果未知"), "backend loss reports unknown outcome: " + Text(unknown));
    Check((await Call(doomed, "1")).IsError == true && Text(await Call(third, "17")).Contains("17"), "failed worker isolated");
    using var recycled = await dashboard.DeleteAsync("/api/workers/" + (long)worker["number"]!, ct);
    Check(recycled.StatusCode == HttpStatusCode.NoContent && (await Snapshot()).Count == 1, "dashboard recycling without a token");
    Console.WriteLine("Passed: worker death invalidated its leases and preserved the other worktree.");

    var backendPid = (int)(await Snapshot()).Single(w => (string?)w!["workdir"] == secondary)!["pid"]!;
    using var backend = Process.GetProcessById(backendPid);
    var descendants = Text(await Call(third, "import os, psutil\n[os.getpid()] + [p.pid for p in psutil.Process().children(recursive=True)]"));
    var tracked = System.Text.RegularExpressions.Regex.Matches(descendants, @"\b\d+\b")
        .Select(m => Process.GetProcessById(int.Parse(m.Value))).ToArray();
    Check(tracked.Length > 0, "tracked real Serena processes");
    try
    {
        host.Kill();
        await host.WaitForExitAsync(ct);
        await Task.WhenAll(tracked.Append(backend).Select(p => p.WaitForExitAsync(ct))).WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    finally { foreach (var p in tracked) p.Dispose(); }
    Console.WriteLine($"Passed: forced manager exit cleaned up Serena and {tracked.Length} observed runtime processes.");
    await CheckColdStdio(binary, sandbox, primary, ct);
}
finally
{
    if (hostStarted && !host.HasExited) { host.Kill(); await host.WaitForExitAsync(); }
    // Windows can keep Serena log handles open briefly after the manager's job is closed.
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(sandbox, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(sandbox, true);
            break;
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 32 && attempt < 20)
        {
            await Task.Delay(100);
        }
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task CheckColdStdio(string binary, string sandbox, string project, CancellationToken ct)
{
    var port = ReservePort();
    var url = $"http://127.0.0.1:{port}";
    Task<McpClient> Connect() => McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = binary, WorkingDirectory = sandbox, Arguments = ["mcp", "--urls", url],
        EnvironmentVariables = new Dictionary<string, string?>
        {
            ["SERENA_HOME"] = Path.Combine(sandbox, "serena-home"),
            ["Pool__IdleTimeout"] = "00:00:01",
            ["Pool__WebDashboard"] = "false",
            ["Pool__WebDashboardOpenOnLaunch"] = "true"
        }
    }), cancellationToken: ct);
    var clients = await Task.WhenAll(Connect(), Connect());
    await using (var second = clients[1])
    {
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        foreach (var path in new[] { "/", "/app.js", "/app.css", "/favicon.ico", "/api/workers" })
        {
            using var response = await http.GetAsync(path, ct);
            Check(response.StatusCode == HttpStatusCode.NotFound, $"dashboard disabled: {path}");
        }
        await using var httpClient = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
        }, http), cancellationToken: ct);
        Check((await httpClient.ListToolsAsync(cancellationToken: ct)).Count == 4, "HTTP MCP works with dashboard disabled");
        string id;
        await using (var first = clients[0])
        {
            var acquired = await first.CallToolAsync("acquire", new Dictionary<string, object?> { ["workdir"] = project }, cancellationToken: ct);
            var text = acquired.Content.OfType<TextContentBlock>().Single().Text;
            Check(acquired.IsError != true, text);
            id = (string)JsonNode.Parse(text)!["instance_id"]!;
        }
        var released = await second.CallToolAsync("release", new Dictionary<string, object?> { ["instance_id"] = id }, cancellationToken: ct);
        Check(released.IsError != true && bool.Parse(released.Content.OfType<TextContentBlock>().Single().Text), "independent stdio bridge lifetimes");
    }
    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
    wait.CancelAfter(TimeSpan.FromSeconds(35));
    while (true)
    {
        using var probe = new TcpClient();
        try { await probe.ConnectAsync(IPAddress.Loopback, port, wait.Token); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { break; }
        await Task.Delay(200, wait.Token);
    }
    Console.WriteLine("Passed: dashboard disabled with HTTP MCP and stdio available, shared leases and automatic host shutdown.");
}

static int ReservePort()
{
    using var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    return ((IPEndPoint)reservation.LocalEndpoint).Port;
}

static async Task Git(params string[] args)
{
    var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var git = Process.Start(start)!;
    await git.WaitForExitAsync();
    Check(git.ExitCode == 0, "git fixture setup");
}
