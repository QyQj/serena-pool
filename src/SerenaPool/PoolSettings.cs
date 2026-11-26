using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SerenaPool;

internal sealed partial class PoolSettings
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromMinutes(60);
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public const string DefaultAddress = "http://127.0.0.1:32980";
    public string? Executable { get; init; }
    public bool WebDashboard { get; init; } = true;
    public bool WebDashboardOpenOnLaunch { get; init; }

    public string ResolveExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "serena.exe" : "serena";
        if (Executable is not { Length: > 0 })
            return FindOnPath(fileName) ?? FindInstalledExecutable(fileName) ?? throw new FileNotFoundException(
                $"找不到 {fileName}。请安装 Serena，或设置 Pool__Executable 为可执行文件或其目录的绝对路径。");

        if (!Path.IsPathFullyQualified(Executable))
            throw new ArgumentException("Pool__Executable 必须为可执行文件或其目录的绝对路径。");
        var path = Directory.Exists(Executable) ? Path.Combine(Executable, fileName) : Executable;
        return File.Exists(path) ? Path.GetFullPath(path) :
            throw new FileNotFoundException($"Pool__Executable 指定的 Serena 启动程序不存在：{path}。");
    }

    private static string? FindInstalledExecutable(string fileName)
    {
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Environment.GetEnvironmentVariable("UV_TOOL_BIN_DIR"), Environment.GetEnvironmentVariable("XDG_BIN_HOME"),
                Path.Combine(home, ".local", "bin") }
            .OfType<string>().Where(Path.IsPathFullyQualified)
            .Select(directory => Path.GetFullPath(Path.Combine(directory, fileName))).FirstOrDefault(File.Exists);
    }

    public static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => dir.Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(dir => Path.GetFullPath(Path.Combine(dir, fileName))).FirstOrDefault(File.Exists);

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
