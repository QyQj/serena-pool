using System.Text;

namespace SerenaPool;

internal sealed class SetupFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static ReadOnlySpan<byte> Bom => [0xef, 0xbb, 0xbf];
    private readonly Dictionary<string, Snapshot> files = new(PoolSettings.PathComparer);

    public Snapshot Read(string path)
    {
        if (!files.TryGetValue(path, out var snapshot))
        {
            snapshot = new(path, ReadBytes(path));
            files.Add(path, snapshot);
        }
        return snapshot;
    }

    public static string PhysicalPath(string path)
    {
        path = Path.GetFullPath(path);
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
            return PhysicalPath(file.ResolveLinkTarget(true)?.FullName ??
                throw new IOException($"无法解析配置链接：{path}。"));

        var directory = Path.GetDirectoryName(path)!;
        var missing = new Stack<string>();
        while (!Directory.Exists(directory))
        {
            missing.Push(Path.GetFileName(directory));
            directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException(path);
        }
        return Path.Combine([PoolSettings.ResolveWorkdir(directory), .. missing, Path.GetFileName(path)]);
    }

    public void Commit(Snapshot state)
    {
        foreach (var snapshot in files.Values)
            snapshot.CheckUnchanged(snapshot.Original);

        var applied = new Stack<Snapshot>();
        var created = new Stack<string>();
        try
        {
            foreach (var snapshot in files.Values.Where(file => file.Changed && file != state)
                .Append(state).Where(file => file.Changed))
            {
                snapshot.CheckUnchanged(snapshot.Original);
                var directory = Path.GetDirectoryName(snapshot.Path)!;
                CreateDirectories(directory, created);
                Write(snapshot.Path, snapshot.Pending!, snapshot.Original is not null);
                applied.Push(snapshot);
            }
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            while (applied.TryPop(out var snapshot))
            {
                try
                {
                    snapshot.CheckUnchanged(snapshot.Pending);
                    if (snapshot.Original is null)
                        File.Delete(snapshot.Path);
                    else
                        Write(snapshot.Path, snapshot.Original, replace: true);
                }
                catch (Exception rollback)
                {
                    failures.Add($"{snapshot.Path}：{rollback.Message}");
                }
            }
            while (created.TryPop(out var directory))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (Exception rollback)
                {
                    failures.Add($"{directory}：{rollback.Message}");
                }
            }
            var detail = failures.Count == 0 ? "已撤销本次改动。" : $"以下路径需要手动检查：{string.Join("，", failures)}";
            throw new IOException($"安装写入失败：{error.Message} {detail}", error);
        }
    }

    private static byte[]? ReadBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void CreateDirectories(string path, Stack<string> created)
    {
        if (Directory.Exists(path))
            return;
        CreateDirectories(Path.GetDirectoryName(path)!, created);
        Directory.CreateDirectory(path);
        created.Push(path);
    }

    private static void Write(string path, byte[] bytes, bool replace)
    {
        var staging = Path.Combine(Path.GetDirectoryName(path)!, $".serena-pool-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = replace ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(staging, options))
                stream.Write(bytes);

            if (replace)
                File.Replace(staging, path, null);
            else
                File.Move(staging, path);
        }
        finally { File.Delete(staging); }
    }

    private static bool Equal(byte[]? left, byte[]? right) =>
        left is null || right is null ? left == right : left.AsSpan().SequenceEqual(right);

    internal sealed class Snapshot(string path, byte[]? original)
    {
        public string Path { get; } = path;
        public byte[]? Original { get; } = original;
        public byte[]? Pending { get; private set; } = original;
        public bool Changed => !Equal(Original, Pending);

        public string? Text => Pending is null ? null : Utf8.GetString(Pending.AsSpan(Pending.AsSpan().StartsWith(Bom) ? 3 : 0));

        public void Set(string content)
        {
            var bytes = Utf8.GetBytes(content);
            Pending = Original.AsSpan().StartsWith(Bom) ? [.. Bom, .. bytes] : bytes;
        }

        public void CheckUnchanged(byte[]? expected)
        {
            if (!Equal(expected, ReadBytes(Path)))
                throw new IOException($"配置在安装过程中被修改：{Path}。请重试。");
        }
    }
}
