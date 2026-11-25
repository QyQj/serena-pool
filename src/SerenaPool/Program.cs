using SerenaPool;

if (args is ["--launch-serena", var executable, var workdir])
    return SerenaProcess.Launch(executable, workdir);

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Serena Pool 需要 Windows Job Object。");
var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Pool").Get<PoolSettings>() ?? new();
if (settings.IdleTimeout <= TimeSpan.Zero || settings.LeaseTimeout <= TimeSpan.Zero)
    throw new InvalidDataException("IdleTimeout 和 LeaseTimeout 必须大于零。");
var urls = (builder.Configuration["urls"] ?? "http://127.0.0.1:47321").Split(';');
foreach (var url in urls)
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
        throw new InvalidDataException("服务只能监听回环地址。");
builder.WebHost.UseUrls(urls);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<InstancePool>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InstancePool>());
builder.Services.AddMcpServer().WithHttpTransport().WithTools<PoolTools>();

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
