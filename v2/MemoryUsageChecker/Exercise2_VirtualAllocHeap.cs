// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using System.Collections.Generic;
using System.Linq;

namespace MemoryUsageChecker
{
    internal static class Exercise2_VirtualAllocHeap
    {
        private const long NotableImpactingBytes = 10L * 1024 * 1024; // 10 MB

        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<ICommitDataSource> pendingCommit,
            IPendingResult<IHeapSnapshotDataSource> pendingHeap)
        {
            output.WriteHeader("=== Exercise 2: VirtualAlloc + Heap ===");

            RunVirtualAllocPart(output, pendingCommit);
            output.WriteBlank();
            RunHeapPart(output, pendingHeap);
        }

        private static void RunVirtualAllocPart(OutputWriter output, IPendingResult<ICommitDataSource> pendingCommit)
        {
            output.WriteSubHeader("--- VirtualAlloc Commit Lifetimes ---");

            if (!pendingCommit.HasResult || pendingCommit.Result.CommitLifetimes.Count == 0)
            {
                output.WriteSkipped("[skipped - VirtualAlloc/commit data not present in trace]");
                return;
            }

            var lifetimes = pendingCommit.Result.CommitLifetimes
                .Where(c => c.Type == CommitLifetimeType.VirtualMemory && c.Process != null)
                .ToList();

            if (lifetimes.Count == 0)
            {
                output.WriteSkipped("[no VirtualMemory commit lifetimes found]");
                return;
            }

            var allPerProcess = lifetimes
                .GroupBy(c => c.Process)
                .Select(g => new
                {
                    Process = g.Key,
                    Total = g.Sum(x => x.AddressRange.Size.Bytes),
                    Impacting = g.Where(x => x.DecommitEvent?.Timestamp == null).Sum(x => x.AddressRange.Size.Bytes),
                    Transient = g.Where(x => x.DecommitEvent?.Timestamp != null).Sum(x => x.AddressRange.Size.Bytes),
                    Lifetimes = g.ToList()
                })
                .OrderByDescending(x => x.Impacting)
                .ToList();

            var perProcess = allPerProcess.Take(output.TopN).ToList();

            output.WriteSubHeader($"Top {output.TopN} processes by Impacting commit size (MB):");
            int rank = 0;
            foreach (var row in perProcess)
            {
                rank++;
                string line = $"  {row.Process.ImageName,-32} (pid {row.Process.Id,6})  Impacting {row.Impacting / 1048576.0,8:F2}  Transient {row.Transient / 1048576.0,8:F2}  Total {row.Total / 1048576.0,8:F2}  MB";
                if (row.Impacting >= NotableImpactingBytes)
                {
                    // Override the rank-based tier: threshold breach forces red.
                    output.WriteCritical(line);
                }
                else
                {
                    output.WriteRanked(rank, perProcess.Count, line);
                }
            }
            // Tail summary
            if (allPerProcess.Count > perProcess.Count)
            {
                int tailCount = allPerProcess.Count - perProcess.Count;
                double tailMb = allPerProcess.Skip(perProcess.Count).Sum(x => x.Impacting) / 1048576.0;
                output.WriteTail($"  + {tailCount} more processes totaling {tailMb:F2} MB Impacting");
            }
            output.WriteBlank();

            foreach (var row in perProcess)
            {
                output.WriteSubHeader($"Top {output.TopK} commit stacks for {row.Process.ImageName} (pid {row.Process.Id})");

                WriteTopStacks(output, "Impacting", row.Lifetimes.Where(x => x.DecommitEvent?.Timestamp == null).Select(x => (x.CommitEvent?.Stack, x.AddressRange.Size.Bytes)));
                WriteTopStacks(output, "Transient", row.Lifetimes.Where(x => x.DecommitEvent?.Timestamp != null).Select(x => (x.CommitEvent?.Stack, x.AddressRange.Size.Bytes)));
                output.WriteBlank();
            }
        }

        // ---- Part B: Heap snapshots (filled in Task 6) ----

        private static void RunHeapPart(OutputWriter output, IPendingResult<IHeapSnapshotDataSource> pendingHeap)
        {
            output.WriteSubHeader("--- Heap Snapshots ---");

            if (!pendingHeap.HasResult || pendingHeap.Result.Snapshots.Count == 0)
            {
                output.WriteSkipped("[skipped - heap data not present in trace.");
                output.WriteSkipped(" Heap tracing requires the per-process TracingFlags=1 registry key documented in WPT Exercise 2 Step 1.1.]");
                return;
            }

            // For each process, use its LATEST snapshot as the outstanding/impacting view.
            var latestPerProcess = pendingHeap.Result.Snapshots
                .Where(s => s.Process != null)
                .GroupBy(s => s.Process)
                .Select(g => g.OrderByDescending(s => s.Timestamp.Nanoseconds).First())
                .ToList();

            var allPerProcessSummary = latestPerProcess
                .Select(s => new
                {
                    Process = s.Process,
                    HeapCount = s.Allocations.Select(a => a.HeapHandle).Distinct().Count(),
                    AllocCount = s.Allocations.Count,
                    OutstandingBytes = s.Allocations.Sum(a => a.Size.Bytes),
                    Snapshot = s
                })
                .OrderByDescending(x => x.OutstandingBytes)
                .ToList();

            var perProcessSummary = allPerProcessSummary.Take(output.TopN).ToList();

            output.WriteSubHeader($"Top {output.TopN} processes by outstanding heap size (KB):");
            int rank = 0;
            foreach (var row in perProcessSummary)
            {
                rank++;
                string line =
                    $"  {row.Process.ImageName,-32} (pid {row.Process.Id,6})  " +
                    $"heaps {row.HeapCount,3}  allocations {row.AllocCount,8}  outstanding {row.OutstandingBytes / 1024.0,10:F2} KB";
                output.WriteRanked(rank, perProcessSummary.Count, line);
            }
            // Tail summary
            if (allPerProcessSummary.Count > perProcessSummary.Count)
            {
                int tailCount = allPerProcessSummary.Count - perProcessSummary.Count;
                double tailKb = allPerProcessSummary.Skip(perProcessSummary.Count).Sum(x => x.OutstandingBytes) / 1024.0;
                output.WriteTail($"  + {tailCount} more processes totaling {tailKb:F2} KB outstanding");
            }
            output.WriteBlank();

            foreach (var row in perProcessSummary)
            {
                // Largest heap handle for this process by outstanding bytes
                var largestHeap = row.Snapshot.Allocations
                    .GroupBy(a => a.HeapHandle)
                    .Select(g => new { Handle = g.Key, Bytes = g.Sum(a => a.Size.Bytes), Allocs = g.ToList() })
                    .OrderByDescending(g => g.Bytes)
                    .FirstOrDefault();

                if (largestHeap == null) continue;

                output.WriteSubHeader($"Top {output.TopK} alloc stacks on largest heap (handle 0x{largestHeap.Handle:X}) of {row.Process.ImageName} (pid {row.Process.Id})");
                WriteTopStacks(output, "Outstanding", largestHeap.Allocs.Select(a => (a.Stack, a.Size.Bytes)));
                output.WriteBlank();
            }
        }

        // ---- Shared stack-aggregation helper (used by Part A AND Task 6's Part B) ----

        internal static void WriteTopStacks(OutputWriter output, string label, IEnumerable<(IStackSnapshot Stack, long SizeBytes)> sized)
        {
            var sizedList = sized.Where(x => x.Stack != null).ToList();
            var allGroups = sizedList
                .GroupBy(x => StackKey(x.Stack))
                .Select(g => new { Key = g.Key, TotalBytes = g.Sum(x => x.SizeBytes), Sample = g.First().Stack })
                .OrderByDescending(x => x.TotalBytes)
                .ToList();

            if (allGroups.Count == 0)
            {
                output.WriteSkipped($"    {label}: (no stacks)");
                return;
            }

            var topGroups = allGroups.Take(output.TopK).ToList();
            output.WriteData($"    {label}:");
            int idx = 0;
            foreach (var grp in topGroups)
            {
                idx++;
                output.WriteRanked(idx, topGroups.Count, $"      #{idx} {grp.TotalBytes / 1048576.0,8:F2} MB  ({sizedList.Count(x => StackKey(x.Stack) == grp.Key)} alloc(s))");
                foreach (var frame in grp.Sample.Frames.Take(12))
                {
                    output.WriteStackFrame($"        {FormatFrame(frame)}");
                }
            }
            // Tail summary for stacks
            if (allGroups.Count > topGroups.Count)
            {
                int tailCount = allGroups.Count - topGroups.Count;
                double tailMb = allGroups.Skip(topGroups.Count).Sum(x => x.TotalBytes) / 1048576.0;
                output.WriteTail($"      + {tailCount} more stack bucket(s) totaling {tailMb:F2} MB");
            }
        }

        internal static void WriteTopStacks(OutputWriter output, string label, IEnumerable<(IThreadStack Stack, long SizeBytes)> sized)
        {
            var sizedList = sized.Where(x => x.Stack != null).ToList();
            var allGroups = sizedList
                .GroupBy(x => string.Join(" | ", x.Stack.Frames.Take(12).Select(FormatFrame)))
                .Select(g => new { Key = g.Key, TotalBytes = g.Sum(x => x.SizeBytes), Sample = g.First().Stack })
                .OrderByDescending(x => x.TotalBytes)
                .ToList();

            if (allGroups.Count == 0)
            {
                output.WriteSkipped($"    {label}: (no stacks)");
                return;
            }

            var topGroups = allGroups.Take(output.TopK).ToList();
            output.WriteData($"    {label}:");
            int idx = 0;
            foreach (var grp in topGroups)
            {
                idx++;
                output.WriteRanked(idx, topGroups.Count, $"      #{idx} {grp.TotalBytes / 1024.0,10:F2} KB  ({sizedList.Count(x => string.Join(" | ", x.Stack.Frames.Take(12).Select(FormatFrame)) == grp.Key)} alloc(s))");
                foreach (var frame in grp.Sample.Frames.Take(12))
                {
                    output.WriteStackFrame($"        {FormatFrame(frame)}");
                }
            }
            // Tail summary for stacks
            if (allGroups.Count > topGroups.Count)
            {
                int tailCount = allGroups.Count - topGroups.Count;
                double tailKb = allGroups.Skip(topGroups.Count).Sum(x => x.TotalBytes) / 1024.0;
                output.WriteTail($"      + {tailCount} more stack bucket(s) totaling {tailKb:F2} KB");
            }
        }

        internal static string StackKey(IStackSnapshot stack)
        {
            // Concatenate up to 12 frames' (image!function or RVA) into a stable key.
            return string.Join(" | ", stack.Frames.Take(12).Select(FormatFrame));
        }

        internal static string FormatFrame(StackFrame frame)
        {
            if (!frame.HasValue) return "[no value]";
            string image = frame.Image?.FileName ?? "?";
            if (frame.Symbol != null && !string.IsNullOrEmpty(frame.Symbol.FunctionName))
            {
                long off = frame.Address.Value - frame.Symbol.AddressRange.BaseAddress.Value;
                return $"{image}!{frame.Symbol.FunctionName}+0x{off:X}";
            }
            return $"{image}!0x{frame.RelativeVirtualAddress.Value:X} [no symbols]";
        }
    }
}
