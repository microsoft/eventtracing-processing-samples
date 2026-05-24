// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using System.Collections.Generic;
using System.Linq;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Implements the WPT "Memory Footprint Optimization — Exercise 1"
    /// analysis. Walks each <see cref="IResidentSetSnapshot"/> and emits:
    /// (1) the MMList totals (Active / Standby / Modified / etc.) in MB,
    /// (2) the top processes by Active resident pages, and
    /// (3) the top kernel images by NonPaged (locked) pages — a common
    /// driver-leak signature.
    /// </summary>
    /// <remarks>
    /// Requires the <c>ResidentSet</c> data source in the ETL (captured by
    /// the <c>ReferenceSet</c> profile in <c>MemoryUsageChecker.wprp</c>).
    /// If absent, the exercise prints a single <c>[skipped]</c> line and
    /// returns without raising.
    /// </remarks>
    internal static class Exercise1_ResidentSet
    {
        private const long PageSizeBytes = 4096;

        /// <summary>
        /// Runs Exercise 1. See class summary for the analysis the method performs.
        /// </summary>
        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<IResidentSetDataSource> pendingResidentSet,
            JsonReport jsonReport = null)
        {
            output.WriteHeader("=== Exercise 1: Resident Set ===");
            Log.Info($"Exercise1: pendingResidentSet.HasResult={pendingResidentSet.HasResult}, snapshots={(pendingResidentSet.HasResult ? pendingResidentSet.Result.Snapshots.Count : 0)}");

            if (!pendingResidentSet.HasResult || pendingResidentSet.Result.Snapshots.Count == 0)
            {
                output.WriteSkipped("[skipped - resident-set data not present in trace]");
                Log.Info("Exercise1: skipped because resident-set data missing");
                return;
            }

            IResidentSetDataSource rsd = pendingResidentSet.Result;

            JsonReport.Exercise1Section jsonSection = null;
            if (jsonReport != null)
            {
                jsonSection = new JsonReport.Exercise1Section();
                jsonReport.Exercise1ResidentSet = jsonSection;
            }

            foreach (IResidentSetSnapshot snapshot in rsd.Snapshots)
            {
                System.DateTimeOffset wallClock = metadata.GetWallClock(snapshot.Timestamp);
                output.WriteSubHeader($"Snapshot @ {wallClock} (trace-relative {snapshot.Timestamp.TotalSeconds:F3}s) - {snapshot.Pages.Count} pages");
                Log.Info($"Exercise1: snapshot @ {wallClock:o} pages={snapshot.Pages.Count}");

                JsonReport.Exercise1Snapshot jsonSnapshot = null;
                if (jsonSection != null)
                {
                    jsonSnapshot = new JsonReport.Exercise1Snapshot
                    {
                        TimestampUtc = wallClock.UtcDateTime,
                        TraceRelativeSeconds = (double)snapshot.Timestamp.TotalSeconds,
                        PageCount = snapshot.Pages.Count
                    };
                    jsonSection.Snapshots.Add(jsonSnapshot);
                }

                WriteMmListSummary(output, snapshot.Pages, jsonSnapshot);
                output.WriteBlank();
                WriteTopProcessesByActive(output, snapshot.Pages, jsonSnapshot);
                output.WriteBlank();
                WriteDriverLockedNonPaged(output, snapshot.Pages, jsonSnapshot);
            }
        }

        /// <summary>
        /// Emits the per-MMList totals (Active / Standby / Modified / Zeroed /
        /// Free / Bad) in MB, sorted descending so the largest list is
        /// rendered with the most prominent color. This shows the
        /// system-wide memory pressure picture at a glance.
        /// </summary>
        private static void WriteMmListSummary(OutputWriter output, IReadOnlyList<IResidentSetPage> pages, JsonReport.Exercise1Snapshot jsonSnapshot)
        {
            output.WriteSubHeader("MMList totals (MB):");
            var byList = pages.GroupBy(p => p.MemoryManagerListType)
                              .Select(g => new { List = g.Key, Bytes = (long)g.Count() * PageSizeBytes })
                              .OrderByDescending(x => x.Bytes)
                              .ToList();

            int rank = 0;
            foreach (var row in byList)
            {
                rank++;
                double mb = row.Bytes / 1024.0 / 1024.0;
                output.WriteRanked(rank, byList.Count, $"  {row.List,-16} {mb,10:F2} MB");
                if (jsonSnapshot != null)
                {
                    jsonSnapshot.MmListTotalsMegabytes.Add(new JsonReport.MmListBucket
                    {
                        Rank = rank,
                        List = row.List.ToString(),
                        Bytes = row.Bytes,
                        Megabytes = mb
                    });
                }
            }
        }

        /// <summary>
        /// Emits the Top N processes by Active resident pages (i.e. RAM the
        /// process is actively using). For each process, also breaks the
        /// total down by <see cref="ResidentSetPageCategory"/> so the reader
        /// can see whether the cost is image (code), private (heap/stack),
        /// session, mapped file, etc. The summary ends with a <c>"+ N more"</c>
        /// tail line so the truncation is honest.
        /// </summary>
        private static void WriteTopProcessesByActive(OutputWriter output, IReadOnlyList<IResidentSetPage> pages, JsonReport.Exercise1Snapshot jsonSnapshot)
        {
            output.WriteSubHeader($"Top {output.TopN} processes by Active working set (MB):");

            var allActiveByProcess = pages
                .Where(p => p.MemoryManagerListType == MemoryManagerListType.Active && p.Process != null)
                .GroupBy(p => p.Process)
                .Select(g => new
                {
                    Process = g.Key,
                    TotalBytes = (long)g.Count() * PageSizeBytes,
                    ByCategory = g.GroupBy(x => x.Category)
                                  .Select(cg => new { Cat = cg.Key, Bytes = (long)cg.Count() * PageSizeBytes })
                                  .OrderByDescending(x => x.Bytes)
                                  .ToList()
                })
                .OrderByDescending(x => x.TotalBytes)
                .ToList();

            if (allActiveByProcess.Count == 0)
            {
                output.WriteSkipped("  (no per-process Active pages in this snapshot)");
                return;
            }

            var topProcesses = allActiveByProcess.Take(output.TopN).ToList();

            int rank = 0;
            foreach (var row in topProcesses)
            {
                rank++;
                double totalMb = row.TotalBytes / 1024.0 / 1024.0;
                string line = $"  {ImageFormatter.FormatProcess(row.Process)}  {totalMb,10:F2} MB";
                output.WriteRanked(rank, topProcesses.Count, line);

                JsonReport.RankedProcessActive jsonRow = null;
                if (jsonSnapshot != null)
                {
                    jsonRow = new JsonReport.RankedProcessActive
                    {
                        Rank = rank,
                        Process = ImageFormatter.BuildProcessIdentity(row.Process),
                        TotalBytes = row.TotalBytes,
                        TotalMegabytes = totalMb
                    };
                    jsonSnapshot.TopProcessesByActiveWorkingSet.Add(jsonRow);
                }

                foreach (var cat in row.ByCategory.Where(c => c.Bytes >= output.MinDisplayBytes).Take(output.TopK))
                {
                    double catMb = cat.Bytes / 1024.0 / 1024.0;
                    output.WriteNormal($"      {cat.Cat,-28} {catMb,8:F2} MB");
                    jsonRow?.ByCategoryMegabytes.Add(new JsonReport.CategoryMb
                    {
                        Category = cat.Cat.ToString(),
                        Bytes = cat.Bytes,
                        Megabytes = catMb
                    });
                }
            }

            // Tail summary
            if (allActiveByProcess.Count > topProcesses.Count)
            {
                int tailCount = allActiveByProcess.Count - topProcesses.Count;
                long tailBytes = allActiveByProcess.Skip(topProcesses.Count).Sum(x => x.TotalBytes);
                double tailMb = tailBytes / 1024.0 / 1024.0;
                output.WriteTail($"  + {tailCount} more processes totaling {tailMb:F2} MB");
                if (jsonSnapshot != null)
                {
                    jsonSnapshot.TailProcesses = new JsonReport.TailSummary
                    {
                        Count = tailCount,
                        Bytes = tailBytes,
                        Megabytes = tailMb
                    };
                }
            }
        }

        /// <summary>
        /// Emits the Top N <see cref="ResidentSetPageCategory.DriverLockedSystemPage"/>
        /// contributors grouped by image path. Pages in this category are
        /// non-paged pool that a kernel-mode driver has locked in physical
        /// RAM. A driver that dominates this list is a classic candidate
        /// for a memory-footprint regression bug.
        /// </summary>
        private static void WriteDriverLockedNonPaged(OutputWriter output, IReadOnlyList<IResidentSetPage> pages, JsonReport.Exercise1Snapshot jsonSnapshot)
        {
            output.WriteSubHeader($"Top {output.TopN} driver-locked non-paged contributors (MB):");

            var allByDriver = pages
                .Where(p => p.Category == ResidentSetPageCategory.DriverLockedSystemPage)
                .GroupBy(p => p.Path ?? p.Tag ?? "(unknown)")
                .Select(g => new { Path = g.Key, Bytes = (long)g.Count() * PageSizeBytes })
                .OrderByDescending(x => x.Bytes)
                .ToList();

            if (allByDriver.Count == 0)
            {
                output.WriteFinding("  (no DriverLockedSystemPage entries found)");
                return;
            }

            var topDrivers = allByDriver.Take(output.TopN).ToList();

            int rank = 0;
            foreach (var row in topDrivers)
            {
                rank++;
                double mb = row.Bytes / 1024.0 / 1024.0;
                output.WriteRanked(rank, topDrivers.Count, $"  {mb,10:F2} MB  {row.Path}");
                if (jsonSnapshot != null)
                {
                    jsonSnapshot.TopDriverLockedNonPaged.Add(new JsonReport.RankedDriverLocked
                    {
                        Rank = rank,
                        Identifier = row.Path,
                        Bytes = row.Bytes,
                        Megabytes = mb
                    });
                }
            }

            // Tail summary
            if (allByDriver.Count > topDrivers.Count)
            {
                int tailCount = allByDriver.Count - topDrivers.Count;
                long tailBytes = allByDriver.Skip(topDrivers.Count).Sum(x => x.Bytes);
                double tailMb = tailBytes / 1024.0 / 1024.0;
                output.WriteTail($"  + {tailCount} more entries totaling {tailMb:F2} MB");
                if (jsonSnapshot != null)
                {
                    jsonSnapshot.TailDriverLockedNonPaged = new JsonReport.TailSummary
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
