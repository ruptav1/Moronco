using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MiniumBrowser;

// Run as a standalone child executable, NEVER load in a browser or test runner
// process: TryApply permanently assigns the current process to the job.
internal static class MemoryBudgetProbe
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 4;

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--inherited-worker") return InheritedWorker(true);
        if (args.Length == 1 && args[0] == "--inherited-worker-uncapped") return InheritedWorker(false);
        bool enforceLimit = args.Length == 0;
        if (!enforceLimit && (args.Length != 1 || args[0] != "--uncapped"))
        {
            Console.Error.WriteLine("Usage: MemoryBudgetProbe.exe [--uncapped]");
            return 12;
        }
        string error;
        MemoryBudget budget;
        // Uncapped startup must also work when existing private commit already
        // exceeds 150 MB. These pages are never touched and are freed below.
        IntPtr startupAllocation = IntPtr.Zero;
        if (!enforceLimit)
        {
            startupAllocation = VirtualAlloc(IntPtr.Zero, new UIntPtr(151000000UL),
                MemReserve | MemCommit, PageReadWrite);
            if (startupAllocation == IntPtr.Zero)
            {
                Console.Error.WriteLine("FAIL uncapped setup allocation: " + Marshal.GetLastWin32Error());
                return 13;
            }
        }
        bool applied;
        try
        {
            // Exercise the original overload as well as the new explicit mode.
            applied = enforceLimit ? MemoryBudget.TryApply(out budget, out error)
                : MemoryBudget.TryApply(false, out budget, out error);
        }
        finally
        {
            if (startupAllocation != IntPtr.Zero) VirtualFree(startupAllocation, UIntPtr.Zero, MemRelease);
        }
        if (!applied)
        {
            Console.Error.WriteLine("FAIL apply: " + error);
            return 1;
        }
        if (budget.IsLimitEnforced != enforceLimit
            || !MemoryBudget.TryApply(enforceLimit, out error)
            || MemoryBudget.TryApply(!enforceLimit, out error) || String.IsNullOrEmpty(error))
        {
            Console.Error.WriteLine("FAIL job mode or repeated-application behavior.");
            return 14;
        }
        Console.WriteLine("PASS mode=" + (enforceLimit ? "capped" : "uncapped")
            + "; repeated same mode accepted; mode switch rejected.");
        MemoryBudgetSnapshot before;
        if (!budget.TrySample(out before, out error) || !before.IsComplete)
        {
            Console.Error.WriteLine("FAIL initial sample: " + error);
            return 2;
        }
        Print("before", before);
        if (!CheckPrivateWorkingSet(before)) return 15;

        // Commit 151 decimal MB without touching resident pages. Capped mode
        // must refuse; uncapped mode must permit it. Free success immediately.
        IntPtr excessive = VirtualAlloc(IntPtr.Zero, new UIntPtr(151000000UL),
            MemReserve | MemCommit, PageReadWrite);
        int allocationError = Marshal.GetLastWin32Error();
        bool largeAccepted = excessive != IntPtr.Zero;
        if (largeAccepted) VirtualFree(excessive, UIntPtr.Zero, MemRelease);
        if (largeAccepted == enforceLimit)
        {
            Console.Error.WriteLine("FAIL 151 MB allocation was " + (largeAccepted ? "accepted" : "refused")
                + "; Win32=" + allocationError);
            return 3;
        }
        Console.WriteLine("PASS 151 MB allocation " + (largeAccepted ? "accepted and immediately freed" : "refused, Win32=" + allocationError));

        const int SmallAllocation = 4 * 1024 * 1024;
        IntPtr small = VirtualAlloc(IntPtr.Zero, new UIntPtr(SmallAllocation),
            MemReserve | MemCommit, PageReadWrite);
        if (small == IntPtr.Zero)
        {
            Console.Error.WriteLine("FAIL small allocation: " + Marshal.GetLastWin32Error());
            return 4;
        }
        try
        {
            MemoryBudgetSnapshot during;
            if (!budget.TrySample(out during, out error) || !during.IsComplete)
            {
                Console.Error.WriteLine("FAIL sample after allocation: " + error);
                return 5;
            }
            Print("during", during);
            if (!CheckPrivateWorkingSet(during)) return 15;
            if (during.SummedProcessPrivateCommitBytes < before.SummedProcessPrivateCommitBytes + SmallAllocation)
            {
                Console.Error.WriteLine("FAIL private commit did not include the allocation.");
                return 6;
            }
            if (enforceLimit && during.HasNativeJobPrivateCommit && during.JobPrivateCommitBytes > MemoryBudget.LimitBytes)
            {
                Console.Error.WriteLine("FAIL native job private commit exceeds budget.");
                return 7;
            }

            // Committing alone need not make pages resident. Touch exactly one
            // byte per page of a bounded 4 MiB allocation, then sample again.
            // Log the delta instead of asserting an exact growth: Windows can
            // trim other resident pages while this non-atomic test runs.
            for (int offset = 0; offset < SmallAllocation; offset += Environment.SystemPageSize)
                Marshal.WriteByte(small, offset, 1);
            MemoryBudgetSnapshot resident;
            if (!budget.TrySample(out resident, out error) || !resident.IsComplete)
            {
                Console.Error.WriteLine("FAIL sample after touching resident pages: " + error);
                return 16;
            }
            Print("resident_4mib_touched", resident);
            if (!CheckPrivateWorkingSet(resident)) return 15;
            if (during.HasPrivateWorkingSet && resident.HasPrivateWorkingSet)
                Console.WriteLine("INFO private_working_set_touch_delta={0}; touched_bytes={1}",
                    resident.PrivateWorkingSetBytes - during.PrivateWorkingSetBytes, SmallAllocation);
            else
                Console.WriteLine("INFO private working set unavailable on this OS or snapshot; no zero-byte claim.");
        }
        finally { VirtualFree(small, UIntPtr.Zero, MemRelease); }

        MemoryBudgetSnapshot after;
        if (!budget.TrySample(out after, out error))
        {
            Console.Error.WriteLine("FAIL final sample: " + error);
            return 8;
        }
        Print("after", after);
        if (!CheckPrivateWorkingSet(after)) return 15;
        if (!CheckChildInheritance(budget)) return 9;
        Console.WriteLine("PASS job application, allocation behavior, accounting, process enumeration, and child inheritance.");
        return 0;
    }

    private static bool CheckChildInheritance(MemoryBudget budget)
    {
        ProcessStartInfo info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
            budget.IsLimitEnforced ? "--inherited-worker" : "--inherited-worker-uncapped");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        using (Process child = Process.Start(info))
        {
            try
            {
                Task<string> ready = Task.Factory.StartNew(delegate { return child.StandardOutput.ReadLine(); });
                if (!ready.Wait(5000) || ready.Result == null || !ready.Result.StartsWith("READY"))
                {
                    Console.Error.WriteLine("FAIL child did not confirm inherited allocation behavior.");
                    return false;
                }
                string error;
                bool member;
                if (!budget.TryContainsProcess(child.Id, out member, out error) || !member)
                {
                    Console.Error.WriteLine("FAIL child not in parent's budget job: " + error);
                    return false;
                }
                MemoryBudgetSnapshot sample;
                if (!budget.TrySample(out sample, out error) || !sample.IsComplete
                    || sample.ProcessCount < 2 || Array.IndexOf(sample.ProcessIds, child.Id) < 0)
                {
                    Console.Error.WriteLine("FAIL child absent from complete accounting: " + error);
                    return false;
                }
                Print("with_inherited_child", sample);
                if (!CheckPrivateWorkingSet(sample)) return false;
                Console.WriteLine("PASS child inherited parent job and expected allocation behavior: " + ready.Result);
                child.StandardInput.WriteLine("exit");
                child.StandardInput.Flush();
                if (!child.WaitForExit(5000) || child.ExitCode != 0)
                {
                    Console.Error.WriteLine("FAIL child shutdown.");
                    return false;
                }
                return true;
            }
            finally
            {
                // This process is solely the probe's own bounded test worker.
                if (!child.HasExited)
                {
                    child.Kill();
                    child.WaitForExit(5000);
                }
            }
        }
    }

    private static int InheritedWorker(bool enforceLimit)
    {
        // Do not call TryApply here: this test must exercise the PARENT job.
        IntPtr excessive = VirtualAlloc(IntPtr.Zero, new UIntPtr(151000000UL),
            MemReserve | MemCommit, PageReadWrite);
        int allocationError = Marshal.GetLastWin32Error();
        bool accepted = excessive != IntPtr.Zero;
        if (accepted) VirtualFree(excessive, UIntPtr.Zero, MemRelease);
        if (accepted == enforceLimit)
        {
            Console.WriteLine("FAIL inherited 151 MB allocation behavior; Win32=" + allocationError);
            return 10;
        }
        Console.WriteLine("READY inherited_allocation=" + (accepted ? "accepted_and_freed" : "refused_win32=" + allocationError));
        Console.Out.Flush();
        return Console.ReadLine() == "exit" ? 0 : 11;
    }

    private static void Print(string phase, MemoryBudgetSnapshot sample)
    {
        Console.WriteLine("{0}: job_private_commit={1}; peak={2}; process_private_commit={3}; working_set_inclusive_shared={4}; pids={5}; complete={6}; native_job_counter={7}; private_working_set={8}; private_working_set_pids={9}/{10}",
            phase, sample.JobPrivateCommitBytes, sample.PeakJobPrivateCommitBytes,
            sample.SummedProcessPrivateCommitBytes, sample.WorkingSetBytesInclusiveShared,
            String.Join(",", sample.ProcessIds), sample.IsComplete, sample.HasNativeJobPrivateCommit,
            sample.HasPrivateWorkingSet ? sample.PrivateWorkingSetBytes.ToString() : "unavailable_or_partial",
            sample.PrivateWorkingSetSampledProcessCount, sample.ProcessCount);
    }

    private static bool CheckPrivateWorkingSet(MemoryBudgetSnapshot sample)
    {
        if (sample.PrivateWorkingSetSampledProcessCount < 0
            || sample.PrivateWorkingSetSampledProcessCount > sample.SampledProcessCount
            || sample.PrivateWorkingSetBytes < 0
            || sample.HasPrivateWorkingSet != (sample.IsComplete
                && sample.PrivateWorkingSetSampledProcessCount == sample.ProcessCount)
            || (sample.HasPrivateWorkingSet && sample.PrivateWorkingSetBytes == 0))
        {
            Console.Error.WriteLine("FAIL private resident memory availability/accounting; a live probe cannot have zero private resident bytes.");
            return false;
        }
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
}
