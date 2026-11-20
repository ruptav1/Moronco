using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiniumBrowser
{
    /// <summary>
    /// Owns a Windows job for child-process accounting and cleanup. The optional
    /// PRIVATE COMMIT limit is not a physical-RAM limit.
    /// Call before creating any browser environment or child process. Assignment
    /// is permanent for this host and is deliberately fail-closed.
    /// </summary>
    public sealed class MemoryBudget
    {
        public const long LimitBytes = 150000000L; // Decimal MB, not MiB.
        // Windows stores job memory limits in whole pages. Round DOWN so the
        // effective limit never exceeds the user's decimal 150 MB budget.
        public static readonly long EffectiveLimitBytes =
            (LimitBytes / Environment.SystemPageSize) * Environment.SystemPageSize;
        private const uint JobMemory = 0x00000200;
        private const uint KillOnJobClose = 0x00002000;
        private const uint BreakawayOk = 0x00000800;
        private const uint SilentBreakawayOk = 0x00001000;
        private const int ExtendedLimitInformation = 9;
        private const int BasicProcessIdList = 3;
        private const int MemoryUsageInformation = 28;
        private const int ErrorMoreData = 234;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int MaximumProcessCapacity = 65536;
        private static readonly object Gate = new object();
        private static volatile MemoryBudget current;

        // Intentionally process-lifetime ownership. NO finalizer/Dispose:
        // closing the last KILL_ON_JOB_CLOSE handle kills this host too.
        // The unnamed, non-inheritable handle is closed by Windows at exit,
        // terminating any children still in the job. It is never exposed.
        private readonly IntPtr jobHandle;
        private readonly bool enforceLimit;
        private bool applied;
        private string applyError;

        private MemoryBudget(IntPtr handle, bool limitEnforced)
        {
            jobHandle = handle;
            enforceLimit = limitEnforced;
        }

        public static MemoryBudget Current { get { return current; } }
        public bool IsApplied { get { return applied; } }
        public bool IsLimitEnforced { get { return enforceLimit; } }

        public static bool TryApply(out string error)
        {
            return TryApply(true, out error);
        }

        public static bool TryApply(out MemoryBudget budget, out string error)
        {
            return TryApply(true, out budget, out error);
        }

        public static bool TryApply(bool enforceLimit, out string error)
        {
            MemoryBudget ignored;
            return TryApply(enforceLimit, out ignored, out error);
        }

        public static bool TryApply(bool enforceLimit, out MemoryBudget budget, out string error)
        {
            lock (Gate)
            {
                budget = current;
                error = null;
                if (budget != null)
                {
                    if (budget.enforceLimit != enforceLimit)
                    {
                        error = "The process job already uses a different memory-limit mode. Restart the process to change modes.";
                        return false;
                    }
                    error = budget.applyError;
                    return budget.applied;
                }

                if (enforceLimit)
                {
                    ProcessMemoryCountersEx before;
                    if (!TryReadProcessMemory(Native.GetCurrentProcess(), out before))
                    {
                        error = Win32Error("Cannot measure host memory before applying the budget");
                        return false;
                    }
                    // Assignment does not retroactively validate earlier allocations.
                    if (before.PrivateUsage.ToUInt64() > (ulong)EffectiveLimitBytes)
                    {
                        error = "The host already exceeds the 150 MB private-commit budget. Browser startup refused.";
                        return false;
                    }
                }

                IntPtr handle = Native.CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    error = Win32Error("Cannot create the memory-budget job");
                    return false;
                }

                bool assigned = false;
                try
                {
                    JobExtendedLimitInformation limits = new JobExtendedLimitInformation();
                    limits.BasicLimitInformation.LimitFlags = KillOnJobClose | (enforceLimit ? JobMemory : 0);
                    if (enforceLimit) limits.JobMemoryLimit = new UIntPtr((ulong)EffectiveLimitBytes);
                    uint size = (uint)Marshal.SizeOf(typeof(JobExtendedLimitInformation));
                    if (!Native.SetInformationJobObject(handle, ExtendedLimitInformation, ref limits, size))
                    {
                        error = Win32Error("Cannot configure the process job");
                        return false;
                    }

                    // Construct the lifetime owner before assigning the current
                    // process so an allocation failure cannot orphan ownership.
                    budget = new MemoryBudget(handle, enforceLimit);
                    if (!Native.AssignProcessToJobObject(handle, Native.GetCurrentProcess()))
                    {
                        error = Win32Error("Cannot place the host in its memory-budget job (an existing parent job may forbid nesting). Browser startup refused");
                        budget = null;
                        return false;
                    }
                    assigned = true;
                    current = budget;

                    bool inJob;
                    if (!Native.IsProcessInJob(Native.GetCurrentProcess(), handle, out inJob) || !inJob)
                    {
                        error = "Cannot verify host membership in the memory-budget job. Browser startup refused.";
                        budget.applyError = error;
                        return false;
                    }

                    uint returned;
                    JobExtendedLimitInformation verified;
                    if (!Native.QueryExtendedLimits(handle, ExtendedLimitInformation, out verified, size, out returned))
                    {
                        error = Win32Error("Cannot verify the applied memory-budget limits. Browser startup refused");
                        budget.applyError = error;
                        return false;
                    }
                    uint flags = verified.BasicLimitInformation.LimitFlags;
                    if ((flags & KillOnJobClose) == 0
                        || (flags & (BreakawayOk | SilentBreakawayOk)) != 0
                        || (enforceLimit && ((flags & JobMemory) == 0
                            || verified.JobMemoryLimit.ToUInt64() != (ulong)EffectiveLimitBytes))
                        || (!enforceLimit && (flags & JobMemory) != 0))
                    {
                        error = "Windows did not retain the required job limits (flags=0x"
                            + flags.ToString("X") + ", bytes=" + verified.JobMemoryLimit.ToUInt64()
                            + "). Browser startup refused.";
                        budget.applyError = error;
                        return false;
                    }

                    if (enforceLimit)
                    {
                        ProcessMemoryCountersEx after;
                        if (!TryReadProcessMemory(Native.GetCurrentProcess(), out after)
                            || after.PrivateUsage.ToUInt64() > (ulong)EffectiveLimitBytes)
                        {
                            error = "The host cannot satisfy the 150 MB private-commit budget after assignment. Browser startup refused.";
                            budget.applyError = error;
                            return false;
                        }
                    }
                    budget.applied = true;
                    return true;
                }
                finally
                {
                    // Safe only while the job is empty. After assignment the
                    // handle must remain alive even if verification fails.
                    if (!assigned) Native.CloseHandle(handle);
                }
            }
        }

        /// <summary>
        /// Verifies an engine PID belongs to this job, including nested jobs.
        /// A caller must refuse an engine running outside the budget.
        /// </summary>
        public bool TryContainsProcess(int processId, out bool contains, out string error)
        {
            contains = false;
            error = null;
            if (processId <= 0)
            {
                error = "Invalid process ID.";
                return false;
            }
            IntPtr process = Native.OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (process == IntPtr.Zero)
            {
                error = Win32Error("Cannot open process " + processId);
                return false;
            }
            try
            {
                if (!Native.IsProcessInJob(process, jobHandle, out contains))
                {
                    error = Win32Error("Cannot query job membership for process " + processId);
                    return false;
                }
                return true;
            }
            finally { Native.CloseHandle(process); }
        }

        /// <summary>
        /// Non-atomic snapshot. Working sets include shared pages and can double
        /// count those pages. Private commit is NOT resident physical memory.
        /// Private working set excludes shared resident pages; it is not the
        /// entire physical-memory cost of the browser or its GPU allocations.
        /// Process exits/access failures produce IsComplete=false, never zeros
        /// silently presented as a complete measurement.
        /// </summary>
        public bool TrySample(out MemoryBudgetSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;
            if (!applied)
            {
                error = applyError ?? "The memory budget has not been applied.";
                return false;
            }

            int[] ids;
            if (!TryGetProcessIds(out ids, out error)) return false;

            uint returned;
            JobExtendedLimitInformation limits;
            if (!Native.QueryExtendedLimits(jobHandle, ExtendedLimitInformation, out limits,
                (uint)Marshal.SizeOf(typeof(JobExtendedLimitInformation)), out returned))
            {
                error = Win32Error("Cannot sample the job peak private commit");
                return false;
            }

            MemoryBudgetSnapshot result = new MemoryBudgetSnapshot();
            result.TimestampUtc = DateTime.UtcNow;
            result.ProcessIds = ids;
            result.ProcessCount = ids.Length;
            result.PeakJobPrivateCommitBytes = (long)limits.PeakJobMemoryUsed.ToUInt64();

            // This information class is used in Microsoft's hcsshim. It is not
            // available on every Windows version. Unlike violation-information
            // JobMemory, this is current usage, not a past notification sample.
            JobMemoryUsageInformation nativeUsage;
            result.HasNativeJobPrivateCommit = Native.QueryMemoryUsage(jobHandle, MemoryUsageInformation,
                out nativeUsage, (uint)Marshal.SizeOf(typeof(JobMemoryUsageInformation)), out returned);
            if (result.HasNativeJobPrivateCommit)
                result.JobPrivateCommitBytes = (long)nativeUsage.JobMemory;

            for (int i = 0; i < ids.Length; i++)
            {
                IntPtr process = Native.OpenProcess(ProcessQueryLimitedInformation, false, (uint)ids[i]);
                if (process == IntPtr.Zero) continue;
                try
                {
                    bool belongs;
                    ProcessMemoryCountersEx memory;
                    // Handles avoid PID reuse after OpenProcess; membership
                    // verification avoids counting an unrelated reused PID.
                    if (!Native.IsProcessInJob(process, jobHandle, out belongs) || !belongs
                        || !TryReadProcessMemory(process, out memory)) continue;
                    result.WorkingSetBytesInclusiveShared += (long)memory.WorkingSetSize.ToUInt64();
                    result.SummedProcessPrivateCommitBytes += (long)memory.PrivateUsage.ToUInt64();
                    result.SampledProcessCount++;
                    long privateWorkingSet;
                    if (TryReadPrivateWorkingSet(process, out privateWorkingSet))
                    {
                        result.PrivateWorkingSetBytes += privateWorkingSet;
                        result.PrivateWorkingSetSampledProcessCount++;
                    }
                }
                finally { Native.CloseHandle(process); }
            }

            result.IsComplete = result.SampledProcessCount == result.ProcessCount;
            result.HasPrivateWorkingSet = result.IsComplete
                && result.PrivateWorkingSetSampledProcessCount == result.ProcessCount;
            if (!result.HasNativeJobPrivateCommit)
                result.JobPrivateCommitBytes = result.SummedProcessPrivateCommitBytes;
            snapshot = result;
            return true;
        }

        public bool TryGetProcessIds(out int[] processIds, out string error)
        {
            processIds = null;
            error = null;
            int capacity = 16;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int bytes = checked(8 + capacity * IntPtr.Size);
                IntPtr buffer = Marshal.AllocHGlobal(bytes);
                try
                {
                    // Initialize the two counts in case a failed query does not
                    // write them; never consume uninitialized native memory.
                    Marshal.WriteInt32(buffer, 0, 0);
                    Marshal.WriteInt32(buffer, 4, 0);
                    uint returned;
                    bool success = Native.QueryProcessIds(jobHandle, BasicProcessIdList, buffer,
                        (uint)bytes, out returned);
                    int win32Error = success ? 0 : Marshal.GetLastWin32Error();
                    uint assignedCount = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                    uint listedCount = unchecked((uint)Marshal.ReadInt32(buffer, 4));
                    if (!success && win32Error != ErrorMoreData)
                    {
                        error = Win32Error("Cannot enumerate memory-budget job processes", win32Error);
                        return false;
                    }
                    if (success && listedCount <= (uint)capacity && assignedCount <= listedCount)
                    {
                        List<int> result = new List<int>((int)listedCount);
                        HashSet<int> seen = new HashSet<int>();
                        for (int i = 0; i < (int)listedCount; i++)
                        {
                            long value = Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64();
                            if (value <= 0 || value > Int32.MaxValue)
                            {
                                error = "Windows returned an invalid process ID for the memory-budget job.";
                                return false;
                            }
                            int pid = (int)value;
                            if (seen.Add(pid)) result.Add(pid);
                        }
                        processIds = result.ToArray();
                        return true;
                    }

                    long needed = Math.Max((long)capacity * 2, (long)assignedCount + 8);
                    if (needed > MaximumProcessCapacity)
                    {
                        error = "The memory-budget process list exceeds the supported safety bound.";
                        return false;
                    }
                    capacity = (int)needed;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            error = "The job process list kept changing; a complete snapshot could not be obtained.";
            return false;
        }

        private static bool TryReadProcessMemory(IntPtr process, out ProcessMemoryCountersEx memory)
        {
            memory = new ProcessMemoryCountersEx();
            memory.cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx));
            return Native.GetProcessMemoryInfo(process, ref memory, memory.cb);
        }

        private static bool TryReadPrivateWorkingSet(IntPtr process, out long bytes)
        {
            bytes = 0;
            ProcessMemoryCountersEx2 memory = new ProcessMemoryCountersEx2();
            uint size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx2));
            memory.cb = size;
            // EX2 is documented for Windows 10/11 22H2 with the September
            // 2023 cumulative update. Older implementations may reject the
            // larger buffer or fill only its base fields. Sentinels prevent
            // either case from silently becoming a zero-byte measurement.
            ulong unavailable = UIntPtr.Size == 8 ? UInt64.MaxValue : UInt32.MaxValue;
            memory.PrivateWorkingSetSize = new UIntPtr(unavailable);
            memory.SharedCommitUsage = UInt64.MaxValue;
            if (!Native.GetProcessMemoryInfoEx2(process, ref memory, size)
                || memory.cb != size
                || memory.PrivateWorkingSetSize.ToUInt64() == unavailable
                || memory.SharedCommitUsage == UInt64.MaxValue)
                return false;
            bytes = checked((long)memory.PrivateWorkingSetSize.ToUInt64());
            return true;
        }

        private static string Win32Error(string action)
        {
            return Win32Error(action, Marshal.GetLastWin32Error());
        }

        private static string Win32Error(string action, int code)
        {
            return action + ": " + new Win32Exception(code).Message + " (Win32 " + code + ").";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimitInformation
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
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobMemoryUsageInformation
        {
            public ulong JobMemory;
            public ulong PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        // https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx2
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
            public UIntPtr PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        private static class Native
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(IntPtr job, int infoClass,
                ref JobExtendedLimitInformation info, uint size);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsProcessInJob(IntPtr process, IntPtr job,
                [MarshalAs(UnmanagedType.Bool)] out bool result);
            [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool QueryExtendedLimits(IntPtr job, int infoClass,
                out JobExtendedLimitInformation info, uint size, out uint returned);
            [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool QueryMemoryUsage(IntPtr job, int infoClass,
                out JobMemoryUsageInformation info, uint size, out uint returned);
            [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool QueryProcessIds(IntPtr job, int infoClass,
                IntPtr info, uint size, out uint returned);
            [DllImport("kernel32.dll")]
            public static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint access,
                [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
            [DllImport("psapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetProcessMemoryInfo(IntPtr process,
                ref ProcessMemoryCountersEx counters, uint size);
            [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetProcessMemoryInfoEx2(IntPtr process,
                ref ProcessMemoryCountersEx2 counters, uint size);
        }
    }

    public sealed class MemoryBudgetSnapshot
    {
        public DateTime TimestampUtc { get; internal set; }
        public int[] ProcessIds { get; internal set; }
        public int ProcessCount { get; internal set; }
        public int SampledProcessCount { get; internal set; }
        public bool IsComplete { get; internal set; }
        public bool HasNativeJobPrivateCommit { get; internal set; }
        public long JobPrivateCommitBytes { get; internal set; }
        // Windows' peak job accounting can include a temporarily charged,
        // ultimately rejected allocation. It is not a peak of successful
        // allocations or sampled resident physical RAM.
        public long PeakJobPrivateCommitBytes { get; internal set; }
        public long SummedProcessPrivateCommitBytes { get; internal set; }
        public long WorkingSetBytesInclusiveShared { get; internal set; }
        // Aggregate of successful EX2 readings. Only display as a complete
        // measurement when HasPrivateWorkingSet is true; otherwise the value
        // is partial or unavailable. This excludes shared resident pages.
        public long PrivateWorkingSetBytes { get; internal set; }
        public bool HasPrivateWorkingSet { get; internal set; }
        public int PrivateWorkingSetSampledProcessCount { get; internal set; }
    }
}
