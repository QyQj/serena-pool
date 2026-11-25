using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SerenaPool;

internal static partial class WindowsJob
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJob(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetInformationJobObject(SafeFileHandle job, int type, in ExtendedLimits limits, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    public static SafeFileHandle Contain(Process launcher)
    {
        var job = CreateJob(0, 0);
        try
        {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (SetInformationJobObject(job, 9, in limits, (uint)Marshal.SizeOf<ExtendedLimits>()) == 0 ||
                AssignProcessToJobObject(job, launcher.SafeHandle) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }
}
