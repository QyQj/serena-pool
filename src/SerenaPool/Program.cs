using SerenaPool;

if (args is ["--launch-serena", var executable, var workdir])
    return SerenaProcess.Launch(executable, workdir);
if (args is ["--launch-unix", var parent, var command, var directory])
    return await ProcessLauncher.LaunchUnixAsync(int.Parse(parent), command, directory);
if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("SerenaPool [serve|stdio] [--urls http://127.0.0.1:47321]\nSerenaPool token-path\nserve: HTTP MCP and Web dashboard (default). stdio: connect an MCP client to the shared local pool.");
    return 0;
}
if (args is ["token-path"]) { Console.WriteLine(LocalAccess.TokenPath); return 0; }

var mode = args.FirstOrDefault() ?? "serve";
var automatic = mode == "--background-serve";
if (mode is "serve" or "stdio" || automatic) args = args.Skip(1).ToArray();
else if (!mode.StartsWith('-')) { Console.Error.WriteLine($"未知命令 {mode}。使用 --help 查看用法。"); return 2; }
if (automatic) ProcessLauncher.Detach();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var settings = builder.Configuration.GetSection("Pool").Get<PoolSettings>() ?? new();
if (settings.IdleTimeout <= TimeSpan.Zero || settings.LeaseTimeout <= TimeSpan.Zero)
    throw new InvalidDataException("IdleTimeout 和 LeaseTimeout 必须大于零。");
var urls = (builder.Configuration["urls"] ?? "http://127.0.0.1:47321").Split(';');
foreach (var url in urls)
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https"))
        throw new InvalidDataException("服务只能监听回环地址。");
if (mode == "stdio")
{
    try { await StdioBridge.RunAsync(new Uri(urls[0]), args); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (automatic) builder.Logging.ClearProviders();
builder.WebHost.UseUrls(urls);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<InstancePool>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InstancePool>());
builder.Services.AddMcpServer().WithHttpTransport().WithTools<PoolTools>();
builder.Services.AddHostedService(sp => ActivatorUtilities.CreateInstance<LocalPipeServer>(sp, automatic));

var app = builder.Build();
app.Use(LocalAccess.Load(builder.Configuration).Invoke);
app.MapMcp("/mcp");
app.MapGet("/api/workers", (InstancePool pool) => pool.Snapshot());
app.MapDelete("/api/leases/{number:long}", (long number, InstancePool pool) =>
    pool.ReleaseByNumber(number) ? Results.NoContent() : Results.NotFound());
app.MapDelete("/api/workers/{number:long}", async (long number, InstancePool pool) =>
    await pool.RecycleAsync(number) ? Results.NoContent() : Results.Conflict("仅可回收空闲实例。"));

foreach (var (route, asset, mime) in new[] { ("/", "index.html", "text/html; charset=utf-8"),
    ("/app.css", "app.css", "text/css"), ("/app.js", "app.js", "text/javascript") })
    app.MapGet(route, () => Results.Stream(typeof(Program).Assembly.GetManifestResourceStream($"SerenaPool.Web.{asset}")!, mime));
await app.RunAsync();
return 0;
