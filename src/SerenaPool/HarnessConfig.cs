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

    private const string ExecutableVariable = "Pool__Executable";
    private string EnvironmentKey => section == "mcp" ? "environment" : "env";

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

    public string SetLaunch(string format, string command, string[] args, string executable, JsonObject? claudeEntry)
    {
        if (toml)
            return SetCodex(command, args, executable);

        var env = (JsonObject?)ObjectProperty(entry ?? claudeEntry, EnvironmentKey)?.DeepClone() ?? new();
        foreach (var key in env.Select(item => item.Key).Where(IsManagedVariable).ToArray())
            env.Remove(key);
        env[ExecutableVariable] = executable;

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
                    value["tools"] = ArrayNode(["*"]);
            }

            value[EnvironmentKey] = env;
            var root = ReadJson(source);
            return root.ContainsKey(section) ? SetJson(source, [section, Server], value) :
                SetJson(source, [section], new JsonObject { [Server] = value });
        }

        var previous = ReadLaunch();
        if (section == "mcp")
        {
            if (previous.Command != command || !previous.Args.SequenceEqual(args))
                source = SetJson(source, [section, Server, "command"], ArrayNode([command, .. args]));
        }
        else
        {
            if (previous.Command != command)
                source = SetJson(source, [section, Server, "command"], JsonValue.Create(command)!);
            if (!previous.Args.SequenceEqual(args))
                source = SetJson(source, [section, Server, "args"], ArrayNode(args));
        }
        return SetJson(source, [section, Server, EnvironmentKey], env);
    }

    private string SetCodex(string command, string[] args, string executable)
    {
        var forwarded = new TomlArray();
        foreach (var value in ForwardedVariables())
            if (!IsManagedVariable(ForwardedName(value)))
                forwarded.Add(value);

        var env = new TomlTable(inline: true);
        foreach (var (name, value) in CodexEnvironment())
            if (!IsManagedVariable(name))
                env[name] = value;
        env[ExecutableVariable] = executable;

        var envSyntax = document!.Tables.OfType<TableSyntax>().FirstOrDefault(candidate =>
            KeyText(candidate.Name?.Key) == section && candidate.Name?.DotKeys.ChildrenCount == 2 &&
            KeyText(candidate.Name.DotKeys.GetChild(0)?.Key) == Server &&
            KeyText(candidate.Name.DotKeys.GetChild(1)?.Key) == "env");

        var updates = new Dictionary<string, object>
        {
            ["command"] = command, ["args"] = ToTomlArray(args)
        };
        if (envSyntax is null)
            updates["env"] = env;
        if (forwarded.Count > 0)
            updates["env_vars"] = forwarded;

        var syntax = document!.Tables.OfType<TableSyntax>().FirstOrDefault(candidate =>
            KeyText(candidate.Name?.Key) == section && candidate.Name?.DotKeys.ChildrenCount == 1 &&
            KeyText(candidate.Name.DotKeys.GetChild(0)?.Key) == Server);

        if (syntax is not null && updates.Keys.All(key => !table!.ContainsKey(key) ||
            syntax.Items.Any(item => item.Key?.DotKeys.ChildrenCount == 0 && KeyText(item.Key.Key) == key)))
        {
            if (forwarded.Count == 0)
                RemoveTomlProperties(syntax, name => name == "env_vars");
            if (envSyntax is not null)
            {
                RemoveTomlProperties(envSyntax, IsManagedVariable);
                var parsed = Toml.Parse(Toml.FromModel(new TomlTable { [ExecutableVariable] = executable }));
                var setting = parsed.KeyValues.GetChild(0)!;
                parsed.KeyValues.RemoveChildAt(0);
                envSyntax.Items.Add(setting);
            }

            foreach (var (key, value) in updates)
            {
                var parsed = Toml.Parse(value is TomlTable environment ?
                    FormatInlineEnvironment(key, environment) : Toml.FromModel(new TomlTable { [key] = value }));
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
                    if (existing.Value is InlineTableSyntax oldInline && updated is InlineTableSyntax newInline)
                    {
                        CopyTokenTrivia(oldInline.OpenBrace, newInline.OpenBrace);
                        CopyTokenTrivia(oldInline.CloseBrace, newInline.CloseBrace);
                    }
                    existing.Value = updated;
                }
            }
            return document.ToString();
        }

        var servers = model!.TryGetValue(section, out var existingServers) ? GetTable(existingServers, section) : new();
        var target = table ?? new TomlTable();
        if (forwarded.Count == 0)
            target.Remove("env_vars");
        foreach (var (key, value) in updates)
            target[key] = value;
        target["env"] = env;
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

    private static string FormatInlineEnvironment(string key, TomlTable environment)
    {
        var serialized = Toml.Parse(Toml.FromModel(environment));
        if (serialized.KeyValues.ChildrenCount != environment.Count)
            throw new InvalidDataException("Codex MCP env 中的环境变量必须为键值对。");
        var fields = string.Join(", ", serialized.KeyValues.Select(item => item.ToString().Trim()));
        return $"{key} = {{ {fields} }}\r\n";
    }

    public bool HasConfiguredExecutable(string executable)
    {
        if (toml)
        {
            var env = CodexEnvironment();
            return ModelValue(env, ExecutableVariable) as string == executable &&
                env.Keys.Count(IsManagedVariable) == 1 &&
                !ForwardedVariables().Any(value => IsManagedVariable(ForwardedName(value)));
        }
        var json = ObjectProperty(entry, EnvironmentKey);
        return json?[ExecutableVariable] is JsonValue value && value.TryGetValue<string>(out var path) &&
            path == executable && json.Count(item => IsManagedVariable(item.Key)) == 1;
    }

    private TomlTable CodexEnvironment() => ModelValue(table, "env") is { } value ? GetTable(value, "env") : new();

    private TomlArray ForwardedVariables() => ModelValue(table, "env_vars") switch
    {
        null => new(),
        TomlArray values => values,
        _ => throw new InvalidDataException("Codex env_vars 必须为数组。")
    };

    private static string? ForwardedName(object? value) => value is TomlTable variable ?
        ModelValue(variable, "name") as string : value as string;

    private static bool IsManagedVariable(string? name) =>
        string.Equals(name, ExecutableVariable, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Pool:Executable", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SerenaPool__Token", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SerenaPool:Token", StringComparison.OrdinalIgnoreCase);

    private static void RemoveTomlProperties(TableSyntax table, Func<string?, bool> matches)
    {
        for (var i = table.Items.ChildrenCount - 1; i >= 0; i--)
            if (matches(KeyText(table.Items.GetChild(i)?.Key?.Key)))
                table.Items.RemoveChildAt(i);
    }

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
