using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace SerenaPool;

internal static partial class ProcessLauncher
{
    public static ProcessStartInfo Self(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    [LibraryImport("libc")] public static partial uint geteuid();
    [LibraryImport("libc")] private static partial int getppid();
    [LibraryImport("libc", SetLastError = true)] private static partial int setpgid(int pid, int group);
    [LibraryImport("libc", SetLastError = true)] private static partial int setsid();
    [LibraryImport("libc", SetLastError = true)] private static partial int kill(int pid, int signal);

    public static void Detach()
    {
        if (!OperatingSystem.IsWindows() && setsid() < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public static void KillGroup(int leader)
    {
        if (kill(-leader, 9) != 0 && Marshal.GetLastPInvokeError() != 3) // ESRCH: the group has already exited.
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public static async Task<int> LaunchUnixAsync(int parent, string executable, string directory)
    {
        if (setpgid(0, 0) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (getppid() != parent) return 1;
            using var child = SerenaProcess.Spawn(executable, directory);
            Console.WriteLine(child.Id);
            Console.Out.Flush();
            var exited = child.WaitForExitAsync();
            while (!exited.IsCompleted && getppid() == parent)
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMilliseconds(250)));
            return 0;
        }
        finally { KillGroup(Environment.ProcessId); }
    }
}
