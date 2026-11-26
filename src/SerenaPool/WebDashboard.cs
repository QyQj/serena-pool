using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SerenaPool;

internal static class WebDashboard
{
    private static readonly Regex PageToken = new(@"\{\{([A-Za-z]+)\}\}", RegexOptions.CultureInvariant);

    public static void Configure(WebApplication app, Uri address, bool openOnLaunch)
    {
        app.MapGet("/api/workers", (InstancePool pool) => pool.Snapshot());
        app.MapDelete("/api/leases/{number:long}", (long number, InstancePool pool) =>
            pool.ReleaseByNumber(number) ? Results.NoContent() : Results.NotFound());
        app.MapDelete("/api/workers/{number:long}", async (long number, InstancePool pool, HttpRequest request) =>
            await pool.RecycleAsync(number) ? Results.NoContent() :
                Results.Conflict(Messages.For(ForRequest(request), "DashboardConflict")));

        var page = ReadAsset("index.html");
        var script = ReadAsset("app.js");
        app.MapGet("/", (HttpContext context) =>
        {
            var language = ForRequest(context.Request);
            var alternate = language.Equals(English) ? SimplifiedChinese : English;
            context.Response.Headers.ContentLanguage = language.Name;
            var html = PageToken.Replace(page, match => WebUtility.HtmlEncode(match.Groups[1].Value switch
            {
                "Language" => language.Name,
                "AlternateLanguage" => alternate.Name,
                "AlternateLanguageLabel" => alternate.Equals(English) ? "English" : "简体中文",
                var key => Messages.For(language, key)
            }));
            return Results.Content(html, "text/html; charset=utf-8");
        });
        app.MapGet("/app.js", (HttpContext context) =>
        {
            var language = ForRequest(context.Request);
            context.Response.Headers.ContentLanguage = language.Name;
            return Results.Content(script.Replace("__DASHBOARD_MESSAGES__", JsonSerializer.Serialize(DashboardText(language)),
                StringComparison.Ordinal), "text/javascript; charset=utf-8");
        });
        app.MapGet("/app.css", () => Results.Stream(OpenAsset("app.css"), "text/css"));
        app.MapGet("/favicon.ico", () => Results.Stream(OpenAsset("favicon.ico"), "image/x-icon"));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (OperatingSystem.IsWindows() && Environment.UserInteractive)
                WindowsDashboardTray.Start(address, openOnLaunch, app.Lifetime.ApplicationStopping);
            else if (openOnLaunch)
                OpenBrowser(address);
        });
    }

    private static Stream OpenAsset(string name) =>
        typeof(WebDashboard).Assembly.GetManifestResourceStream($"SerenaPool.Web.{name}")!;

    private static string ReadAsset(string name)
    {
        using var reader = new StreamReader(OpenAsset(name));
        return reader.ReadToEnd();
    }

    public static void OpenBrowser(Uri address)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine(LocalText("DashboardOpenFailed", address, error.Message));
        }
    }
}
