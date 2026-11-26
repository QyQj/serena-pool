using System.Text;

namespace SerenaPool;

internal sealed class ProcessFileLog : ILoggerProvider
{
    private readonly object sync = new();
    private FileStream? file;

    public ProcessFileLog(string role)
    {
        try
        {
            var directory = LogDirectory();
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, BufferSize = 0
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var name = FormattableString.Invariant($"{role}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Environment.ProcessId}.log");
            file = new FileStream(Path.Combine(directory, name), options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportFailure(error);
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        lock (sync)
        {
            var stream = file;
            file = null;
            try { stream?.Dispose(); }
            catch (IOException error) { ReportFailure(error); }
        }
    }

    private static string LogDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var directory = OperatingSystem.IsWindows()
            ? Path.Combine(Path.IsPathFullyQualified(appData ?? "") ? appData! :
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerenaPool", "logs")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Logs", "SerenaPool")
                : Path.Combine(Path.IsPathFullyQualified(state ?? "") ? state! : Path.Combine(home, ".local", "state"), "SerenaPool", "logs");
        return Path.IsPathFullyQualified(directory) ? directory : throw new IOException("Cannot locate the current user's log directory.");
    }

    private void Write(string category, LogLevel level, string message, Exception? error)
    {
        lock (sync)
        {
            if (file is null) return;
            var entry = $"{DateTimeOffset.UtcNow:O} [{level}] {category} {message}{Environment.NewLine}";
            if (error is not null) entry += error + Environment.NewLine;
            try { file.Write(Encoding.UTF8.GetBytes(entry)); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                ReportFailure(failure);
                Dispose();
            }
        }
    }

    private static void ReportFailure(Exception error) =>
        Console.Error.WriteLine($"Serena Pool file logging failed: {error.Message}");

    private sealed class Logger(ProcessFileLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level != LogLevel.None &&
            !category.StartsWith("ModelContextProtocol", StringComparison.Ordinal);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) owner.Write(category, level, formatter(state, error), error);
        }
    }
}
