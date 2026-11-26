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
        var pipe = new LocalPipeAddress(address.Port);
        var log = app.Logger;
        var coreUnavailable = 0;
        app.MapGet("/api/workers", (HttpRequest request) => ForwardAsync(request, DashboardOperation.Workers));
        app.MapGet("/api/calls", (HttpRequest request) => ForwardAsync(request, DashboardOperation.Calls));
        app.MapDelete("/api/leases/{number:long}", (long number, HttpRequest request) =>
            ForwardAsync(request, DashboardOperation.Release, number));
        app.MapDelete("/api/workers/{number:long}", (long number, HttpRequest request) =>
            ForwardAsync(request, DashboardOperation.Recycle, number));

        async Task<IResult> ForwardAsync(HttpRequest request, DashboardOperation operation, long number = 0)
        {
            try
            {
                var reply = await DashboardPipeServer.SendAsync(pipe, new(operation, number), request.HttpContext.RequestAborted);
                if (Interlocked.Exchange(ref coreUnavailable, 0) == 1)
                    log.LogInformation("Management pipe connection restored");
                return reply.Status switch
                {
                    200 when reply.Payload is not null => Results.Json(reply.Payload),
                    204 => Results.NoContent(),
                    400 => Results.BadRequest(),
                    404 => Results.NotFound(),
                    409 => Results.Conflict(Messages.For(ForRequest(request), "DashboardConflict")),
                    500 => Results.StatusCode(StatusCodes.Status500InternalServerError),
                    _ => Results.StatusCode(StatusCodes.Status502BadGateway)
                };
            }
            catch (TimeoutException)
            {
                if (Interlocked.Exchange(ref coreUnavailable, 1) == 0)
                    log.LogWarning("Management pipe unavailable for pool address {Address}", address);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                log.LogWarning(error, "Management pipe disconnected");
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }

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
                WindowsDashboardTray.Start(address, openOnLaunch, app.Lifetime.ApplicationStopping, log);
            else if (openOnLaunch)
                OpenBrowser(address, log);
        });
    }

    private static Stream OpenAsset(string name) =>
        typeof(WebDashboard).Assembly.GetManifestResourceStream($"SerenaPool.Web.{name}")!;

    private static string ReadAsset(string name)
    {
        using var reader = new StreamReader(OpenAsset(name));
        return reader.ReadToEnd();
    }

    public static void OpenBrowser(Uri address, ILogger log)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            log.LogWarning(error, "{Message}", LocalText("DashboardOpenFailed", address, error.Message));
        }
    }
}
