namespace SerenaPool;

internal sealed class PoolSettings
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromMinutes(60);
    public string Executable { get; init; } = "serena.exe";

    public string ResolveExecutable()
    {
        var candidates = Path.IsPathFullyQualified(Executable)
            ? [Executable]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(Path.IsPathFullyQualified).Select(dir => Path.Combine(dir.Trim('"'), Executable));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"找不到 Serena 启动程序 {Executable}，请设置 Pool:Executable 为绝对路径。");
    }

    public static string ResolveWorkdir(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new ArgumentException("workdir 必须是已存在目录的绝对路径。");
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = new DirectoryInfo(Path.Combine(current, segment));
            current = dir.ResolveLinkTarget(true)?.FullName ?? dir.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
