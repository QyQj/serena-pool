using System.ComponentModel;
using System.Diagnostics;
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
        serena-pool dashboard [--urls {PoolSettings.DefaultAddress}]
        """);
    Console.WriteLine(HarnessSetup.Usage);
    return 0;
}

ProcessFileLog? fileLog = null;
ILogger? log = null;
try
{
    var mode = args[0];
    if (mode == "setup")
        return HarnessSetup.Run(args[1..]);

    int? ownerPid = null;
    var hostArgs = args[1..];
    if (mode == "--background-dashboard" && hostArgs is [var pid, ..])
    {
        ownerPid = int.Parse(pid);
        hostArgs = hostArgs[1..];
    }
    else if (mode is not ("mcp" or "dashboard" or "--core" or "--background-core"))
        throw new ArgumentException(LocalText("UnknownCommand", mode));

    var role = mode switch
    {
        "mcp" => "stdio",
        "dashboard" or "--background-dashboard" => "dashboard",
        _ => "core"
    };
    fileLog = new ProcessFileLog(role);
    log = fileLog.CreateLogger("SerenaPool");
    log.LogInformation("Starting {Role} (PID {Pid})", role, Environment.ProcessId);

    if (hostArgs is not [] and not ["--urls", _])
        throw new ArgumentException(LocalText("McpUsage", PoolSettings.DefaultAddress));

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = hostArgs, ContentRootPath = AppContext.BaseDirectory
    });
    var address = PoolSettings.ReadAddress(builder.Configuration);
    log.LogInformation("Using pool address {Address}", address);

    if (mode == "mcp")
    {
        await StdioBridge.RunAsync(address, log);
        return 0;
    }

    var settings = builder.Configuration.GetSection("Pool").Get<PoolSettings>() ?? new();
    if (mode is "dashboard" or "--background-dashboard")
    {
        if (!settings.WebDashboard)
            throw new InvalidOperationException(LocalText("DashboardDisabled"));
        if (ownerPid.HasValue) ProcessLauncher.Detach();
        await RunDashboardAsync(address, hostArgs, settings, ownerPid, fileLog);
        return 0;
    }

    if (mode == "--background-core") ProcessLauncher.Detach();
    if (settings.IdleTimeout <= TimeSpan.Zero || settings.LeaseTimeout <= TimeSpan.Zero)
        throw new InvalidDataException(LocalText("PositiveTimeoutsRequired"));
    if (settings.Executable is { Length: > 0 })
        settings.ResolveExecutable();

    RunCore(builder, settings, address, mode == "--background-core", fileLog);
    return 0;
}
catch (Exception ex)
{
    log?.LogError(ex, "Process failed");
    Console.Error.WriteLine(ex.Message);
    return 1;
}
finally
{
    log?.LogInformation("Process exiting");
    fileLog?.Dispose();
}

static void RunCore(HostApplicationBuilder builder, PoolSettings settings, Uri address, bool background, ProcessFileLog fileLog)
{
    var pipe = new LocalPipeAddress(address.Port);
    using var ownership = new Mutex(false, pipe.Name + "-owner");
    try
    {
        if (!ownership.WaitOne(0)) throw new IOException(LocalText("CoreAlreadyRunning", address.Port));
    }
    catch (AbandonedMutexException) { } // The previous owner exited and this thread now holds the mutex.

    try
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(fileLog);
        builder.Logging.AddFilter<ProcessFileLog>("SerenaPool", LogLevel.Information);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(pipe);
        builder.Services.AddSingleton<InstancePool>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InstancePool>());
        builder.Services.AddMcpServer().WithTools<PoolTools>();
        builder.Services.AddHostedService<LocalPipeServer>();
        if (settings.WebDashboard) builder.Services.AddHostedService<DashboardPipeServer>();

        var core = builder.Build();
        var log = core.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SerenaPool.Core");
        var lifetime = core.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() => log.LogInformation("Core started, dashboard enabled: {Enabled}", settings.WebDashboard));
        lifetime.ApplicationStopping.Register(() => log.LogInformation("Core stopping"));
        if (background && settings.WebDashboard)
        {
            lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    using var dashboard = Process.Start(ProcessLauncher.Self("--background-dashboard",
                        Environment.ProcessId.ToString(), "--urls", address.GetLeftPart(UriPartial.Authority)));
                    log.LogInformation("Launched dashboard process {Pid}", dashboard?.Id);
                }
                catch (Win32Exception error)
                {
                    log.LogError(error, "{Message}", LocalText("DashboardStartFailed", address));
                }
            });
        }
        core.Run();
    }
    finally { ownership.ReleaseMutex(); }
}

static async Task RunDashboardAsync(Uri address, string[] hostArgs, PoolSettings settings, int? ownerPid, ProcessFileLog fileLog)
{
    using var owner = ownerPid is { } pid ? Process.GetProcessById(pid) : null;
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = hostArgs, ContentRootPath = AppContext.BaseDirectory
    });
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddProvider(fileLog);
    builder.Logging.AddFilter<ProcessFileLog>("SerenaPool", LogLevel.Information);
    builder.Logging.AddFilter<ProcessFileLog>("Microsoft.AspNetCore.Server.Kestrel.Connections", LogLevel.Debug);
    builder.WebHost.UseUrls(address.GetLeftPart(UriPartial.Authority));

    await using var app = builder.Build();
    app.Use(LocalAccess.Invoke);
    WebDashboard.Configure(app, address, settings.WebDashboardOpenOnLaunch);
    app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Dashboard listening at {Address}", address));
    app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Dashboard stopping"));
    if (owner is not null)
    {
        owner.Exited += (_, _) => app.Lifetime.StopApplication();
        owner.EnableRaisingEvents = true;
    }
    try { await app.RunAsync(); }
    catch (IOException ex) when (ex.InnerException is AddressInUseException)
    { throw new IOException(LocalText("DashboardAddressOccupied", address), ex); }
}
