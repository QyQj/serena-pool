using System.Text;
using Microsoft.AspNetCore.Connections;
using SerenaPool;

Messages.ConfigureProcess();

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
        throw new ArgumentException(LocalText("UnknownCommand", args[0]));

    var hostArgs = args[1..];
    if (hostArgs is not [] and not ["--urls", _])
        throw new ArgumentException(LocalText("McpUsage", PoolSettings.DefaultAddress));

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = hostArgs, ContentRootPath = AppContext.BaseDirectory
    });
    var configuredUrl = builder.Configuration["urls"] ?? PoolSettings.DefaultAddress;
    if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var address) || !address.IsLoopback ||
        address.Scheme is not ("http" or "https") || address.AbsolutePath != "/" ||
        address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0 ||
        !background && address.Port == 0)
        throw new ArgumentException(LocalText("LoopbackRequired"));

    if (!background)
    {
        await StdioBridge.RunAsync(address);
        return 0;
    }

    ProcessLauncher.Detach();
    var settings = builder.Configuration.GetSection("Pool").Get<PoolSettings>() ?? new();
    if (settings.IdleTimeout <= TimeSpan.Zero || settings.LeaseTimeout <= TimeSpan.Zero)
        throw new InvalidDataException(LocalText("PositiveTimeoutsRequired"));
    if (settings.Executable is { Length: > 0 })
        settings.ResolveExecutable();

    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(address.GetLeftPart(UriPartial.Authority));
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<InstancePool>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InstancePool>());
    builder.Services.AddMcpServer().WithHttpTransport().WithTools<PoolTools>();
    builder.Services.AddHostedService<LocalPipeServer>();

    await using var app = builder.Build();
    app.Use(LocalAccess.Invoke);
    var pool = app.Services.GetRequiredService<InstancePool>();
    app.UseWhen(context => context.Request.Path.StartsWithSegments("/mcp"),
        branch => branch.Use(pool.TrackHttpRequestAsync));
    app.MapMcp("/mcp");
    if (settings.WebDashboard)
        WebDashboard.Configure(app, address, settings.WebDashboardOpenOnLaunch);

    try { await app.RunAsync(); }
    catch (IOException ex) when (ex.InnerException is AddressInUseException)
    {
        throw new IOException(LocalText("PoolAddressOccupied", address), ex);
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
