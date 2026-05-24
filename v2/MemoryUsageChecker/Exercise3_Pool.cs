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
    internal static class Exercise3_Pool
    {
        private const long NotableNonPagedBytes = 1L * 1024 * 1024; // 1 MB
        private const long PageSizeBytes = 4096;
        private const string KernelInternalBucket = "(kernel-internal)";

        // Windows kernel images. Pool allocations whose stack contains ONLY these
        // images are core kernel work (paging, scheduler, ETW itself). Any frame
        // outside this set is treated as the responsible driver.
        private static readonly HashSet<string> KernelImages = new(StringComparer.OrdinalIgnoreCase)
        {
            "ntoskrnl.exe",
            "ntkrnlmp.exe",
            "ntkrnlpa.exe",
            "ntkrpamp.exe",
            "ntkrla57.exe",
            "hal.dll",
        };

        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<IPoolAllocationDataSource> pendingPool,
            IPendingResult<IResidentSetDataSource> pendingResidentSet)
        {
            output.WriteHeader("=== Exercise 3: Pool ===");

            RunPoolPart(output, pendingPool);
            output.WriteBlank();
            RunDriverCodeFootprintPart(output, pendingResidentSet);
        }

        private static void RunPoolPart(OutputWriter output, IPendingResult<IPoolAllocationDataSource> pendingPool)
        {
            output.WriteSubHeader("--- Pool Allocations ---");

            if (!pendingPool.HasResult || pendingPool.Result.Intervals.Count == 0)
            {
                output.WriteSkipped("[skipped - pool allocation data not present in trace]");
                return;
            }

            var intervals = pendingPool.Result.Intervals.ToList();

            // Group by the FIRST NON-KERNEL image in the allocation stack — that's the
            // driver that actually requested the allocation. Frame[0] is always
            // ntoskrnl!ExAllocatePool*, frames just above it are kernel helpers; the
            // first frame whose image is not in KernelImages is the driver caller.
            // Allocations whose entire stack is kernel-only fall into "(kernel-internal)".
            var allPerDriver = intervals
                .Where(i => i.Stack != null && i.Stack.Frames.Count > 0)
                .GroupBy(GetResponsibleDriver)
                .Select(g => new
                {
                    Driver = g.Key,
                    NonPagedImpacting = g.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp == null).Sum(x => x.AllocationRange.Size.Bytes),
                    NonPagedTransient = g.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp != null).Sum(x => x.AllocationRange.Size.Bytes),
                    PagedImpacting = g.Where(x => x.PoolType.IsPaged && x.FreeTimestamp == null).Sum(x => x.AllocationRange.Size.Bytes),
                    PagedTransient = g.Where(x => x.PoolType.IsPaged && x.FreeTimestamp != null).Sum(x => x.AllocationRange.Size.Bytes),
                    AllocCount = g.LongCount(),
                    Intervals = g.ToList()
                })
                .OrderByDescending(x => x.NonPagedImpacting)
                .ToList();

            var perDriver = allPerDriver.Take(output.TopN).ToList();

            output.WriteSubHeader($"Top {output.TopN} drivers by NonPaged Impacting size (KB):");
            int rank = 0;
            foreach (var row in perDriver)
            {
                rank++;
                string line =
                    $"  {row.Driver,-28}  NP-Imp {row.NonPagedImpacting / 1024.0,9:F1}  NP-Tr {row.NonPagedTransient / 1024.0,9:F1}  " +
                    $"P-Imp {row.PagedImpacting / 1024.0,9:F1}  P-Tr {row.PagedTransient / 1024.0,9:F1}  KB  ({row.AllocCount} allocs)";
                if (row.NonPagedImpacting >= NotableNonPagedBytes)
                {
                    // Threshold breach — force red regardless of rank.
                    output.WriteCritical(line);
                }
                else
                {
                    output.WriteRanked(rank, perDriver.Count, line);
                }
            }
            // Tail summary for drivers
            if (allPerDriver.Count > perDriver.Count)
            {
                int tailCount = allPerDriver.Count - perDriver.Count;
                double tailKb = allPerDriver.Skip(perDriver.Count).Sum(x => x.NonPagedImpacting) / 1024.0;
                output.WriteTail($"  + {tailCount} more drivers totaling {tailKb:F1} KB NP-Imp");
            }
            output.WriteBlank();

            // Per top-driver: top-K Impacting and Transient stacks (NonPaged)
            foreach (var row in perDriver)
            {
                output.WriteSubHeader($"Top {output.TopK} pool alloc stacks for {row.Driver} (NonPaged only)");
                Exercise2_VirtualAllocHeap.WriteTopStacks(output, "Impacting",
                    row.Intervals.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp == null)
                                 .Select(x => (x.Stack, x.AllocationRange.Size.Bytes)));
                Exercise2_VirtualAllocHeap.WriteTopStacks(output, "Transient",
                    row.Intervals.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp != null)
                                 .Select(x => (x.Stack, x.AllocationRange.Size.Bytes)));
                output.WriteBlank();
            }

            // Per #1 driver: per-pool-tag breakdown (ranked + tail summary)
            var topDriver = perDriver.FirstOrDefault();
            if (topDriver != null)
            {
                output.WriteSubHeader($"Per-pool-tag breakdown for #1 driver {topDriver.Driver} (top {output.TopK})");
                var allTagBreakdown = topDriver.Intervals
                    .GroupBy(x => string.IsNullOrEmpty(x.Tag) ? "(no tag)" : x.Tag)
                    .Select(g => new
                    {
                        Tag = g.Key,
                        NpImp = g.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp == null).Sum(x => x.AllocationRange.Size.Bytes),
                        Count = g.LongCount()
                    })
                    .OrderByDescending(x => x.NpImp)
                    .ToList();

                var tagBreakdown = allTagBreakdown.Take(output.TopK).ToList();

                int tagRank = 0;
                foreach (var t in tagBreakdown)
                {
                    tagRank++;
                    output.WriteRanked(tagRank, tagBreakdown.Count, $"  tag {t.Tag,-8}  NP-Imp {t.NpImp / 1024.0,9:F1} KB  ({t.Count} allocs)");
                }
                if (allTagBreakdown.Count > tagBreakdown.Count)
                {
                    int tailCount = allTagBreakdown.Count - tagBreakdown.Count;
                    double tailKb = allTagBreakdown.Skip(tagBreakdown.Count).Sum(x => x.NpImp) / 1024.0;
                    output.WriteTail($"  + {tailCount} more tags totaling {tailKb:F1} KB NP-Imp");
                }
            }
        }

        // Returns the file name of the first non-kernel image walked from the
        // innermost frame outward. Falls back to KernelInternalBucket when every
        // frame belongs to a Windows kernel image.
        private static string GetResponsibleDriver(IPoolAllocationInterval interval)
        {
            foreach (var frame in interval.Stack.Frames)
            {
                string image = frame.Image?.FileName;
                if (string.IsNullOrEmpty(image))
                {
                    continue;
                }
                if (KernelImages.Contains(image))
                {
                    continue;
                }
                return image;
            }
            return KernelInternalBucket;
        }

        // ---- Part B: Driver code footprint (filled in Task 8) ----
        private static void RunDriverCodeFootprintPart(OutputWriter output, IPendingResult<IResidentSetDataSource> pendingResidentSet)
        {
            output.WriteSubHeader("--- Driver Code Footprint (File Backed Pages) ---");
            output.WriteSkipped("[not yet implemented]");
        }
    }
}