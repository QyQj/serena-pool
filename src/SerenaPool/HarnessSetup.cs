using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SerenaPool;

internal static class HarnessSetup
{
    private const string Package = "SerenaPool@";
    private static readonly string Version = typeof(HarnessSetup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "PackageVersion").Value!;
    private static readonly JsonSerializerOptions StateOptions = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var catalog = GetTargets(home);
        if (args is ["--help"] or ["-h"])
        {
            PrintUsage(catalog);
            return 0;
        }

        if (args.Length == 0)
        {
            PrintUsage(catalog);
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                return 2;
            Console.Write(LocalText("ChooseTargets"));
            args = (Console.ReadLine() ?? "").Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0)
                return 0;
        }

        var selected = args.Select(argument =>
        {
            if (int.TryParse(argument, out var index) && index >= 1 && index <= catalog.Length)
                return catalog[index - 1];
            return catalog.SingleOrDefault(target => target.Id.Equals(argument, StringComparison.OrdinalIgnoreCase)) ??
                throw new ArgumentException(LocalText("UnknownTarget", argument));
        }).Distinct().Select(target => target.ResolvePaths()).ToArray();

        using var mutex = new Mutex(false, $"{StdioBridge.PipeName(0)}-setup");
        try
        {
            if (!mutex.WaitOne(0))
                throw new IOException(LocalText("SetupBusy"));
        }
        catch (AbandonedMutexException) { /* 重新读取当前配置后再安装。 */ }

        try { Install(home, catalog, selected); }
        finally { mutex.ReleaseMutex(); }
        return 0;
    }

    private static void Install(string home, Target[] catalog, Target[] selected)
    {
        var files = new SetupFiles();
        var stateFile = files.Read(SetupFiles.PhysicalPath(Path.Combine(home, ".serena-pool", "setup.json")));
        var state = stateFile.Text is { } data ? JsonSerializer.Deserialize<InstallState>(data) ??
            throw new InvalidDataException(LocalText("InvalidSetupState", stateFile.Path)) : new([], []);
        using var stream = typeof(HarnessSetup).Assembly.GetManifestResourceStream("SerenaPool.Skill")!;
        using var reader = new StreamReader(stream);
        var skill = reader.ReadToEnd();
        var configurations = new Dictionary<string, (string Format, SetupFiles.Snapshot File)>(PoolSettings.PathComparer);
        var conflicts = new List<string>();
        var launches = new Launch?[selected.Length];
        var groups = selected.GroupBy(target => target.Skill, PoolSettings.PathComparer).ToArray();

        SetupFiles.Snapshot ConfigFile(Target target)
        {
            if (configurations.TryGetValue(target.Config, out var known))
            {
                if (known.Format != target.Format)
                    throw new IOException(LocalText("ConfigPathConflict", target.Config));
                return known.File;
            }
            var file = files.Read(target.Config);
            configurations.Add(target.Config, (target.Format, file));
            return file;
        }

        HarnessConfig ReadConfig(Target target)
        {
            try { return new(ConfigFile(target).Text, target.Format); }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            {
                throw new InvalidDataException(LocalText("ConfigReadFailed", target.Id, target.Config, error.Message), error);
            }
        }

        for (var i = 0; i < selected.Length; i++)
        {
            var target = selected[i];
            var config = ReadConfig(target);
            launches[i] = Launch.Recognize(config);
            if (config.Exists && launches[i] is null)
                conflicts.Add(LocalText("ExternalMcpEntry", target.Id, target.Config));
        }

        foreach (var group in groups)
        {
            var owners = string.Join(LocalText("ClientSeparator"), group.Select(target => target.Id));
            var skillFile = files.Read(group.Key);
            var prior = state.Skills.SingleOrDefault(item => PoolSettings.PathComparer.Equals(item.Path, group.Key));
            if (skillFile.Text is { } existing &&
                (prior is null || existing != prior.Source || !SameUtf8File(skillFile, prior.Source)))
                conflicts.Add(LocalText("SkillConflict", owners, group.Key));

            var otherTargets = catalog.Where(target =>
                PoolSettings.PathComparer.Equals(SetupFiles.PhysicalPath(target.Skill), group.Key))
                .Select(target => target.ResolvePaths()).ToList();
            otherTargets.AddRange(state.Registrations.Where(item => PoolSettings.PathComparer.Equals(item.Skill, group.Key)));
            foreach (var target in otherTargets.DistinctBy(target => target.Config, PoolSettings.PathComparer))
            {
                if (selected.Any(item => PoolSettings.PathComparer.Equals(item.Config, target.Config)))
                    continue;
                var file = ConfigFile(target);
                if (file.Text is not { } contents || !contents.Contains(Package, StringComparison.OrdinalIgnoreCase))
                    continue;
                var registration = ReadConfig(target).ReadLaunch();
                if (Launch.PinnedVersion(registration.Command, registration.Args) is { } installed &&
                    !installed.Equals(Version, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add(LocalText("SharedSkillVersion", target.Id, target.Config, installed, owners));
            }
        }

        if (conflicts.Count > 0)
            throw new InvalidOperationException(LocalText("SetupConflicts", string.Join("\n- ", conflicts)));

        var executable = new PoolSettings { Executable = Environment.GetEnvironmentVariable("Pool__Executable") }.ResolveExecutable();
        ValidateSerena(executable);
        var defaultDotnet = FindDotnet();
        var sdkVersions = new Dictionary<(string, string), int>();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previews = new Dictionary<SetupFiles.Snapshot, string>();
        for (var i = 0; i < selected.Length; i++)
        {
            var target = selected[i];
            var config = ReadConfig(target);
            var old = launches[i];
            var dotnet = old is { Version: not null } ? old.Command : defaultDotnet;
            var cwd = Environment.CurrentDirectory;
            if (!sdkVersions.TryGetValue((dotnet, cwd), out var major))
            {
                var sdk = RunCommand(dotnet, ["--version"], cwd).Trim();
                if (!int.TryParse(sdk.Split('.')[0], out major) || major < 10)
                    throw new InvalidOperationException(LocalText("DotnetRequired", dotnet));
                sdkVersions.Add((dotnet, cwd), major);
            }

            var execOptions = old?.ExecOptions.Where(option => option != "--yes") ?? [];
            string[] options = major >= 11 ? ["--yes", .. execOptions] : [.. execOptions];
            var mcpArgs = old?.McpArgs ?? [];
            string[] desired = ["tool", "exec", Package + Version, .. options, "--", "mcp", .. mcpArgs];
            if (old?.Command != dotnet || !config.ReadLaunch().Args.SequenceEqual(desired) ||
                !config.HasConfiguredExecutable(executable))
            {
                var claude = old is null && target.Id == "claude-code" ? PrepareClaude(dotnet, desired) : null;
                var updated = config.SetLaunch(target.Format, dotnet, desired, executable, claude);
                var generated = new HarnessConfig(updated, target.Format);
                var confirmed = generated.ReadLaunch();
                if (confirmed.Command != dotnet || !confirmed.Args.SequenceEqual(desired) || !generated.HasConfiguredExecutable(executable))
                    throw new InvalidDataException(LocalText("RegistrationFailed", target.Id));
                var file = ConfigFile(target);
                file.Set(updated);
                if (file.Changed)
                {
                    string[] managedArgs = ["tool", "exec", Package + Version, .. options.Where(option => option == "--yes"),
                        "--", "mcp", .. mcpArgs];
                    var managedClaude = claude is null ? null : new JsonObject { ["type"] = "stdio" };
                    previews[file] = new HarnessConfig(null, target.Format)
                        .SetLaunch(target.Format, dotnet, managedArgs, executable, managedClaude);
                }
            }
            urls.Add(mcpArgs is ["--urls", var url] ? url : PoolSettings.DefaultAddress);
            if (!state.Registrations.Any(item => item.Id == target.Id && PoolSettings.PathComparer.Equals(item.Config, target.Config)))
                state.Registrations.Add(target);
        }

        foreach (var group in groups)
        {
            files.Read(group.Key).Set(skill);
            state.Skills.RemoveAll(item => PoolSettings.PathComparer.Equals(item.Path, group.Key));
            state.Skills.Add(new(group.Key, Version, skill));
        }
        state.Registrations.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id + left.Config, right.Id + right.Config));
        state.Skills.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        stateFile.Set(JsonSerializer.Serialize(state, StateOptions).ReplaceLineEndings("\r\n") + "\r\n");
        var backups = previews.Keys.ToDictionary(file => file, files.BackUp);
        files.Commit(stateFile);

        Console.WriteLine(LocalText("SetupComplete", Version));
        foreach (var target in selected)
        {
            Console.WriteLine();
            Console.WriteLine(target.Id);
            Console.WriteLine(LocalText("McpConfigLine", target.Config));
            Console.WriteLine($"  Skill       {target.Skill}");
            var file = ConfigFile(target);
            if (!previews.TryGetValue(file, out var preview))
            {
                Console.WriteLine(LocalText("ConfigUnchanged"));
                continue;
            }

            if (backups[file] is { } backup)
                Console.WriteLine(LocalText("BackupLine", backup));
            Console.WriteLine(LocalText("ManagedSettings"));
            foreach (var line in preview.Trim().Split('\n'))
                Console.WriteLine($"    {line.TrimEnd('\r')}");
        }

        Console.WriteLine();
        Console.WriteLine(LocalText("RuntimeDetails"));
        Console.WriteLine($"  Serena      {executable}");
        foreach (var url in urls)
        {
            var address = url.TrimEnd('/');
            Console.WriteLine(LocalText("WebAddressLine", address));
            Console.WriteLine($"  HTTP MCP    {address}/mcp");
        }
    }

    private static bool SameUtf8File(SetupFiles.Snapshot file, string content) =>
        file.Original.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(content));

    private static void ValidateSerena(string executable)
    {
        string help;
        try
        {
            help = RunCommand(executable, ["start-mcp-server", "--help"], Environment.CurrentDirectory);
        }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            throw new IOException(LocalText("SerenaRunFailed", executable, error.Message), error);
        }

        if (!help.Split('\n').Any(line => line.TrimStart().StartsWith("--agent-interface ", StringComparison.Ordinal) &&
            line.Contains("repl", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(LocalText("SerenaReplRequired", executable));
    }

    private static string FindDotnet()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (path is not null && Path.IsPathFullyQualified(path) && File.Exists(path) &&
            Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(path);
        return PoolSettings.FindOnPath(name) ?? throw new FileNotFoundException(LocalText("DotnetNotFound", name));
    }

    private static JsonObject PrepareClaude(string dotnet, string[] args)
    {
        var claude = FindClient("claude") ?? throw new FileNotFoundException(LocalText("ClaudeNotFound"));
        if (OperatingSystem.IsWindows() && Path.GetExtension(claude).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var native = Path.Combine(Path.GetDirectoryName(claude)!, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            claude = File.Exists(native) ? native :
                throw new FileNotFoundException(LocalText("ClaudeNativeRequired"));
        }
        var stage = Directory.CreateTempSubdirectory("serena-pool-claude-");
        try
        {
            RunCommand(claude, ["mcp", "add", "--transport", "stdio", "--scope", "user",
                HarnessConfig.Server, "--", dotnet, .. args], stage.FullName, stage.FullName);
            var root = HarnessConfig.ReadJson(File.ReadAllText(Path.Combine(stage.FullName, ".claude.json")));
            var registered = HarnessConfig.ObjectProperty(HarnessConfig.ObjectProperty(root, "mcpServers"), HarnessConfig.Server);
            return registered is null ? throw new InvalidDataException(LocalText("ClaudeRegistrationMissing")) :
                (JsonObject)registered.DeepClone();
        }
        finally { stage.Delete(recursive: true); }
    }

    private static string RunCommand(string command, string[] args, string cwd, string? claudeDirectory = null)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (claudeDirectory is not null)
        {
            start.Environment["CLAUDE_CONFIG_DIR"] = claudeDirectory;
            start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        }
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException(LocalText("CommandStartFailed", command));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException(LocalText("CommandTimedOut", Path.GetFileName(command)));
        }
        if (process.ExitCode != 0)
            throw new IOException(LocalText("CommandFailed", Path.GetFileName(command), args[0], process.ExitCode));
        error.GetAwaiter().GetResult();
        return output.GetAwaiter().GetResult();
    }

    private static Target[] GetTargets(string home)
    {
        var shared = Path.Combine(home, ".agents", "skills");
        var claude = EnvPath("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        var antigravity = Path.Combine(home, ".gemini", "config");
        var openCode = EnvPath("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        var openJson = Path.Combine(openCode, "opencode", "opencode.json");
        var explicitOpenCode = EnvPath("OPENCODE_CONFIG");
        var openConfig = explicitOpenCode ?? (File.Exists(openJson + "c") ? openJson + "c" : openJson);
        var claudeConfig = EnvPath("CLAUDE_CONFIG_DIR") is not null ?
            Path.Combine(claude, ".claude.json") : Path.Combine(home, ".claude.json");
        Target[] targets = [
            new("codex", "codex", "codex", Path.Combine(EnvPath("CODEX_HOME") ?? Path.Combine(home, ".codex"), "config.toml"), shared),
            new("claude-code", "claude", "claude-code", claudeConfig, Path.Combine(claude, "skills")),
            new("cursor", "cursor", "cursor", Path.Combine(home, ".cursor", "mcp.json"), shared),
            new("copilot", "copilot", "copilot", Path.Combine(EnvPath("COPILOT_HOME") ?? Path.Combine(home, ".copilot"), "mcp-config.json"), shared),
            new("opencode", "opencode", "opencode", openConfig, shared),
            new("antigravity", "agy", "antigravity", Path.Combine(antigravity, "mcp_config.json"), Path.Combine(antigravity, "skills")),
            new("gemini-cli", "gemini", "gemini-cli", Path.Combine(EnvPath("GEMINI_CLI_HOME") ?? home, ".gemini", "settings.json"), shared),
        ];
        return targets.Select(target => target with { Skill = Path.Combine(target.Skill, HarnessConfig.Server, "SKILL.md") }).ToArray();
    }

    private static string? EnvPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : Path.GetFullPath(value);
    }

    private static string? FindClient(string name) => OperatingSystem.IsWindows() ?
        PoolSettings.FindOnPath(name + ".exe") ?? PoolSettings.FindOnPath(name) ??
        PoolSettings.FindOnPath(name + ".cmd") : PoolSettings.FindOnPath(name);

    private static void PrintUsage(Target[] targets)
    {
        Console.WriteLine("serena-pool setup [harness ...]");
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            var detected = FindClient(target.Executable) is not null || File.Exists(target.Config);
            Console.WriteLine($"  {i + 1}. {target.Id}{(detected ? LocalText("DetectedClient") : "")}");
        }
    }

    internal sealed record Target(string Id, string Executable, string Format, string Config, string Skill)
    {
        public Target ResolvePaths()
        {
            var skillFile = Skill;
            if (new FileInfo(skillFile).LinkTarget is not null)
                throw new IOException(LocalText("RegularSkillRequired", skillFile));
            return this with { Config = SetupFiles.PhysicalPath(Config), Skill = SetupFiles.PhysicalPath(skillFile) };
        }
    }

    private sealed record InstalledSkill(string Path, string Version, string Source);
    private sealed record InstallState(List<Target> Registrations, List<InstalledSkill> Skills);

    private sealed record Launch(string Command, string? Version, string[] ExecOptions, string[] McpArgs)
    {
        public static Launch? Recognize(HarnessConfig config)
        {
            if (!config.Exists)
                return null;
            var (command, args) = config.ReadLaunch();
            if (!Path.IsPathFullyQualified(command))
                return null;
            var name = Path.GetFileName(command);
            if ((name.Equals("SerenaPool", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("SerenaPool.exe", StringComparison.OrdinalIgnoreCase)) &&
                args is ["stdio", .. var legacy] && ValidMcpArgs(legacy))
                return new(command, null, [], legacy);

            var version = PinnedVersion(command, args);
            if (version is null)
                return null;
            var split = Array.IndexOf(args, "--", 3);
            if (split < 0 || args[(split + 1)..] is not ["mcp", .. var mcp] || !ValidMcpArgs(mcp))
                return null;
            return new(command, version, args[3..split], mcp);
        }

        public static string? PinnedVersion(string command, string[] args)
        {
            var name = Path.GetFileName(command);
            if (!(name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) ||
                args is not ["tool", "exec", var tool, ..] || !tool.StartsWith(Package, StringComparison.OrdinalIgnoreCase))
                return null;
            var version = tool[Package.Length..];
            return version.Length > 0 && char.IsAsciiDigit(version[0]) &&
                version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+') ? version : null;
        }

        private static bool ValidMcpArgs(string[] args) => args is [] or ["--urls", _];
    }
}
