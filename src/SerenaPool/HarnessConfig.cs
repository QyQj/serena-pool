using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace SerenaPool;

internal sealed class HarnessConfig
{
    public const string Server = "serena-pool";
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private static readonly string[] InheritedEnvironment = ["Pool__Executable", "SerenaPool__Token"];

    private string source;
    private readonly bool toml;
    private readonly string section;
    private readonly JsonObject? entry;
    private readonly DocumentSyntax? document;
    private readonly TomlTable? model;
    private readonly TomlTable? table;

    public HarnessConfig(string? contents, string format)
    {
        toml = format == "codex";
        section = toml ? "mcp_servers" : format == "opencode" ? "mcp" : "mcpServers";
        source = contents ?? (toml ? "" : "{}\r\n");

        if (toml)
        {
            document = Toml.Parse(source.Length == 0 || source.EndsWith('\n') ? source : source + "\r\n");
            if (!document.TryToModel(out model, out _) || model is null)
                throw new InvalidDataException("Codex TOML 配置无效。");
            if (model.TryGetValue(section, out var servers))
                table = ModelValue(GetTable(servers, section), Server) is { } value ? GetTable(value, Server) : null;
        }
        else
        {
            var root = ReadJson(source);
            entry = ObjectProperty(ObjectProperty(root, section), Server);
        }
    }

    public bool Exists => entry is not null || table is not null;

    public (string Command, string[] Args) ReadLaunch()
    {
        if (toml)
            return (ModelValue(table, "command") as string ?? "",
                ModelValue(table, "args") is TomlArray array ? array.Select(StringArgument).ToArray() : []);
        if (section == "mcp")
        {
            var command = JsonArguments(entry?["command"]);
            return command.Length == 0 ? ("", []) : (command[0], command[1..]);
        }
        return (entry?["command"]?.GetValue<string>() ?? "", JsonArguments(entry?["args"]));
    }

    public bool HasLiteralToken()
    {
        var env = toml ? ModelValue(table, "env") as TomlTable : null;
        var json = toml ? null : ObjectProperty(entry, section == "mcp" ? "environment" : "env");
        foreach (var key in new[] { "SerenaPool__Token", "SerenaPool:Token" })
        {
            var value = toml ? ModelValue(env, key) as string : json?[key]?.GetValue<string>();
            if (!string.IsNullOrEmpty(value) && !(value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}') ||
                section == "mcp" && value.StartsWith("{env:", StringComparison.Ordinal) && value.EndsWith('}')))
                return true;
        }
        return false;
    }

    public string SetLaunch(string format, string command, string[] args, JsonObject? claudeEntry)
    {
        if (toml)
            return SetCodex(command, args);

        if (entry is null)
        {
            var value = claudeEntry ?? new JsonObject();
            if (section == "mcp")
            {
                value["type"] = "local";
                value["command"] = ArrayNode([command, .. args]);
            }
            else
            {
                value["command"] = command;
                value["args"] = ArrayNode(args);
                if (format is "cursor" or "copilot")
                    value["type"] = "stdio";
                if (format == "copilot")
                {
                    value["tools"] = ArrayNode(["*"]);
                    value["env"] = new JsonObject(InheritedEnvironment.Select(name =>
                        KeyValuePair.Create<string, JsonNode?>(name, EnvironmentReference(name))));
                }
            }

            var root = ReadJson(source);
            return root.ContainsKey(section) ? SetJson(source, [section, Server], value) :
                SetJson(source, [section], new JsonObject { [Server] = value });
        }

        if (section == "mcp")
            return SetJson(source, [section, Server, "command"], ArrayNode([command, .. args]));

        var previous = ReadLaunch();
        if (previous.Command != command)
            source = SetJson(source, [section, Server, "command"], JsonValue.Create(command)!);
        if (!previous.Args.SequenceEqual(args))
            source = SetJson(source, [section, Server, "args"], ArrayNode(args));
        if (format == "copilot")
        {
            var env = ObjectProperty(entry, "env");
            if (env is null)
                return SetJson(source, [section, Server, "env"], new JsonObject(InheritedEnvironment.Select(name =>
                    KeyValuePair.Create<string, JsonNode?>(name, EnvironmentReference(name)))));
            foreach (var name in InheritedEnvironment)
                if (!env.ContainsKey(name))
                    source = SetJson(source, [section, Server, "env", name], EnvironmentReference(name));
        }
        return source;
    }

    private string SetCodex(string command, string[] args)
    {
        var forwarded = ModelValue(table, "env_vars") switch
        {
            null => new TomlArray(),
            TomlArray values => values,
            _ => throw new InvalidDataException("Codex env_vars 必须为数组。")
        };
        foreach (var name in InheritedEnvironment)
            if (!Forwards(forwarded, name))
                forwarded.Add(name);
        var updates = new Dictionary<string, object>
        {
            ["command"] = command, ["args"] = ToTomlArray(args),
            ["env_vars"] = forwarded
        };

        var syntax = document!.Tables.OfType<TableSyntax>().FirstOrDefault(candidate =>
            KeyText(candidate.Name?.Key) == section && candidate.Name?.DotKeys.ChildrenCount == 1 &&
            KeyText(candidate.Name.DotKeys.GetChild(0)?.Key) == Server);

        if (syntax is not null && updates.Keys.All(key => !table!.ContainsKey(key) ||
            syntax.Items.Any(item => item.Key?.DotKeys.ChildrenCount == 0 && KeyText(item.Key.Key) == key)))
        {
            foreach (var (key, value) in updates)
            {
                var parsed = Toml.Parse(Toml.FromModel(new TomlTable { [key] = value }));
                var replacement = parsed.KeyValues.GetChild(0)!;
                var existing = syntax.Items.FirstOrDefault(item => item.Key?.DotKeys.ChildrenCount == 0 && KeyText(item.Key.Key) == key);
                if (existing is null)
                {
                    parsed.KeyValues.RemoveChildAt(0);
                    syntax.Items.Add(replacement);
                }
                else
                {
                    var updated = replacement.Value!;
                    replacement.Value = null;
                    updated.LeadingTrivia = existing.Value!.LeadingTrivia;
                    updated.TrailingTrivia = existing.Value.TrailingTrivia;
                    if (existing.Value is StringValueSyntax oldString && updated is StringValueSyntax newString)
                        CopyTokenTrivia(oldString.Token, newString.Token);
                    if (existing.Value is ArraySyntax oldArray && updated is ArraySyntax newArray)
                    {
                        CopyTokenTrivia(oldArray.OpenBracket, newArray.OpenBracket);
                        CopyTokenTrivia(oldArray.CloseBracket, newArray.CloseBracket);
                    }
                    existing.Value = updated;
                }
            }
            return document.ToString();
        }

        var servers = model!.TryGetValue(section, out var existingServers) ? GetTable(existingServers, section) : new();
        var target = table ?? new TomlTable();
        foreach (var (key, value) in updates)
            target[key] = value;
        if (table is null)
        {
            var append = Toml.FromModel(new TomlTable { [section] = new TomlTable { [Server] = target } });
            var combined = source + "\r\n" + append.ReplaceLineEndings("\r\n");
            if (Toml.Parse(combined).TryToModel<TomlTable>(out _, out _))
                return combined;
        }
        servers[Server] = target;
        model[section] = servers;
        return Toml.FromModel(model).ReplaceLineEndings(source.Contains("\r\n") ? "\r\n" : "\n");
    }

    public bool ForwardsEnvironment(string format) => format switch
    {
        "codex" => ModelValue(table, "env_vars") is TomlArray values &&
            InheritedEnvironment.All(name => Forwards(values, name)),
        "copilot" => ObjectProperty(entry, "env") is { } env && InheritedEnvironment.All(env.ContainsKey),
        _ => true
    };

    private static JsonValue EnvironmentReference(string name) =>
        JsonValue.Create("${" + name + ":-" + PoolSettings.DefaultEnvironmentValue + "}")!;

    private static bool Forwards(TomlArray values, string name) => values.Any(value => value switch
    {
        string variable => variable == name,
        TomlTable variable => ModelValue(variable, "name") as string == name &&
            (ModelValue(variable, "source") as string is null or "local"),
        _ => false
    });

    private static void CopyTokenTrivia(SyntaxToken? previous, SyntaxToken? next)
    {
        if (previous is null || next is null)
            return;
        next.LeadingTrivia = previous.LeadingTrivia;
        next.TrailingTrivia = previous.TrailingTrivia;
    }

    private static TomlArray ToTomlArray(IEnumerable<string> values)
    {
        var array = new TomlArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static string? KeyText(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text,
        StringValueSyntax quoted => quoted.Value,
        _ => null
    };

    private static TomlTable GetTable(object? value, string key) =>
        value as TomlTable ?? throw new InvalidDataException($"TOML 项 {key} 必须为表。");

    private static object? ModelValue(TomlTable? table, string key) =>
        table is not null && table.TryGetValue(key, out var value) ? value : null;

    internal static JsonObject ReadJson(string source) =>
        JsonNode.Parse(source, documentOptions: JsonOptions) as JsonObject ??
        throw new InvalidDataException("JSON 配置的顶层必须为对象。");

    internal static JsonObject? ObjectProperty(JsonObject? root, string name)
    {
        if (root is null || !root.TryGetPropertyValue(name, out var value))
            return null;
        return value as JsonObject ?? throw new InvalidDataException($"JSON 项 {name} 必须为对象。");
    }

    private static JsonArray ArrayNode(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static string[] JsonArguments(JsonNode? value) => value is null ? [] :
        value.AsArray().Select(item => StringArgument(item?.GetValue<string>())).ToArray();
    private static string StringArgument(object? value) => value as string ??
        throw new InvalidDataException("MCP 程序和参数必须为字符串。");

    private static string SetJson(string text, string[] path, JsonNode value)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = new string(' ', path.Length * 2);
        var serialized = value.ToJsonString(PrettyJson).ReplaceLineEndings("\n").Replace("\n", newline + indent);
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
        });

        reader.Read();
        for (var i = 0; i < path.Length; i++)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException($"JSON 配置路径无效：{path[i]}。");
            var position = checked((int)reader.BytesConsumed);
            var search = reader;
            search.Read();
            var hasProperties = search.TokenType == JsonTokenType.PropertyName;
            var found = false;
            while (search.TokenType == JsonTokenType.PropertyName)
            {
                var matches = search.ValueTextEquals(path[i]);
                search.Read();
                if (matches)
                {
                    found = true;
                    reader = search;
                    break;
                }
                search.Skip();
                search.Read();
            }

            if (!found)
            {
                if (i != path.Length - 1)
                    throw new InvalidDataException($"JSON 配置路径不存在：{path[i]}。");
                var item = newline + indent + JsonSerializer.Serialize(path[i]) + ": " + serialized +
                    (hasProperties ? "," : newline + indent[2..]);
                return ReplaceJson(bytes, position, position, item);
            }
        }

        var start = checked((int)reader.TokenStartIndex);
        reader.Skip();
        return ReplaceJson(bytes, start, checked((int)reader.BytesConsumed), serialized);
    }

    private static string ReplaceJson(byte[] bytes, int start, int end, string value) =>
        Encoding.UTF8.GetString(bytes.AsSpan(0, start)) + value + Encoding.UTF8.GetString(bytes.AsSpan(end));
}
