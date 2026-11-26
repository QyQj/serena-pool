global using static SerenaPool.Messages;

using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace SerenaPool;

internal static class Messages
{
    public static CultureInfo English { get; } = CultureInfo.GetCultureInfo("en");
    public static CultureInfo SimplifiedChinese { get; } = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly ResourceManager Resources = new("SerenaPool.Strings", typeof(Messages).Assembly);

    public static void ConfigureProcess()
    {
        var culture = SupportedCulture(Environment.GetEnvironmentVariable("SERENA_POOL_LANGUAGE"))
            ?? SupportedCulture(CultureInfo.CurrentUICulture.Name) ?? English;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string LocalText(string key, params object?[] args)
    {
        var culture = CultureInfo.CurrentUICulture;
        var template = For(culture, key);
        return args.Length == 0 ? template : string.Format(culture, template, args);
    }

    public static string For(CultureInfo culture, string key) =>
        Resources.GetString(key, culture) ?? throw new InvalidOperationException($"Missing message resource: {key}");

    public static CultureInfo ForRequest(HttpRequest request)
    {
        if (SupportedCulture(request.Query["lang"].FirstOrDefault()) is { } requested)
            return requested;
        foreach (var preference in request.GetTypedHeaders().AcceptLanguage
            .Where(value => value.Quality is not <= 0).OrderByDescending(value => value.Quality ?? 1))
            if (SupportedCulture(preference.Value.Value) is { } culture)
                return culture;
        return CultureInfo.CurrentUICulture;
    }

    public static Dictionary<string, string> DashboardText(CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: true)!
            .Cast<DictionaryEntry>().Where(entry => ((string)entry.Key).StartsWith("Dashboard", StringComparison.Ordinal))
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);

    private static CultureInfo? SupportedCulture(string? name)
    {
        var language = name?.Split('-', '_')[0];
        return language?.ToLowerInvariant() switch
        {
            "en" => English,
            "zh" => SimplifiedChinese,
            _ => null
        };
    }
}

internal sealed class LocalizedDescriptionAttribute(string key) : DescriptionAttribute
{
    public override string Description => LocalText(key);
}
