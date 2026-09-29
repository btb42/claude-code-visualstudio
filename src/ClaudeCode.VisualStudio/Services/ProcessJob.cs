using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Ties every claude CLI child to the lifetime of the extension host (devenv.exe) via a Windows
    /// Job Object with KILL_ON_JOB_CLOSE.
    ///
    /// Windows does not kill child processes when their parent exits. The CLI runs in stream-json
    /// mode and stays alive as long as its stdin is open, so when VS closes (or the extension is
    /// torn down, or devenv crashes) the process is left orphaned. That orphan keeps the file lock
    /// on its <c>--resume</c> session id, so the NEXT launch's attempt to resume the same
    /// conversation blocks forever waiting for the lock — which is exactly the "open a fresh
    /// install, type the first message, and nothing happens" symptom.
    ///
    /// A single process-wide job owned by devenv fixes this at the OS level: the job handle is held
    /// for the whole devenv lifetime, and the instant it closes — normal exit or crash — every
    /// process still in the job is terminated by the kernel. The handle is intentionally never
    /// closed by us; letting the process die is the whole point.
    /// </summary>
    internal static class ProcessJob
    {
        private static readonly object _gate = new object();
        private static IntPtr _job = IntPtr.Zero;
        private static bool _tried;

        /// <summary>Adds <paramref name="process"/> to the kill-on-close job. Best-effort: any
        /// failure (unsupported platform, denied) is logged and swallowed — the CLI still runs.</summary>
        public static void Assign(Process process)
        {
            if (process == null) return;
            try
            {
                var job = EnsureJob();
                if (job == IntPtr.Zero) return;
                if (!AssignProcessToJobObject(job, process.Handle))
                    Log.Write("ProcessJob: AssignProcessToJobObject failed err=" + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Log.Write("ProcessJob: assign threw " + ex.Message); }
        }

        private static IntPtr EnsureJob()
        {
            lock (_gate)
            {
                if (_tried) return _job;
                _tried = true;

                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    Log.Write("ProcessJob: CreateJobObject failed err=" + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int len = Marshal.SizeOf(info);
                var ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                    {
                        Log.Write("ProcessJob: SetInformationJobObject failed err=" + Marshal.GetLastWin32Error());
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally { Marshal.FreeHGlobal(ptr); }

                _job = job;
                Log.Write("ProcessJob: kill-on-close job created");
                return _job;
            }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

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
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
