// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Implements the WPT "Memory Footprint Optimization — Exercise 2"
    /// analysis in two parts:
    /// <list type="number">
    ///   <item><b>VirtualAlloc Commit Lifetimes</b> — splits each process'
    ///         private commit into "Impacting" (still committed at trace stop)
    ///         and "Transient" (decommitted before stop), then highlights the
    ///         top commit stacks responsible for the impacting bytes.</item>
    ///   <item><b>Heap Snapshots</b> — for each user-mode heap snapshot in
    ///         the trace, reports the top processes by managed-heap size and
    ///         the top allocation stacks within each.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Requires the <c>VirtualAlloc</c> and <c>Heap</c> data sources
    /// (captured by the matching profiles in <c>MemoryUsageChecker.wprp</c>).
    /// Stacks are best-effort: when symbols are unavailable they will show
    /// <c>[no symbols]</c> per frame instead of failing the run.
    /// </remarks>
    internal static class Exercise2_VirtualAllocHeap
    {
        private const long NotableImpactingBytes = 10L * 1024 * 1024; // 10 MB

        /// <summary>
        /// Runs Exercise 2. See class summary for the full analysis the method performs.
        /// </summary>
        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<ICommitDataSource> pendingCommit,
            IPendingResult<IHeapSnapshotDataSource> pendingHeap,
            JsonReport jsonReport = null)
        {
            output.WriteHeader("=== Exercise 2: VirtualAlloc + Heap ===");
            Log.Info($"Exercise2: pendingCommit.HasResult={pendingCommit.HasResult}, lifetimes={(pendingCommit.HasResult ? pendingCommit.Result.CommitLifetimes.Count : 0)}");
            Log.Info($"Exercise2: pendingHeap.HasResult={pendingHeap.HasResult}, snapshots={(pendingHeap.HasResult ? pendingHeap.Result.Snapshots.Count : 0)}");

            JsonReport.Exercise2Section jsonSection = null;
            if (jsonReport != null)
            {
                jsonSection = new JsonReport.Exercise2Section();
                jsonReport.Exercise2VirtualAllocHeap = jsonSection;
            }

            RunVirtualAllocPart(output, pendingCommit, jsonSection);
            output.WriteBlank();
            RunHeapPart(output, pendingHeap, jsonSection);
        }

        /// <summary>
        /// Implements <b>Part A: VirtualAlloc Commit Lifetimes</b>. Splits
        /// every process's <see cref="CommitLifetimeType.VirtualMemory"/>
        /// commits into "Impacting" (no <c>DecommitEvent</c> seen before
        /// trace stop) and "Transient" (decommitted during the trace), ranks
        /// the processes by Impacting size, then prints the top commit
        /// stacks for both buckets so the reader can see the call sites
        /// allocating the largest committed ranges.
        /// </summary>
        private static void RunVirtualAllocPart(OutputWriter output, IPendingResult<ICommitDataSource> pendingCommit, JsonReport.Exercise2Section jsonSection)
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

            var perProcess = allPerProcess
                .Where(x => x.Impacting >= output.MinDisplayBytes)
                .Take(output.TopN)
                .ToList();

            JsonReport.Exercise2VirtualAlloc jsonVa = null;
            if (jsonSection != null)
            {
                jsonVa = new JsonReport.Exercise2VirtualAlloc();
                jsonSection.VirtualAlloc = jsonVa;
            }

            output.WriteSubHeader($"Top {output.TopN} processes by Impacting commit size (MB){output.MinDisplaySuffix}:");
            int rank = 0;
            foreach (var row in perProcess)
            {
                rank++;
                string line = $"  {ImageFormatter.FormatProcess(row.Process)}  Impacting {row.Impacting / 1048576.0,8:F2}  Transient {row.Transient / 1048576.0,8:F2}  Total {row.Total / 1048576.0,8:F2}  MB";
                if (row.Impacting >= NotableImpactingBytes)
                {
                    // Override the rank-based tier: threshold breach forces red.
                    output.WriteCritical(line);
                }
                else
                {
                    output.WriteRanked(rank, perProcess.Count, line);
                }

                if (jsonVa != null)
                {
                    jsonVa.TopProcessesByImpactingBytes.Add(new JsonReport.RankedProcessVirtualAlloc
                    {
                        Rank = rank,
                        Process = ImageFormatter.BuildProcessIdentity(row.Process),
                        ImpactingBytes = row.Impacting,
                        TransientBytes = row.Transient,
                        TotalBytes = row.Total,
                        TopImpactingStacks = BuildRankedStacks(row.Lifetimes.Where(x => x.DecommitEvent?.Timestamp == null).Select(x => ((IStackSnapshot)x.CommitEvent?.Stack, x.AddressRange.Size.Bytes)), output.TopK, output.MinDisplayBytes),
                        TopTransientStacks = BuildRankedStacks(row.Lifetimes.Where(x => x.DecommitEvent?.Timestamp != null).Select(x => ((IStackSnapshot)x.CommitEvent?.Stack, x.AddressRange.Size.Bytes)), output.TopK, output.MinDisplayBytes)
                    });
                }
            }
            // Tail summary
            if (allPerProcess.Count > perProcess.Count)
            {
                int tailCount = allPerProcess.Count - perProcess.Count;
                long tailBytes = allPerProcess.Skip(perProcess.Count).Sum(x => x.Impacting);
                double tailMb = tailBytes / 1048576.0;
                output.WriteTail($"  + {tailCount} more processes totaling {tailMb:F2} MB Impacting");
                if (jsonVa != null)
                {
                    jsonVa.TailProcesses = new JsonReport.TailSummary
                    {
                        Count = tailCount,
                        Bytes = tailBytes,
                        Megabytes = tailMb
                    };
                }
            }
            output.WriteBlank();

            // Per-process stack drill-downs: skipped entirely when --no-symbols
            // (raw addresses aren't actionable) and capped at the first
            // --top-stacks rows of the outer table so a large --top doesn't
            // explode the report. The outer ranked table above already lists
            // up to --top processes.
            int drillDownLimit = Math.Min(output.TopStacks, perProcess.Count);
            if (output.NoSymbols)
            {
                output.WriteSkipped("Per-process commit-stack drill-down skipped (--no-symbols) — re-run without --no-symbols for actionable stacks.");
            }
            else if (drillDownLimit == 0)
            {
                output.WriteSkipped("Per-process commit-stack drill-down skipped (--top-stacks 0).");
            }
            else
            {
                if (perProcess.Count > drillDownLimit)
                {
                    output.WriteTail($"(Drill-down emitted for the top {drillDownLimit} of {perProcess.Count} ranked process(es); raise --top-stacks to see more.)");
                }
                int drilled = 0;
                foreach (var row in perProcess)
                {
                    if (drilled >= drillDownLimit) break;
                    drilled++;
                    output.WriteSubHeader($"Top {output.TopK} commit stacks for {ImageFormatter.FormatProcessShort(row.Process)}");

                    WriteTopStacks(output, "Impacting", row.Lifetimes.Where(x => x.DecommitEvent?.Timestamp == null).Select(x => (x.CommitEvent?.Stack, x.AddressRange.Size.Bytes)));
                    WriteTopStacks(output, "Transient", row.Lifetimes.Where(x => x.DecommitEvent?.Timestamp != null).Select(x => (x.CommitEvent?.Stack, x.AddressRange.Size.Bytes)));
                    output.WriteBlank();
                }
            }
        }

        // ---- Part B: Heap snapshots ----

        /// <summary>
        /// Implements <b>Part B: Heap Snapshots</b>. For each process, picks
        /// the LATEST heap snapshot as the canonical "outstanding heap" view
        /// at trace stop, ranks processes by outstanding bytes, and for each
        /// top process prints the Top K allocation stacks on its largest
        /// heap handle. Skipped (with guidance) when the trace contains no
        /// heap data — heap tracing requires the per-process
        /// <c>TracingFlags=1</c> registry key documented in WPT Exercise 2
        /// Step 1.1.
        /// </summary>
        private static void RunHeapPart(OutputWriter output, IPendingResult<IHeapSnapshotDataSource> pendingHeap, JsonReport.Exercise2Section jsonSection)
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

            var perProcessSummary = allPerProcessSummary
                .Where(x => x.OutstandingBytes >= output.MinDisplayBytes)
                .Take(output.TopN)
                .ToList();

            JsonReport.Exercise2Heap jsonHeap = null;
            if (jsonSection != null)
            {
                jsonHeap = new JsonReport.Exercise2Heap();
                jsonSection.Heap = jsonHeap;
            }

            output.WriteSubHeader($"Top {output.TopN} processes by outstanding heap size (KB){output.MinDisplaySuffix}:");
            int rank = 0;
            foreach (var row in perProcessSummary)
            {
                rank++;
                string line =
                    $"  {ImageFormatter.FormatProcess(row.Process)}  " +
                    $"heaps {row.HeapCount,3}  allocations {row.AllocCount,8}  outstanding {row.OutstandingBytes / 1024.0,10:F2} KB";
                output.WriteRanked(rank, perProcessSummary.Count, line);

                // Largest heap handle for this process by outstanding bytes
                var largestHeap = row.Snapshot.Allocations
                    .GroupBy(a => a.HeapHandle)
                    .Select(g => new { Handle = g.Key, Bytes = g.Sum(a => a.Size.Bytes), Allocs = g.ToList() })
                    .OrderByDescending(g => g.Bytes)
                    .FirstOrDefault();

                if (jsonHeap != null)
                {
                    jsonHeap.TopProcessesByOutstandingBytes.Add(new JsonReport.RankedProcessHeap
                    {
                        Rank = rank,
                        Process = ImageFormatter.BuildProcessIdentity(row.Process),
                        HeapCount = row.HeapCount,
                        AllocationCount = row.AllocCount,
                        OutstandingBytes = row.OutstandingBytes,
                        LargestHeapHandle = largestHeap != null ? $"0x{largestHeap.Handle:X}" : null,
                        TopAllocationStacks = largestHeap != null
                            ? BuildRankedStacksFromThreadStacks(largestHeap.Allocs.Select(a => (a.Stack, a.Size.Bytes)), output.TopK, output.MinDisplayBytes)
                            : new List<JsonReport.RankedStack>()
                    });
                }
            }
            // Tail summary
            if (allPerProcessSummary.Count > perProcessSummary.Count)
            {
                int tailCount = allPerProcessSummary.Count - perProcessSummary.Count;
                long tailBytes = allPerProcessSummary.Skip(perProcessSummary.Count).Sum(x => x.OutstandingBytes);
                double tailKb = tailBytes / 1024.0;
                output.WriteTail($"  + {tailCount} more processes totaling {tailKb:F2} KB outstanding");
                if (jsonHeap != null)
                {
                    jsonHeap.TailProcesses = new JsonReport.TailSummary
                    {
                        Count = tailCount,
                        Bytes = tailBytes,
                        Megabytes = tailBytes / 1048576.0
                    };
                }
            }
            output.WriteBlank();

            int heapDrillDownLimit = Math.Min(output.TopStacks, perProcessSummary.Count);
            if (output.NoSymbols)
            {
                output.WriteSkipped("Per-process heap-stack drill-down skipped (--no-symbols) — re-run without --no-symbols for actionable stacks.");
                return;
            }
            if (heapDrillDownLimit == 0)
            {
                output.WriteSkipped("Per-process heap-stack drill-down skipped (--top-stacks 0).");
                return;
            }
            if (perProcessSummary.Count > heapDrillDownLimit)
            {
                output.WriteTail($"(Drill-down emitted for the top {heapDrillDownLimit} of {perProcessSummary.Count} ranked process(es); raise --top-stacks to see more.)");
            }
            int heapDrilled = 0;
            foreach (var row in perProcessSummary)
            {
                if (heapDrilled >= heapDrillDownLimit) break;
                heapDrilled++;

                // Largest heap handle for this process by outstanding bytes
                var largestHeap = row.Snapshot.Allocations
                    .GroupBy(a => a.HeapHandle)
                    .Select(g => new { Handle = g.Key, Bytes = g.Sum(a => a.Size.Bytes), Allocs = g.ToList() })
                    .OrderByDescending(g => g.Bytes)
                    .FirstOrDefault();

                if (largestHeap == null) continue;

                output.WriteSubHeader($"Top {output.TopK} alloc stacks on largest heap (handle 0x{largestHeap.Handle:X}) of {ImageFormatter.FormatProcessShort(row.Process)}");
                WriteTopStacks(output, "Outstanding", largestHeap.Allocs.Select(a => (a.Stack, a.Size.Bytes)));
                output.WriteBlank();
            }
        }

        // ---- Shared stack-aggregation helpers (used by Part A AND Part B) ----

        /// <summary>
        /// Groups <paramref name="sized"/> by a 12-frame stack key, sums the
        /// bytes per group, ranks descending, prints the Top K groups
        /// (one bucket per stack, with the bucket size and one sample of
        /// the stack), then a tail summary line. Overload used by the
        /// VirtualAlloc path which provides <see cref="IStackSnapshot"/>s.
        /// </summary>
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

            var topGroups = allGroups.Where(x => x.TotalBytes >= output.MinDisplayBytes).Take(output.TopK).ToList();
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

        /// <summary>
        /// Sibling overload for the heap path, which provides
        /// <see cref="IThreadStack"/>s (no <see cref="IStackSnapshot"/>
        /// available in the heap data source). Behavior is identical: group
        /// by frame-list key, sum bytes, rank, print Top K + tail.
        /// </summary>
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

            var topGroups = allGroups.Where(x => x.TotalBytes >= output.MinDisplayBytes).Take(output.TopK).ToList();
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

        /// <summary>
        /// Returns a stable string key built from up to the first 12 frames
        /// of <paramref name="stack"/>. Used to bucket stacks that differ
        /// only deep in the kernel/runtime — so two callers that share the
        /// top 12 frames are treated as the same logical allocation site.
        /// </summary>
        internal static string StackKey(IStackSnapshot stack)
        {
            return string.Join(" | ", stack.Frames.Take(12).Select(FormatFrame));
        }

        /// <summary>
        /// Formats a single stack frame as <c>image!function+0xOFFSET</c>
        /// when symbols loaded, or <c>image!0xRVA [no symbols]</c> when
        /// symbol resolution failed for that frame. Returns
        /// <c>"[no value]"</c> when the frame has no value (e.g. tail
        /// padding in a truncated stack).
        /// </summary>
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

        /// <summary>
        /// JSON-side sibling of <see cref="WriteTopStacks(OutputWriter, string, IEnumerable{ValueTuple{IStackSnapshot, long}})"/>:
        /// groups <paramref name="sized"/> by the same 12-frame stack key,
        /// sums bytes per group, ranks descending, and returns the Top K
        /// buckets as <see cref="JsonReport.RankedStack"/> objects with raw
        /// byte totals and the sample stack's first 12 frames. Returns an
        /// empty list when no stacks are available.
        /// </summary>
        internal static List<JsonReport.RankedStack> BuildRankedStacks(IEnumerable<(IStackSnapshot Stack, long SizeBytes)> sized, int topK, long minDisplayBytes)
        {
            var sizedList = sized.Where(x => x.Stack != null).ToList();
            var allGroups = sizedList
                .GroupBy(x => StackKey(x.Stack))
                .Select(g => new { Key = g.Key, TotalBytes = g.Sum(x => x.SizeBytes), AllocationCount = (long)g.Count(), Sample = g.First().Stack })
                .OrderByDescending(x => x.TotalBytes)
                .ToList();

            var result = new List<JsonReport.RankedStack>();
            int rank = 0;
            foreach (var grp in allGroups.Where(g => g.TotalBytes >= minDisplayBytes).Take(topK))
            {
                rank++;
                result.Add(new JsonReport.RankedStack
                {
                    Rank = rank,
                    TotalBytes = grp.TotalBytes,
                    AllocationCount = grp.AllocationCount,
                    Frames = grp.Sample.Frames.Take(12).Select(FormatFrame).ToList()
                });
            }
            return result;
        }

        /// <summary>
        /// Sibling of <see cref="BuildRankedStacks(IEnumerable{ValueTuple{IStackSnapshot, long}}, int, long)"/>
        /// for the heap path, which provides <see cref="IThreadStack"/>s
        /// instead of <see cref="IStackSnapshot"/>s.
        /// </summary>
        internal static List<JsonReport.RankedStack> BuildRankedStacksFromThreadStacks(IEnumerable<(IThreadStack Stack, long SizeBytes)> sized, int topK, long minDisplayBytes)
        {
            var sizedList = sized.Where(x => x.Stack != null).ToList();
            var allGroups = sizedList
                .GroupBy(x => string.Join(" | ", x.Stack.Frames.Take(12).Select(FormatFrame)))
                .Select(g => new { Key = g.Key, TotalBytes = g.Sum(x => x.SizeBytes), AllocationCount = (long)g.Count(), Sample = g.First().Stack })
                .OrderByDescending(x => x.TotalBytes)
                .ToList();

            var result = new List<JsonReport.RankedStack>();
            int rank = 0;
            foreach (var grp in allGroups.Where(g => g.TotalBytes >= minDisplayBytes).Take(topK))
            {
                rank++;
                result.Add(new JsonReport.RankedStack
                {
                    Rank = rank,
                    TotalBytes = grp.TotalBytes,
                    AllocationCount = grp.AllocationCount,
                    Frames = grp.Sample.Frames.Take(12).Select(FormatFrame).ToList()
                });
            }
            return result;
        }
    }
}
