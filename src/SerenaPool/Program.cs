using System.Text;
using Microsoft.AspNetCore.Connections;
using SerenaPool;

if (args is ["--launch-serena", var executable, var workdir])
    return SerenaProcess.Launch(executable, workdir);
if (args is ["--launch-unix", var parent, var command, var directory])
    return await ProcessLauncher.LaunchUnixAsync(int.Parse(parent), command, directory);

if (Console.IsOutputRedirected)
    Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine($"""
        serena-pool mcp [--urls {PoolSettings.DefaultAddress}]
        serena-pool setup [harness ...]
        """);
    return 0;
}

try
{
    if (args[0] == "setup")
        return HarnessSetup.Run(args[1..]);

    var background = args[0] == "--background-serve";
    if (!background && args[0] != "mcp")
        throw new ArgumentException($"未知命令 {args[0]}。使用 --help 查看用法。");

    var hostArgs = args[1..];
    if (hostArgs is not [] and not ["--urls", _])
        throw new ArgumentException($"用法：serena-pool mcp [--urls {PoolSettings.DefaultAddress}]");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = hostArgs, ContentRootPath = AppContext.BaseDirectory
    });
    var configuredUrl = builder.Configuration["urls"] ?? PoolSettings.DefaultAddress;
    if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var address) || !address.IsLoopback ||
        address.Scheme is not ("http" or "https") || address.AbsolutePath != "/" ||
        address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0 ||
        !background && address.Port == 0)
        throw new ArgumentException("请为 --urls 指定包含固定端口的本机回环地址。");

    if (!background)
    {
        await StdioBridge.RunAsync(address);
        return 0;
    }

    ProcessLauncher.Detach();
    var settings = builder.Configuration.GetSection("Pool").Get<PoolSettings>() ?? new();
    if (settings.IdleTimeout <= TimeSpan.Zero || settings.LeaseTimeout <= TimeSpan.Zero)
        throw new InvalidDataException("IdleTimeout 和 LeaseTimeout 必须大于零。");
    if (settings.Executable is { Length: > 0 } and not PoolSettings.DefaultEnvironmentValue)
        settings.ResolveExecutable();

    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(address.GetLeftPart(UriPartial.Authority));
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<InstancePool>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InstancePool>());
    builder.Services.AddMcpServer().WithHttpTransport().WithTools<PoolTools>();
    builder.Services.AddHostedService<LocalPipeServer>();

    await using var app = builder.Build();
    app.Use(LocalAccess.Load(builder.Configuration).Invoke);
    app.MapMcp("/mcp");
    app.MapGet("/api/workers", (InstancePool pool) => pool.Snapshot()).AllowAnonymous();
    app.MapDelete("/api/leases/{number:long}", (long number, InstancePool pool) =>
        pool.ReleaseByNumber(number) ? Results.NoContent() : Results.NotFound()).AllowAnonymous();
    app.MapDelete("/api/workers/{number:long}", async (long number, InstancePool pool) =>
        await pool.RecycleAsync(number) ? Results.NoContent() : Results.Conflict("仅可回收空闲实例。")).AllowAnonymous();

    foreach (var (route, asset, mime) in new[] { ("/", "index.html", "text/html; charset=utf-8"),
        ("/app.css", "app.css", "text/css"), ("/app.js", "app.js", "text/javascript") })
        app.MapGet(route, () => Results.Stream(typeof(Program).Assembly.GetManifestResourceStream($"SerenaPool.Web.{asset}")!, mime));

    try { await app.RunAsync(); }
    catch (IOException ex) when (ex.InnerException is AddressInUseException)
    {
        throw new IOException($"地址 {address} 已被占用。请关闭占用进程，或用 mcp --urls 指定其他本机端口。", ex);
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
