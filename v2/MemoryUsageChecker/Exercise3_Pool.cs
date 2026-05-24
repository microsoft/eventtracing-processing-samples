// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        /// Per-driver accumulator populated by a single streaming pass over
        /// the pool allocation intervals. Replaces the previous LINQ
        /// <c>GroupBy / .Sum() × 4 / .ToList()</c> pipeline which iterated
        /// every group six times — catastrophic on large traces (the
        /// 11+ GB captures produced by long pool-with-stacks runs can hold
        /// tens of millions of intervals).
        /// </summary>
        private sealed class DriverAcc
        {
            public IImage DriverImage;
            public string DriverLeaf;
            public long NonPagedImpacting;
            public long NonPagedTransient;
            public long PagedImpacting;
            public long PagedTransient;
            public long AllocCount;
            /// <summary>
            /// Per-interval back-references. Populated for every driver
            /// during the streaming pass, then released for non-Top-N
            /// drivers right after the rank cut so memory usage scales
            /// with the displayed Top-N rather than the raw interval count.
            /// </summary>
            public List<IPoolAllocationInterval> Intervals;
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

            // Parallel streaming pass: partition the interval list into per-thread
            // accumulators, then merge. On modern multi-core CPUs this is the dominant
            // win on huge traces — an 11+ GB capture with tens of millions of pool
            // intervals (the pathological case the user hit) drops from "stuck for
            // many minutes" to a few seconds of pure CPU saturation.
            IReadOnlyList<IPoolAllocationInterval> intervals = pendingPool.Result.Intervals;
            int intervalCount = intervals.Count;
            Log.Info($"Exercise3 Pool: aggregating {intervalCount:N0} intervals across {Environment.ProcessorCount} cores...");
            output.WriteInfo($"Aggregating {intervalCount:N0} pool allocations across {Environment.ProcessorCount} cores...");

            DateTime aggregateStart = DateTime.UtcNow;
            long processedAtomic = 0;
            long skippedNoStack = 0;

            // Background ticker: log progress every 5s so a multi-minute aggregation
            // on a huge trace no longer looks like a hang. The ticker stops as soon
            // as the Parallel.ForEach below completes.
            using var progressCts = new CancellationTokenSource();
            Task progressTask = Task.Run(async () =>
            {
                try
                {
                    while (!progressCts.IsCancellationRequested)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(5), progressCts.Token).ConfigureAwait(false); }
                        catch (TaskCanceledException) { break; }
                        long cur = Interlocked.Read(ref processedAtomic);
                        double pct = intervalCount > 0 ? (100.0 * cur / intervalCount) : 0.0;
                        double elapsedSec = (DateTime.UtcNow - aggregateStart).TotalSeconds;
                        double rate = elapsedSec > 0 ? cur / elapsedSec : 0;
                        string line = $"  ... aggregated {cur:N0} of {intervalCount:N0} ({pct,5:F1}%) in {elapsedSec,5:F1}s ({rate / 1_000_000.0:F1} M/s)";
                        Log.Info(line);
                        // Use Console.Error so progress doesn't pollute the result file
                        // (Console.Out is owned by OutputWriter for the result mirror).
                        Console.Error.WriteLine(line);
                    }
                }
                catch (Exception ex) { Log.Error("Pool progress ticker failed", ex); }
            });

            // Partition by index range so each worker walks a slice of the
            // IReadOnlyList without enumerator overhead. Per-thread dictionaries
            // avoid lock contention; we merge at the end (only ~hundreds of keys).
            var partitioner = System.Collections.Concurrent.Partitioner.Create(0, intervalCount);
            var perThreadDictionaries = new ConcurrentBag<Dictionary<string, DriverAcc>>();

            Parallel.ForEach(
                partitioner,
                () => new Dictionary<string, DriverAcc>(capacity: 64, StringComparer.Ordinal),
                (range, _, local) =>
                {
                    long localProcessed = 0;
                    long localSkipped = 0;
                    for (int idx = range.Item1; idx < range.Item2; idx++)
                    {
                        IPoolAllocationInterval i = intervals[idx];
                        localProcessed++;
                        if (i.Stack == null || i.Stack.Frames.Count == 0)
                        {
                            localSkipped++;
                            continue;
                        }

                        IImage image = GetResponsibleDriverImage(i);
                        string key = image?.Path ?? image?.FileName ?? KernelInternalBucket;

                        if (!local.TryGetValue(key, out DriverAcc acc))
                        {
                            acc = new DriverAcc
                            {
                                DriverImage = image,
                                DriverLeaf = image?.FileName ?? key,
                                Intervals = new List<IPoolAllocationInterval>(),
                            };
                            local[key] = acc;
                        }
                        else if (acc.DriverImage == null && image != null)
                        {
                            acc.DriverImage = image;
                            if (string.IsNullOrEmpty(acc.DriverLeaf) || acc.DriverLeaf == key)
                            {
                                acc.DriverLeaf = image.FileName ?? acc.DriverLeaf;
                            }
                        }

                        long bytes = i.AllocationRange.Size.Bytes;
                        bool paged = i.PoolType.IsPaged;
                        bool freed = i.FreeTimestamp != null;
                        if (!paged && !freed) acc.NonPagedImpacting += bytes;
                        else if (!paged && freed) acc.NonPagedTransient += bytes;
                        else if (paged && !freed) acc.PagedImpacting += bytes;
                        else acc.PagedTransient += bytes;
                        acc.AllocCount++;
                        acc.Intervals.Add(i);
                    }
                    Interlocked.Add(ref processedAtomic, localProcessed);
                    Interlocked.Add(ref skippedNoStack, localSkipped);
                    return local;
                },
                local => perThreadDictionaries.Add(local));

            // Stop the progress ticker and let it print its final tick.
            progressCts.Cancel();
            try { progressTask.Wait(TimeSpan.FromSeconds(2)); } catch { }

            // Merge per-thread dictionaries. The key space is small (number of
            // distinct drivers, typically < 500) so this is cheap.
            var accByKey = new Dictionary<string, DriverAcc>(capacity: 256, StringComparer.Ordinal);
            foreach (var local in perThreadDictionaries)
            {
                foreach (var kvp in local)
                {
                    if (!accByKey.TryGetValue(kvp.Key, out DriverAcc agg))
                    {
                        accByKey[kvp.Key] = kvp.Value;
                    }
                    else
                    {
                        agg.NonPagedImpacting += kvp.Value.NonPagedImpacting;
                        agg.NonPagedTransient += kvp.Value.NonPagedTransient;
                        agg.PagedImpacting += kvp.Value.PagedImpacting;
                        agg.PagedTransient += kvp.Value.PagedTransient;
                        agg.AllocCount += kvp.Value.AllocCount;
                        agg.Intervals.AddRange(kvp.Value.Intervals);
                        if (agg.DriverImage == null && kvp.Value.DriverImage != null)
                        {
                            agg.DriverImage = kvp.Value.DriverImage;
                            if (string.IsNullOrEmpty(agg.DriverLeaf) || agg.DriverLeaf == kvp.Key)
                            {
                                agg.DriverLeaf = kvp.Value.DriverImage.FileName ?? agg.DriverLeaf;
                            }
                        }
                    }
                }
            }

            double aggregateSec = (DateTime.UtcNow - aggregateStart).TotalSeconds;
            string completeLine = $"Aggregated {processedAtomic:N0} intervals into {accByKey.Count:N0} driver buckets in {aggregateSec:F1}s (skipped {Interlocked.Read(ref skippedNoStack):N0} stack-less).";
            Log.Info("Exercise3 Pool: " + completeLine);
            output.WriteInfo(completeLine);

            var allPerDriver = accByKey
                .Select(kvp => new { DriverKey = kvp.Key, Acc = kvp.Value })
                .OrderByDescending(x => x.Acc.NonPagedImpacting)
                .ToList();

            var perDriver = allPerDriver
                .Where(x => x.Acc.NonPagedImpacting >= output.MinDisplayBytes)
                .Take(output.TopN)
                .ToList();

            // Release per-interval references for drivers that didn't make the
            // Top-N cut so working-set memory scales with the displayed set
            // rather than the raw interval count.
            var topKeys = new HashSet<string>(perDriver.Select(x => x.DriverKey), StringComparer.Ordinal);
            foreach (var kvp in accByKey)
            {
                if (!topKeys.Contains(kvp.Key))
                {
                    kvp.Value.Intervals = null;
                }
            }

            JsonReport.Exercise3PoolAllocations jsonPool = null;
            if (jsonSection != null)
            {
                jsonPool = new JsonReport.Exercise3PoolAllocations();
                jsonSection.PoolAllocations = jsonPool;
            }

            output.WriteSubHeader($"Top {output.TopN} drivers by NonPaged Impacting size (KB){output.MinDisplaySuffix}:");
            int rank = 0;
            foreach (var row in perDriver)
            {
                rank++;
                DriverAcc acc = row.Acc;
                string driverLabel = ImageFormatter.FormatDriverShort(acc.DriverImage, acc.DriverLeaf);
                string line =
                    $"  {driverLabel,-60}  NP-Imp {acc.NonPagedImpacting / 1024.0,9:F1}  NP-Tr {acc.NonPagedTransient / 1024.0,9:F1}  " +
                    $"P-Imp {acc.PagedImpacting / 1024.0,9:F1}  P-Tr {acc.PagedTransient / 1024.0,9:F1}  KB  ({acc.AllocCount} allocs)";
                if (acc.NonPagedImpacting >= NotableNonPagedBytes)
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
                        Driver = ImageFormatter.BuildDriverIdentity(acc.DriverImage, acc.DriverLeaf, row.DriverKey),
                        NonPagedImpactingBytes = acc.NonPagedImpacting,
                        NonPagedTransientBytes = acc.NonPagedTransient,
                        PagedImpactingBytes = acc.PagedImpacting,
                        PagedTransientBytes = acc.PagedTransient,
                        AllocationCount = acc.AllocCount,
                        TopImpactingStacks = Exercise2_VirtualAllocHeap.BuildRankedStacks(
                            acc.Intervals.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp == null)
                                         .Select(x => ((IStackSnapshot)x.Stack, x.AllocationRange.Size.Bytes)),
                            output.TopK,
                            output.MinDisplayBytes),
                        // Transient (already-freed) pool stacks are intentionally
                        // omitted from the JSON to keep the actionable signal high
                        // — they are not leaks and the per-driver Transient byte
                        // total above is sufficient context.
                        TopTransientStacks = new List<JsonReport.RankedStack>()
                    });
                }
            }
            // Tail summary for drivers
            if (allPerDriver.Count > perDriver.Count)
            {
                int tailCount = allPerDriver.Count - perDriver.Count;
                long tailBytes = allPerDriver.Skip(perDriver.Count).Sum(x => x.Acc.NonPagedImpacting);
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

            // Per top-driver: top-K alloc stacks for OUTSTANDING (Impacting)
            // allocations only. Transient (already-freed) allocations are
            // intentionally skipped from the stack render because a freed
            // allocation is by definition not a leak — surfacing its top stack
            // would be noise that dilutes the actionable signal. The Transient
            // total is still shown in the per-driver row above for context.
            // Drill-down is also suppressed entirely under --no-symbols (raw
            // ntoskrnl!0xRVA frames aren't actionable) and capped at the first
            // --top-stacks ranked drivers.
            int poolDrillDownLimit = Math.Min(output.TopStacks, perDriver.Count);
            if (output.NoSymbols)
            {
                output.WriteSkipped("Per-driver pool-stack drill-down skipped (--no-symbols) — re-run without --no-symbols for actionable stacks.");
            }
            else if (poolDrillDownLimit == 0)
            {
                output.WriteSkipped("Per-driver pool-stack drill-down skipped (--top-stacks 0).");
            }
            else
            {
                if (perDriver.Count > poolDrillDownLimit)
                {
                    output.WriteTail($"(Drill-down emitted for the top {poolDrillDownLimit} of {perDriver.Count} ranked driver(s); raise --top-stacks to see more.)");
                }
                int drilledDrivers = 0;
                foreach (var row in perDriver)
                {
                    if (drilledDrivers >= poolDrillDownLimit) break;
                    drilledDrivers++;
                    DriverAcc acc = row.Acc;
                    string driverLabel = ImageFormatter.FormatDriverShort(acc.DriverImage, acc.DriverLeaf);
                    output.WriteSubHeader($"Top {output.TopK} outstanding NonPaged pool alloc stacks for {driverLabel}");
                    Exercise2_VirtualAllocHeap.WriteTopStacks(output, "Impacting",
                        acc.Intervals.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp == null)
                                     .Select(x => (x.Stack, x.AllocationRange.Size.Bytes)));
                    output.WriteBlank();
                }
            }

            // Per-pool-tag breakdown for the top drivers. Previously emitted
            // only for #1; we now drill into the top min(--top-stacks, 3)
            // drivers because the 2nd and 3rd worst offenders are equally
            // actionable signals. The tag column is the strongest
            // "where do I look in the driver" hint when symbols are missing,
            // so we keep emitting it under --no-symbols too.
            int tagDriverLimit = Math.Min(Math.Min(output.TopStacks, 3), perDriver.Count);
            for (int td = 0; td < tagDriverLimit; td++)
            {
                var entry = perDriver[td];
                DriverAcc topAcc = entry.Acc;
                if (topAcc.Intervals == null) continue;
                string topDriverLabel = ImageFormatter.FormatDriverShort(topAcc.DriverImage, topAcc.DriverLeaf);
                output.WriteSubHeader($"Per-pool-tag breakdown for #{td + 1} driver {topDriverLabel} (top {output.TopK})");
                var allTagBreakdown = topAcc.Intervals
                    .GroupBy(x => string.IsNullOrEmpty(x.Tag) ? "(no tag)" : x.Tag)
                    .Select(g => new
                    {
                        Tag = g.Key,
                        NpImp = g.Where(x => !x.PoolType.IsPaged && x.FreeTimestamp == null).Sum(x => x.AllocationRange.Size.Bytes),
                        Count = g.LongCount()
                    })
                    .OrderByDescending(x => x.NpImp)
                    .ToList();

                var tagBreakdown = allTagBreakdown.Where(t => t.NpImp >= output.MinDisplayBytes).Take(output.TopK).ToList();

                int tagRank = 0;
                foreach (var t in tagBreakdown)
                {
                    tagRank++;
                    output.WriteRanked(tagRank, tagBreakdown.Count, $"  tag {t.Tag,-8}  NP-Imp {t.NpImp / 1024.0,9:F1} KB  ({t.Count} allocs)");
                    // Only the #1 driver's tag breakdown is persisted to the
                    // JSON sidecar today to preserve schema stability; the
                    // text report carries the top-3 view above.
                    if (td == 0)
                    {
                        jsonPool?.TopDriverTagBreakdown.Add(new JsonReport.RankedTag
                        {
                            Rank = tagRank,
                            Tag = t.Tag,
                            NonPagedImpactingBytes = t.NpImp,
                            AllocationCount = t.Count
                        });
                    }
                }
                if (allTagBreakdown.Count > tagBreakdown.Count)
                {
                    int tailCount = allTagBreakdown.Count - tagBreakdown.Count;
                    long tailBytes = allTagBreakdown.Skip(tagBreakdown.Count).Sum(x => x.NpImp);
                    double tailKb = tailBytes / 1024.0;
                    output.WriteTail($"  + {tailCount} more tags totaling {tailKb:F1} KB NP-Imp");
                    if (td == 0 && jsonPool != null)
                    {
                        jsonPool.TailTagBreakdown = new JsonReport.TailSummary
                        {
                            Count = tailCount,
                            Bytes = tailBytes,
                            Megabytes = tailBytes / 1048576.0
                        };
                    }
                }
                output.WriteBlank();
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

            var displayed = allByDriver
                .Where(x => x.Bytes >= output.MinDisplayBytes)
                .Take(output.TopN)
                .ToList();

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

            output.WriteSubHeader($"Top {output.TopN} drivers by code resident footprint (MB){output.MinDisplaySuffix}:");
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