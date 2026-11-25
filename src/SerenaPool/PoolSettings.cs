using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SerenaPool;

internal sealed partial class PoolSettings
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromMinutes(60);
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public string Executable { get; init; } = OperatingSystem.IsWindows() ? "serena.exe" : "serena";

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
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("workdir 必须是已存在目录的绝对路径。");
        if (!OperatingSystem.IsWindows())
        {
            var ptr = RealPath(path, 0);
            if (ptr == 0) throw new IOException($"无法解析工作目录 {path}", new Win32Exception(Marshal.GetLastPInvokeError()));
            try
            {
                var physical = Marshal.PtrToStringUTF8(ptr)!;
                return Directory.Exists(physical) ? physical : throw new DirectoryNotFoundException(physical);
            }
            finally { Free(ptr); }
        }
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = new DirectoryInfo(Path.Combine(current, segment));
            current = dir.ResolveLinkTarget(true)?.FullName ?? dir.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RealPath(string path, nint buffer);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(nint pointer);
}
