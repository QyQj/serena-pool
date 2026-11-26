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
    WorkingDirectory = app, ArgumentList = { "--core", "--urls", url.GetLeftPart(UriPartial.Authority) }
};
start.Environment["SERENA_POOL_LANGUAGE"] = "zh-CN";
start.Environment["SERENA_HOME"] = Path.Combine(sandbox, "serena-home");
start.Environment["Pool__IdleTimeout"] = "00:00:04";
start.Environment["Pool__WebDashboard"] = "true";
start.Environment["Pool__WebDashboardOpenOnLaunch"] = "false";
using var host = new Process { StartInfo = start };
bool hostStarted = false;
Process? web = null;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var ct = deadline.Token;
try
{
    await CheckCodexSetup(binary, sandbox, ct);
    Directory.CreateDirectory(primary);
    await Git("init", "-q", primary);
    await File.WriteAllTextAsync(Path.Combine(primary, "sample.py"), "class Marker:\r\n    branch = 'primary'\r\n", ct);
    await Git("-C", primary, "add", "sample.py");
    await Git("-C", primary, "-c", "user.name=Pool Smoke", "-c", "user.email=smoke@localhost",
        "-c", "commit.gpgsign=false", "commit", "-qm", "fixture");
    await Git("-C", primary, "worktree", "add", "--quiet", "--detach", secondary);
    await File.WriteAllTextAsync(Path.Combine(secondary, "sample.py"), "class Marker:\r\n    branch = 'secondary'\r\n", ct);

    hostStarted = host.Start();
    using var dashboard = new HttpClient { BaseAddress = url };
    Process StartDashboard()
    {
        var webStart = new ProcessStartInfo(binary)
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "dashboard", "--urls", url.GetLeftPart(UriPartial.Authority) }
        };
        webStart.Environment["Pool__WebDashboard"] = "true";
        webStart.Environment["Pool__WebDashboardOpenOnLaunch"] = "false";
        return Process.Start(webStart)!;
    }
    web = StartDashboard();
    await WaitForDashboard(dashboard, ct);
    var initial = await Task.WhenAll(dashboard.GetStringAsync("/api/workers", ct), dashboard.GetStringAsync("/api/calls", ct));
    Check(JsonNode.Parse(initial[0]) is JsonArray { Count: 0 }, "dashboard status is available without a token");
    Check(JsonNode.Parse(initial[1])?["calls"] is JsonArray { Count: 0 }, "call history starts empty");
    using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/workers");
    foreign.Headers.Add("Origin", "http://example.org");
    using var blocked = await dashboard.SendAsync(foreign, ct);
    Check(blocked.StatusCode == HttpStatusCode.Forbidden, "foreign origin rejected");
    using var foreignHistory = new HttpRequestMessage(HttpMethod.Get, "/api/calls");
    foreignHistory.Headers.Add("Origin", "http://example.org");
    using var blockedHistory = await dashboard.SendAsync(foreignHistory, ct);
    Check(blockedHistory.StatusCode == HttpStatusCode.Forbidden, "foreign origin cannot read call history");
    foreach (var path in new[] { "/", "/app.js", "/app.css" })
        Check((await dashboard.GetStringAsync(path, ct)).Length > 0, $"embedded asset {path}");

    await CheckDashboardLocale(dashboard, "en-US", null, "en", "Worker pool", "Idle", ct);
    await CheckDashboardLocale(dashboard, "fr-FR, zh-HK;q=0.7", null, "zh-CN", "实例池", "空闲", ct);
    await CheckDashboardLocale(dashboard, "zh-CN", "en", "en", "Worker pool", "Idle", ct);

    using var favicon = await dashboard.GetAsync("/favicon.ico", ct);
    favicon.EnsureSuccessStatusCode();
    var icon = await favicon.Content.ReadAsByteArrayAsync(ct);
    Check(favicon.Content.Headers.ContentType?.MediaType == "image/x-icon" &&
        icon.AsSpan().StartsWith(new byte[] { 0, 0, 1, 0 }), "dashboard favicon");

    using var removed = await dashboard.PostAsync("/mcp", null, ct);
    Check(removed.StatusCode == HttpStatusCode.NotFound, "HTTP MCP endpoint removed");
    Task<McpClient> Connect() => McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = binary, Arguments = ["mcp", "--urls", url.GetLeftPart(UriPartial.Authority)],
        WorkingDirectory = sandbox
    }), cancellationToken: ct);
    await using var client = await Connect();
    await using var pipeClient = await Connect();
    var tools = await client.ListToolsAsync(cancellationToken: ct);
    Check(tools.Select(t => t.Name).Order().SequenceEqual(new[] { "acquire", "call", "list", "release" }), "MCP tools");
    Check(tools.Single(t => t.Name == "acquire").ProtocolTool.Description?.Contains("领用") == true,
        "MCP descriptions follow the pool language");
    string[] titles = ["SerenaPool Acquire", "SerenaPool Call", "SerenaPool List", "SerenaPool Release"];
    Check(tools.Select(t => t.ProtocolTool.Title).Order().SequenceEqual(titles), "stdio tool titles");

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
    Check(fromStdio.IsError != true && Text(fromStdio).Contains("731"), "a second stdio client can use the same lease");
    Check(Text(await Call(ids[1], "'independent' if 'marker' not in globals() else 'contaminated'")).Contains("independent"), "independent namespace");
    // Serena's default registry evicts the oldest of 100 sessions, including live leases.
    for (var i = 0; i < 100; i++)
    {
        var shortLease = await Acquire(primary);
        var releasedShort = await Tool("release", ("instance_id", shortLease));
        Check(releasedShort.IsError != true && bool.Parse(Text(releasedShort)), "release short lease");
    }
    Check(Text(await Call(ids[0], "marker")) == "731", "long lease retains its namespace across 100 short leases");
    var duringRestart = Call(ids[0], "import time\ntime.sleep(2)\nmarker");
    await WaitUntil(async () => (await Snapshot()).Any(w => (bool)w!["running"]!));
    web.Kill();
    await web.WaitForExitAsync(ct);
    web.Dispose();
    web = null;
    Check(Text(await duringRestart) == "731" && Text(await Call(ids[0], "marker")) == "731",
        "dashboard exit leaves in-flight work and its lease intact");
    web = StartDashboard();
    await WaitForDashboard(dashboard, ct);
    Check((await Snapshot()).Any(w => (int)w!["pid"]! == (int)main["pid"]!), "restarted dashboard sees the original worker");
    Console.WriteLine("Passed: dashboard restarts independently and preserves worker and lease state.");

    var observedCall = await Call(ids[0], """
        ignored = "s.fs.read_file('dashboard-private-source')"
        inspect_api = getattr(s, "info")
        for _ in range(2):
            inspect_api("lsp.get_symbols_overview")
        "dashboard-private-result"
        """);
    Check(observedCall.IsError != true && Text(observedCall) == "dashboard-private-result" &&
        !(observedCall.StructuredContent?.GetRawText().Contains("serena-pool-usage-") ?? false),
        "instrumented calls retain the original MCP response");
    var pythonError = await Call(ids[1], "s.vars()\nraise ValueError('dashboard-private-error')");
    Check(Text(pythonError).StartsWith("ValueError: dashboard-private-error"), "REPL error keeps its original output");
    var historyJson = await dashboard.GetStringAsync("/api/calls", ct);
    var history = JsonNode.Parse(historyJson)!["calls"]!.AsArray();
    Check((string?)history[0]!["status"] == "failed" && (int?)history[0]!["tools"]?["s.vars"] == 1 &&
        (string?)history[1]!["status"] == "succeeded" && (int?)history[1]!["tools"]?["s.info"] == 2 &&
        history[1]!["tools"]?["fs.read_file"] is null && (string?)history[1]!["workdir"] == primary,
        "history counts actual REPL invocations including those in failed calls");
    Check(!historyJson.Contains("dashboard-private-") && !historyJson.Contains(ids[0]), "history contains only call metadata");
    const string symbol = "s.lsp.find_symbol('Marker', relative_path='sample.py', include_body=True)";
    var symbols = await Task.WhenAll(Call(ids[0], symbol), Call(third, symbol));
    for (var i = 0; i < symbols.Length; i++)
    {
        var result = Text(symbols[i]);
        Check(result.Contains(i == 0 ? "primary" : "secondary"), "worktree symbol lookup: " + result);
    }
    var symbolHistory = JsonNode.Parse(await dashboard.GetStringAsync("/api/calls", ct))!["calls"]!.AsArray();
    Check(symbolHistory.Take(2).All(entry => (int?)entry!["tools"]?["lsp.find_symbol"] == 1),
        "facade calls are observed in both worktrees");
    Console.WriteLine("Passed: MCP access, process reuse, private sessions, both worktree symbol lookups.");

    var running = Call(ids[0], "import time\ntime.sleep(2)\n'finished'");
    await WaitUntil(async () => (await Snapshot()).Any(w => (bool)w!["running"]!));
    var queued = Call(ids[0], "'should not execute'");
    await WaitUntil(async () => (await Snapshot()).Any(w => (int)w!["queued"]! == 1));
    await Tool("release", ("instance_id", ids[0]));
    Check((await queued).IsError == true && Text(await running).Contains("finished"), "release drains only running work");
    var canceledHistory = JsonNode.Parse(await dashboard.GetStringAsync("/api/calls", ct))!["calls"]!.AsArray();
    Check(canceledHistory.Take(2).Any(entry => (string?)entry!["status"] == "canceled" && entry["tools"] is null),
        "canceled queued calls have no tool trace");
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
    var failedHistory = JsonNode.Parse(await dashboard.GetStringAsync("/api/calls", ct))!["calls"]!.AsArray();
    Check((string?)failedHistory[0]!["status"] == "unknown", "worker loss is recorded with an unknown outcome");
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
    using var offline = await dashboard.GetAsync("/api/workers", ct);
    Check(offline.StatusCode == HttpStatusCode.ServiceUnavailable && !web.HasExited, "standalone dashboard reports an unavailable core");
    web.Kill();
    await web.WaitForExitAsync(ct);
    await CheckColdStdio(binary, sandbox, primary, ct);
}
finally
{
    if (web is not null)
    {
        if (!web.HasExited) { web.Kill(); await web.WaitForExitAsync(); }
        web.Dispose();
    }
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

static async Task CheckDashboardLocale(HttpClient http, string acceptLanguage, string? requested, string language,
    string heading, string idle, CancellationToken ct)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, requested is null ? "/" : $"/?lang={requested}");
    request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
    using var response = await http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();
    var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(ct));
    Check(response.Content.Headers.ContentLanguage.Single() == language && page.Contains($"lang=\"{language}\"") &&
        page.Contains(heading) && page.Contains($"/app.js?lang={language}"), "dashboard language " + language);

    var script = await http.GetStringAsync($"/app.js?lang={language}", ct);
    const string prefix = "const messages = ";
    var json = script.Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim();
    Check(JsonNode.Parse(json.TrimEnd(';'))?["DashboardIdle"]?.GetValue<string>() == idle, "dashboard script language");
}

static async Task CheckCodexSetup(string binary, string sandbox, CancellationToken ct)
{
    var home = Directory.CreateDirectory(Path.Combine(sandbox, "setup")).FullName;
    var config = Path.Combine(home, "config.toml");
    await File.WriteAllTextAsync(config, $$"""
        [mcp_servers.serena-pool]
        command = {{JsonValue.Create(binary)!.ToJsonString()}}
        args = ["stdio", "--urls", "http://127.0.0.1:30741"]
        env = { Pool__WebDashboard = "false", OTHER = "private-setting" } # dashboard setting
        """.ReplaceLineEndings("\r\n") + "\r\n", ct);

    var original = await File.ReadAllBytesAsync(config, ct);
    var priorBackup = config + ".SerenaPool";
    await File.WriteAllTextAsync(priorBackup, "existing backup", ct);

    var start = new ProcessStartInfo(binary)
    {
        UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "setup", "codex" },
        RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8
    };
    foreach (var name in new[] { "HOME", "USERPROFILE", "CODEX_HOME", "COPILOT_HOME", "XDG_CONFIG_HOME", "GEMINI_CLI_HOME" })
        start.Environment[name] = home;
    start.Environment.Remove("OPENCODE_CONFIG");
    start.Environment["SERENA_POOL_LANGUAGE"] = "en";
    async Task<string> RunSetup()
    {
        using var setup = Process.Start(start)!;
        var output = setup.StandardOutput.ReadToEndAsync(ct);
        var errors = setup.StandardError.ReadToEndAsync(ct);
        await setup.WaitForExitAsync(ct);
        Check(setup.ExitCode == 0, $"setup: {await errors}");
        return await output;
    }

    var summary = await RunSetup();
    var backup = config + ".1.SerenaPool";
    Check((await File.ReadAllBytesAsync(backup, ct)).SequenceEqual(original) &&
        await File.ReadAllTextAsync(priorBackup, ct) == "existing backup", "setup preserves both backups");
    Check(summary.Contains("Settings written by Serena Pool") &&
        summary.Contains(backup) && summary.Contains("[mcp_servers.serena-pool]") &&
        summary.Contains("Pool__Executable") && !summary.Contains("private-setting"), "setup shows only its managed configuration");
    var updated = await File.ReadAllTextAsync(config, ct);
    Check(updated.Contains("SerenaPool@") && updated.Contains("30741") && updated.Contains("Pool__Executable") &&
        updated.Contains("Pool__WebDashboard = \"false\"") && updated.Contains("# dashboard setting"),
        "setup preserves inline Codex configuration");

    var existingParent = """
        [mcp_servers] # keep the existing header
        [mcp_servers.existing]
        command = "example"
        """.ReplaceLineEndings("\r\n") + "\r\n";
    await File.WriteAllTextAsync(config, existingParent, ct);
    await RunSetup();
    var appended = await File.ReadAllTextAsync(config, ct);
    Check(appended.StartsWith(existingParent, StringComparison.Ordinal) &&
        appended.Split('\n').Count(line => line.StartsWith("[mcp_servers]", StringComparison.Ordinal)) == 1 &&
        appended.Contains("[mcp_servers.serena-pool]"), "setup adds only the Codex child table");
}

static async Task CheckColdStdio(string binary, string sandbox, string project, CancellationToken ct)
{
    var port = ReservePort();
    var url = $"http://127.0.0.1:{port}";
    Task<McpClient> Connect(bool dashboard) => McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = binary, WorkingDirectory = sandbox, Arguments = ["mcp", "--urls", url],
        EnvironmentVariables = new Dictionary<string, string?>
        {
            ["SERENA_HOME"] = Path.Combine(sandbox, "serena-home"),
            ["Pool__IdleTimeout"] = "00:00:01",
            ["Pool__WebDashboard"] = dashboard.ToString(),
            ["Pool__WebDashboardOpenOnLaunch"] = "false"
        }
    }), cancellationToken: ct);

    var clients = await Task.WhenAll(Connect(true), Connect(true));
    using var http = new HttpClient { BaseAddress = new Uri(url) };
    await using (var second = clients[1])
    {
        string id;
        await using (var first = clients[0])
        {
            await WaitForDashboard(http, ct);
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
    Console.WriteLine("Passed: concurrent cold stdio startup shares one core and the automatic dashboard follows core shutdown.");

    port = ReservePort();
    url = $"http://127.0.0.1:{port}";
    await using var disabled = await Connect(false);
    Check((await disabled.ListToolsAsync(cancellationToken: ct)).Count == 4, "stdio works with dashboard disabled");
    using var reservation = new TcpListener(IPAddress.Loopback, port);
    reservation.Start();
    Console.WriteLine("Passed: stdio-only core leaves the TCP port free.");
}

static async Task WaitForDashboard(HttpClient http, CancellationToken ct)
{
    using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
    ready.CancelAfter(TimeSpan.FromSeconds(15));
    while (true)
    {
        try
        {
            using var response = await http.GetAsync("/", ready.Token);
            response.EnsureSuccessStatusCode();
            return;
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
        {
            await Task.Delay(50, ready.Token);
        }
    }
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
