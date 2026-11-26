using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace SerenaPool;

internal sealed record ReplUsage(
    [property: JsonPropertyName("failed")] bool Failed,
    [property: JsonPropertyName("tools")] Dictionary<string, int> Tools);

internal static class ReplTelemetry
{
    private const string InstallHook = """
        from contextvars import ContextVar
        from functools import wraps
        from json import dumps
        from serena.repl.facade import FacadeMethod
        from serena.repl.repl import SerenaRepl, SerenaReplEntrypoint

        current = ContextVar("serena_pool_usage", default=None)
        execute = SerenaRepl.execute
        format_error = SerenaRepl._format_error
        qualified_name = FacadeMethod.qualified_name.fget
        entrypoints = ("info", "overview", "vars", "clear")

        def count(original, get_name):
            @wraps(original)
            def counted(self, *args, **kwargs):
                usage = current.get()
                if usage is not None and isinstance(name := get_name(self), str):
                    tools = usage["tools"]
                    tools[name] = tools.get(name, 0) + 1
                return original(self, *args, **kwargs)
            return counted

        @wraps(format_error)
        def observed_error(self, *args, **kwargs):
            usage = current.get()
            if usage is not None:
                usage["failed"] = True
            return format_error(self, *args, **kwargs)

        @wraps(execute)
        def observed_execute(self, *args, **kwargs):
            if current.get() is not None:
                return execute(self, *args, **kwargs)
            usage = {"failed": False, "tools": {}}
            token = current.set(usage)
            try:
                result = execute(self, *args, **kwargs)
            finally:
                current.reset(token)
            if not isinstance(result, str):
                return result
            return result + marker + dumps(usage, separators=(",", ":"))

        # Resolve the hooks before applying any of them.
        entrypoint_hooks = {
            name: count(getattr(SerenaReplEntrypoint, name), lambda _, name=name: "s." + name)
            for name in entrypoints
        }
        FacadeMethod.__call__ = count(FacadeMethod.__call__, qualified_name)
        for name, hook in entrypoint_hooks.items():
            setattr(SerenaReplEntrypoint, name, hook)
        SerenaRepl._format_error = observed_error
        SerenaRepl.execute = observed_execute
        """;

    public static string Bootstrap(string marker) =>
        "exec(" + JsonSerializer.Serialize(InstallHook) + ", {'marker': " + JsonSerializer.Serialize(marker) + "})";

    public static ReplUsage? Extract(CallToolResult result, string marker)
    {
        var content = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        if (content is null) return null;
        var original = content.Text;
        var start = original.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        content.Text = original[..start];
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured &&
            structured.TryGetProperty("result", out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() == original)
        {
            var json = JsonObject.Create(structured)!;
            json["result"] = content.Text;
            result.StructuredContent = JsonSerializer.SerializeToElement(json);
        }

        try
        {
            return JsonSerializer.Deserialize<ReplUsage>(original.AsSpan(start + marker.Length)) is { Tools: not null } usage
                ? usage : null;
        }
        catch (JsonException) { return null; }
    }
}
