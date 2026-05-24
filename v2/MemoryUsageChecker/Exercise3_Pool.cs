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
    /// Implements the WPT "Memory Footprint Optimization — Exercise 3"
    /// analysis in two parts:
    /// <list type="number">
    ///   <item><b>Pool Allocations (Part A)</b> — groups outstanding pool
    ///         allocations by the first non-kernel image in the allocation
    ///         stack (the responsible driver), then by pool tag, then by
    ///         allocation stack.</item>
    ///   <item><b>Driver Code Footprint (Part B)</b> — joins the kernel-mode
    ///         portion of the resident-set snapshot with loaded driver images
    ///         to report how much physical RAM each driver's code pages
    ///         occupy. A common signature for "bloated driver pulled in by
    ///         vendor SKU".</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Requires the <c>Pool</c> data source for Part A and the <c>ResidentSet</c>
    /// data source for Part B (both captured by <c>MemoryUsageChecker.wprp</c>).
    /// The kernel-image set in <see cref="KernelImages"/> is intentionally
    /// small: any frame outside it is treated as the responsible driver.
    /// </remarks>
    internal static class Exercise3_Pool
    {
        private const long NotableNonPagedBytes = 1L * 1024 * 1024; // 1 MB
        private const long NotableDriverCodeBytes = 2L * 1024 * 1024; // 2 MB
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

        /// <summary>
        /// Runs Exercise 3. See class summary for the full analysis the method performs.
        /// </summary>
        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<IPoolAllocationDataSource> pendingPool,
            IPendingResult<IResidentSetDataSource> pendingResidentSet,
            JsonReport jsonReport = null)
        {
            output.WriteHeader("=== Exercise 3: Pool ===");
            Log.Info($"Exercise3: pendingPool.HasResult={pendingPool.HasResult}, intervals={(pendingPool.HasResult ? pendingPool.Result.Intervals.Count : 0)}");
            Log.Info($"Exercise3: pendingResidentSet.HasResult={pendingResidentSet.HasResult}");

            JsonReport.Exercise3Section jsonSection = null;
            if (jsonReport != null)
            {
                jsonSection = new JsonReport.Exercise3Section();
                jsonReport.Exercise3Pool = jsonSection;
            }

            RunPoolPart(output, pendingPool, jsonSection);
            output.WriteBlank();
            RunDriverCodeFootprintPart(output, metadata, pendingProcesses, pendingResidentSet, jsonSection);
        }

        /// <summary>
        /// Implements <b>Part A: Pool Allocations</b>. Groups every pool
        /// allocation interval by the first non-kernel image in its
        /// allocation stack (the responsible driver), ranks drivers by
        /// outstanding non-paged bytes, and for the top drivers prints
        /// per-driver top stacks plus a per-pool-tag breakdown for the #1
        /// offender. Crosses the
        /// <see cref="NotableNonPagedBytes"/> threshold renders red
        /// regardless of rank to draw the eye to absolute leaks.
        /// </summary>
        private static void RunPoolPart(OutputWriter output, IPendingResult<IPoolAllocationDataSource> pendingPool, JsonReport.Exercise3Section jsonSection)
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
                .Select(i => new { Interval = i, DriverImage = GetResponsibleDriverImage(i) })
                .GroupBy(x => x.DriverImage?.Path ?? x.DriverImage?.FileName ?? KernelInternalBucket)
                .Select(g => new
                {
                    DriverKey = g.Key,
                    DriverImage = g.Select(x => x.DriverImage).FirstOrDefault(img => img != null),
                    DriverLeaf = g.Select(x => x.DriverImage?.FileName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? g.Key,
                    NonPagedImpacting = g.Where(x => !x.Interval.PoolType.IsPaged && x.Interval.FreeTimestamp == null).Sum(x => x.Interval.AllocationRange.Size.Bytes),
                    NonPagedTransient = g.Where(x => !x.Interval.PoolType.IsPaged && x.Interval.FreeTimestamp != null).Sum(x => x.Interval.AllocationRange.Size.Bytes),
                    PagedImpacting = g.Where(x => x.Interval.PoolType.IsPaged && x.Interval.FreeTimestamp == null).Sum(x => x.Interval.AllocationRange.Size.Bytes),
                    PagedTransient = g.Where(x => x.Interval.PoolType.IsPaged && x.Interval.FreeTimestamp != null).Sum(x => x.Interval.AllocationRange.Size.Bytes),
                    AllocCount = g.LongCount(),
                    Intervals = g.Select(x => x.Interval).ToList()
                })
                .OrderByDescending(x => x.NonPagedImpacting)
                .ToList();

            var perDriver = allPerDriver.Take(output.TopN).ToList();

            JsonReport.Exercise3PoolAllocations jsonPool = null;
            if (jsonSection != null)
            {
                jsonPool = new JsonReport.Exercise3PoolAllocations();
                jsonSection.PoolAllocations = jsonPool;
            }

            output.WriteSubHeader($"Top {output.TopN} drivers by NonPaged Impacting size (KB):");
            int rank = 0;
            foreach (var row in perDriver)
            {
                rank++;
                string driverLabel = ImageFormatter.FormatDriverShort(row.DriverImage, row.DriverLeaf);
                string line =
                    $"  {driverLabel,-60}  NP-Imp {row.NonPagedImpacting / 1024.0,9:F1}  NP-Tr {row.NonPagedTransient / 1024.0,9:F1}  " +
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

                if (jsonPool != null)
                {
                    jsonPool.TopDriversByNonPagedImpactingBytes.Add(new JsonReport.RankedDriverPool
                    {
                        Rank = rank,
                        Driver = ImageFormatter.BuildDriverIdentity(row.DriverImage, row.DriverLeaf, row.DriverKey),
                        NonPagedImpactingBytes = row.NonPagedImpacting,
                        NonPagedTransientBytes = row.NonPagedTransient,
                        PagedImpactingBytes = row.PagedImpacting,
                        PagedTransientBytes = row.PagedTransient,
                        AllocationCount = row.AllocCount,
                        TopImpactingStacks = Exercise2_VirtualAllocHeap.BuildRankedStacks(
                            row.Intervals.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp == null)
                                         .Select(x => ((IStackSnapshot)x.Stack, x.AllocationRange.Size.Bytes)),
                            output.TopK),
                        TopTransientStacks = Exercise2_VirtualAllocHeap.BuildRankedStacks(
                            row.Intervals.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp != null)
                                         .Select(x => ((IStackSnapshot)x.Stack, x.AllocationRange.Size.Bytes)),
                            output.TopK)
                    });
                }
            }
            // Tail summary for drivers
            if (allPerDriver.Count > perDriver.Count)
            {
                int tailCount = allPerDriver.Count - perDriver.Count;
                long tailBytes = allPerDriver.Skip(perDriver.Count).Sum(x => x.NonPagedImpacting);
                double tailKb = tailBytes / 1024.0;
                output.WriteTail($"  + {tailCount} more drivers totaling {tailKb:F1} KB NP-Imp");
                if (jsonPool != null)
                {
                    jsonPool.TailDrivers = new JsonReport.TailSummary
                    {
                        Count = tailCount,
                        Bytes = tailBytes,
                        Megabytes = tailBytes / 1048576.0
                    };
                }
            }
            output.WriteBlank();

            // Per top-driver: top-K Impacting and Transient stacks (NonPaged)
            foreach (var row in perDriver)
            {
                string driverLabel = ImageFormatter.FormatDriverShort(row.DriverImage, row.DriverLeaf);
                output.WriteSubHeader($"Top {output.TopK} pool alloc stacks for {driverLabel} (NonPaged only)");
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
                string topDriverLabel = ImageFormatter.FormatDriverShort(topDriver.DriverImage, topDriver.DriverLeaf);
                output.WriteSubHeader($"Per-pool-tag breakdown for #1 driver {topDriverLabel} (top {output.TopK})");
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
                    jsonPool?.TopDriverTagBreakdown.Add(new JsonReport.RankedTag
                    {
                        Rank = tagRank,
                        Tag = t.Tag,
                        NonPagedImpactingBytes = t.NpImp,
                        AllocationCount = t.Count
                    });
                }
                if (allTagBreakdown.Count > tagBreakdown.Count)
                {
                    int tailCount = allTagBreakdown.Count - tagBreakdown.Count;
                    long tailBytes = allTagBreakdown.Skip(tagBreakdown.Count).Sum(x => x.NpImp);
                    double tailKb = tailBytes / 1024.0;
                    output.WriteTail($"  + {tailCount} more tags totaling {tailKb:F1} KB NP-Imp");
                    if (jsonPool != null)
                    {
                        jsonPool.TailTagBreakdown = new JsonReport.TailSummary
                        {
                            Count = tailCount,
                            Bytes = tailBytes,
                            Megabytes = tailBytes / 1048576.0
                        };
                    }
                }
            }
        }

        /// <summary>
        /// Returns the first non-kernel <see cref="Microsoft.Windows.EventTracing.Processes.IImage"/>
        /// walked from the innermost frame outward on
        /// <paramref name="interval"/>'s allocation stack. This is the
        /// driver that actually requested the allocation:
        /// <c>ExAllocatePool*</c> in frame[0] always belongs to a kernel
        /// image (<see cref="KernelImages"/>); the first frame above it
        /// outside that set is the caller. Returns <c>null</c> if every
        /// frame is a kernel image — callers should bucket that case under
        /// <see cref="KernelInternalBucket"/>.
        /// </summary>
        private static Microsoft.Windows.EventTracing.Processes.IImage GetResponsibleDriverImage(IPoolAllocationInterval interval)
        {
            foreach (var frame in interval.Stack.Frames)
            {
                var img = frame.Image;
                string image = img?.FileName;
                if (string.IsNullOrEmpty(image))
                {
                    continue;
                }
                if (KernelImages.Contains(image))
                {
                    continue;
                }
                return img;
            }
            return null;
        }

        // ---- Part B: Driver code footprint ----

        /// <summary>
        /// Implements <b>Part B: Driver Code Footprint</b>. Takes the
        /// kernel-mode portion of the LATEST resident-set snapshot
        /// (<see cref="ResidentSetPageCategory.NonProcessImagePage"/>),
        /// joins it with the union of <see cref="IProcess.Images"/> across
        /// every process so the leaf file name can be enriched with the
        /// driver's friendly name and version, then ranks drivers by
        /// resident code-page bytes. Crosses the
        /// <see cref="NotableDriverCodeBytes"/> threshold renders red.
        /// </summary>
        private static void RunDriverCodeFootprintPart(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<IResidentSetDataSource> pendingResidentSet,
            JsonReport.Exercise3Section jsonSection)
        {
            output.WriteSubHeader("--- Driver Code Footprint (File Backed Pages) ---");

            if (!pendingResidentSet.HasResult || pendingResidentSet.Result.Snapshots.Count == 0)
            {
                output.WriteSkipped("[skipped - resident-set data not present in trace]");
                return;
            }

            // Build a one-shot path -> IImage lookup so we can enrich a driver
            // row with FileDescription + FileVersion when the trace captured
            // the image load (the Loader keyword in the .wprp).
            Dictionary<string, Microsoft.Windows.EventTracing.Processes.IImage> imageByPath =
                new Dictionary<string, Microsoft.Windows.EventTracing.Processes.IImage>(StringComparer.OrdinalIgnoreCase);
            if (pendingProcesses.HasResult)
            {
                foreach (var p in pendingProcesses.Result.Processes)
                {
                    if (p.Images == null) continue;
                    foreach (var img in p.Images)
                    {
                        string path = null;
                        try { path = img.Path; } catch { }
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!imageByPath.ContainsKey(path)) imageByPath[path] = img;
                    }
                }
            }

            // Pick latest snapshot for steady-state image footprint
            var snapshot = pendingResidentSet.Result.Snapshots.OrderByDescending(s => s.Timestamp.Nanoseconds).First();

            // Filter pages that represent driver code resident in RAM
            var driverPages = snapshot.Pages.Where(p =>
                p.MemoryManagerListType == MemoryManagerListType.Active &&
                !string.IsNullOrEmpty(p.Path) &&
                p.Path.IndexOf(@"\System32\drivers\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (p.Category == ResidentSetPageCategory.Driver ||
                 p.Category == ResidentSetPageCategory.DriverFile ||
                 p.Category == ResidentSetPageCategory.Image));

            // Group by full path (preserving casing for distinct identifiers)
            var allByDriver = driverPages
                .GroupBy(p => p.Path)
                .Select(g => new
                {
                    Path = g.Key,
                    Leaf = System.IO.Path.GetFileName(g.Key),
                    Image = imageByPath.TryGetValue(g.Key, out var img) ? img : null,
                    PageCount = g.LongCount(),
                    Bytes = g.LongCount() * PageSizeBytes,
                    Mb = (g.LongCount() * PageSizeBytes) / 1024.0 / 1024.0
                })
                .OrderByDescending(x => x.Bytes)
                .ToList();

            var displayed = allByDriver.Take(output.TopN).ToList();

            if (displayed.Count == 0)
            {
                output.WriteFinding("(no driver code pages found in the resident set)");
                return;
            }

            JsonReport.Exercise3DriverCodeFootprint jsonFootprint = null;
            if (jsonSection != null)
            {
                System.DateTimeOffset snapshotWall;
                try { snapshotWall = metadata.GetWallClock(snapshot.Timestamp); }
                catch { snapshotWall = default; }
                jsonFootprint = new JsonReport.Exercise3DriverCodeFootprint
                {
                    SnapshotTimestampUtc = snapshotWall == default ? (DateTime?)null : snapshotWall.UtcDateTime
                };
                jsonSection.DriverCodeFootprint = jsonFootprint;
            }

            output.WriteSubHeader($"Top {output.TopN} drivers by code resident footprint (MB):");
            int rank = 0;
            foreach (var row in displayed)
            {
                rank++;
                string identifier = ImageFormatter.FormatDriverRow(row.Image, row.Leaf, row.Path);
                string line = $"  {row.Mb,8:F2} MB  {row.PageCount,7} pages  {identifier}";
                if (row.Bytes >= NotableDriverCodeBytes)
                {
                    output.WriteCritical(line);
                }
                else
                {
                    output.WriteRanked(rank, displayed.Count, line);
                }

                jsonFootprint?.TopDriversByResidentBytes.Add(new JsonReport.RankedDriverFootprint
                {
                    Rank = rank,
                    Driver = ImageFormatter.BuildDriverIdentity(row.Image, row.Leaf, row.Path),
                    ResidentBytes = row.Bytes
                });
            }

            // Tail summary
            if (allByDriver.Count > displayed.Count)
            {
                int tailCount = allByDriver.Count - displayed.Count;
                long tailBytes = allByDriver.Skip(displayed.Count).Sum(x => x.Bytes);
                double tailMb = tailBytes / 1048576.0;
                output.WriteTail($"  + {tailCount} more drivers totaling {tailMb:F2} MB");
                if (jsonFootprint != null)
                {
                    jsonFootprint.TailDrivers = new JsonReport.TailSummary
                    {
                        Count = tailCount,
                        Bytes = tailBytes,
                        Megabytes = tailMb
                    };
                }
            }
        }
    }
}