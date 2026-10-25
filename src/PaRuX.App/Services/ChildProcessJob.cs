using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaRuX.Services
{
    /// <summary>
    /// Ties PaRuX Core's process lifetime to this (PaRuX.exe) process via a Windows Job
    /// Object, so quitting from the tray - or Windows/Task Manager force-closing the app, or a
    /// crash - can no longer leave PaRuX.Core.exe (and, transitively, llama-server/ComfyUI,
    /// which PaRuX Core already ties to itself the same way) running in the background.
    /// User-reported: clicking "Exit" in the tray did not fully close the app. Same root cause,
    /// one process level up, as a real bug fixed Core-side earlier: nothing ever explicitly
    /// killed the child process, and Windows does not do this automatically.
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE makes Windows itself kill every process ever assigned
    /// to the job the moment the job handle closes, which happens on ANY exit of this process,
    /// not just a clean one a try/finally could catch.
    /// </summary>
    internal static class ChildProcessJob
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        private static readonly IntPtr Handle = Create();

        private static IntPtr Create()
        {
            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE }
                };
                uint size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size)) return IntPtr.Zero;
                return job;
            }
            catch
            {
                return IntPtr.Zero; // best-effort: if the Job Object API is unavailable, the
                // child process just behaves as it did before this feature existed
            }
        }

        /// <summary>Best-effort: a failure here means PaRuX Core just isn't tied to this
        /// process's lifetime, not that starting it should be treated as having failed.</summary>
        public static void Assign(Process process)
        {
            if (Handle == IntPtr.Zero) return;
            try { AssignProcessToJobObject(Handle, process.Handle); }
            catch { }
        }
    }
}
