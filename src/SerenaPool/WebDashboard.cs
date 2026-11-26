using System.ComponentModel;
using System.Diagnostics;

namespace SerenaPool;

internal static class WebDashboard
{
    public static void Configure(WebApplication app, Uri address, bool openOnLaunch)
    {
        app.MapGet("/api/workers", (InstancePool pool) => pool.Snapshot());
        app.MapDelete("/api/leases/{number:long}", (long number, InstancePool pool) =>
            pool.ReleaseByNumber(number) ? Results.NoContent() : Results.NotFound());
        app.MapDelete("/api/workers/{number:long}", async (long number, InstancePool pool) =>
            await pool.RecycleAsync(number) ? Results.NoContent() : Results.Conflict("仅可回收空闲实例。"));

        foreach (var (route, asset, mime) in new[] { ("/", "index.html", "text/html; charset=utf-8"),
            ("/app.css", "app.css", "text/css"), ("/app.js", "app.js", "text/javascript"),
            ("/favicon.ico", "favicon.ico", "image/x-icon") })
            app.MapGet(route, () => Results.Stream(typeof(WebDashboard).Assembly.GetManifestResourceStream($"SerenaPool.Web.{asset}")!, mime));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (OperatingSystem.IsWindows() && Environment.UserInteractive)
                WindowsDashboardTray.Start(address, openOnLaunch, app.Lifetime.ApplicationStopping);
            else if (openOnLaunch)
                OpenBrowser(address);
        });
    }

    public static void OpenBrowser(Uri address)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"无法打开 Serena Pool 管理页 {address}：{error.Message}");
        }
    }
}
