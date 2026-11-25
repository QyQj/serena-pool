using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args is not [var scratchRoot] || !Path.IsPathFullyQualified(scratchRoot) || !Directory.Exists(scratchRoot))
    throw new ArgumentException("请传入一个已存在的临时目录绝对路径。");
var sandbox = Directory.CreateDirectory(Path.Combine(scratchRoot, $"serena-pool-test-{Guid.NewGuid():N}")).FullName;
var primary = Path.Combine(sandbox, "主工作区");
var secondary = Path.Combine(sandbox, "worktree");
var app = Path.GetFullPath("src/SerenaPool");
var binary = Path.Combine(AppContext.BaseDirectory, "SerenaPool.exe");
var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var start = new ProcessStartInfo(binary)
{
    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
    WorkingDirectory = app, ArgumentList = { "--urls", "http://127.0.0.1:0" }
};
start.Environment["SerenaPool__Token"] = secret;
start.Environment["SERENA_HOME"] = Path.Combine(sandbox, "serena-home");
start.Environment["Pool__IdleTimeout"] = "00:00:04";
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

    var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
    host.OutputDataReceived += (_, e) =>
    {
        const string marker = "Now listening on: ";
        if (e.Data is { } line && line.IndexOf(marker, StringComparison.Ordinal) is var i && i >= 0)
            ready.TrySetResult(new Uri(line[(i + marker.Length)..]));
    };
    hostStarted = host.Start();
    host.BeginOutputReadLine();
    var url = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    using var http = new HttpClient { BaseAddress = url };
    using var denied = await http.GetAsync("/api/workers", ct);
    Check(denied.StatusCode == HttpStatusCode.Unauthorized, "API requires token");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/workers");
    foreign.Headers.Add("Origin", "http://example.org");
    using var blocked = await http.SendAsync(foreign, ct);
    Check(blocked.StatusCode == HttpStatusCode.Forbidden, "foreign origin rejected");
    foreach (var path in new[] { "/", "/app.js", "/app.css" })
        Check((await http.GetStringAsync(path, ct)).Length > 0, $"embedded asset {path}");

    var transport = new HttpClientTransport(new() { Endpoint = new Uri(url, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
    var tools = await client.ListToolsAsync(cancellationToken: ct);
    Check(tools.Select(t => t.Name).Order().SequenceEqual(new[] { "acquire", "call", "list", "release" }), "MCP tools");

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
    async Task<JsonArray> Snapshot() => JsonNode.Parse(await http.GetStringAsync("/api/workers", ct))!.AsArray();
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
    var main = all.Single(w => (string?)w!["workdir"] == primary)!;
    Check(main["leases"]!.AsArray().Count == 2 && !Text(await Tool("list")).Contains(ids[0]), "shared process, private handles");
    Check(Text(await Call(ids[0], "marker = 731\nmarker")).Contains("731"), "first REPL variable");
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
    using var released = await http.DeleteAsync("/api/leases/" + leaseNumber, ct);
    Check(released.IsSuccessStatusCode && (await Call(warm, "1")).IsError == true, "dashboard release");
    await WaitUntil(async () => (await Snapshot()).Count == 1);
    Console.WriteLine("Passed: queued cancellation, in-flight completion, warm reuse, admin release, timed recycling.");

    var doomed = await Acquire(primary);
    var interrupted = Call(doomed, "import time\ntime.sleep(30)\n'finished'");
    await WaitUntil(async () => (await Snapshot()).Any(w => (string?)w!["workdir"] == primary && (bool)w["running"]!));
    using (var process = Process.GetProcessById((int)(await Snapshot()).Single(w => (string?)w!["workdir"] == primary)!["pid"]!))
        process.Kill();
    var unknown = await interrupted.WaitAsync(TimeSpan.FromSeconds(10), ct);
    Check(unknown.IsError == true && Text(unknown).Contains("执行结果未知"), "backend loss reports unknown outcome: " + Text(unknown));
    Check((await Call(doomed, "1")).IsError == true && Text(await Call(third, "17")).Contains("17"), "failed worker isolated");
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
}
finally
{
    if (hostStarted && !host.HasExited) { host.Kill(); await host.WaitForExitAsync(); }
    // Git objects can be read-only on Windows. Only remove this test's freshly created directory.
    foreach (var file in Directory.EnumerateFiles(sandbox, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(sandbox, true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Git(params string[] args)
{
    var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var git = Process.Start(start)!;
    await git.WaitForExitAsync();
    Check(git.ExitCode == 0, "git fixture setup");
}
