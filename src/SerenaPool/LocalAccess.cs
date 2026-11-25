using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SerenaPool;

internal sealed class LocalAccess(string secret)
{
    private readonly byte[] expected = Encoding.UTF8.GetBytes(secret);

    public static LocalAccess Load(IConfiguration config)
    {
        if (config["SerenaPool:Token"] is { Length: > 0 } configured) return new(configured);
        var dir = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerenaPool"));
        var path = Path.Combine(dir.FullName, "access-token");
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(dir.FullName, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporary, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { /* Another host finished initialization first. */ }
            }
            finally { File.Delete(temporary); }
        }
        var token = File.ReadAllText(path).Trim();
        if (token.Length == 0) throw new InvalidDataException($"本机凭证文件为空：{path}");
        return new(token);
    }

    public async Task Invoke(HttpContext context, RequestDelegate next)
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
        if (request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/mcp"))
        {
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..]), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
        }
        await next(context);
    }
}
