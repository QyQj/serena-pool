using System.Net;

namespace SerenaPool;

internal static class LocalAccess
{
    public static async Task Invoke(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var host = request.Host.Host;
        var origin = request.Headers.Origin;
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)) ||
            origin.Count > 0 && !string.Equals(origin.ToString(), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        await next(context);
    }
}
