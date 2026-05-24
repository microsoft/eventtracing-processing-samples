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
            IPendingResult<IResidentSetDataSource> pendingResidentSet)
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

            foreach (IResidentSetSnapshot snapshot in rsd.Snapshots)
            {
                System.DateTimeOffset wallClock = metadata.GetWallClock(snapshot.Timestamp);
                output.WriteSubHeader($"Snapshot @ {wallClock} (trace-relative {snapshot.Timestamp.TotalSeconds:F3}s) - {snapshot.Pages.Count} pages");
                Log.Info($"Exercise1: snapshot @ {wallClock:o} pages={snapshot.Pages.Count}");

                WriteMmListSummary(output, snapshot.Pages);
                output.WriteBlank();
                WriteTopProcessesByActive(output, snapshot.Pages);
                output.WriteBlank();
                WriteDriverLockedNonPaged(output, snapshot.Pages);
            }
        }

        /// <summary>
        /// Emits the per-MMList totals (Active / Standby / Modified / Zeroed /
        /// Free / Bad) in MB, sorted descending so the largest list is
        /// rendered with the most prominent color. This shows the
        /// system-wide memory pressure picture at a glance.
        /// </summary>
        private static void WriteMmListSummary(OutputWriter output, IReadOnlyList<IResidentSetPage> pages)
        {
            output.WriteSubHeader("MMList totals (MB):");
            var byList = pages.GroupBy(p => p.MemoryManagerListType)
                              .Select(g => new { List = g.Key, Mb = (g.Count() * PageSizeBytes) / 1024.0 / 1024.0 })
                              .OrderByDescending(x => x.Mb)
                              .ToList();

            int rank = 0;
            foreach (var row in byList)
            {
                rank++;
                output.WriteRanked(rank, byList.Count, $"  {row.List,-16} {row.Mb,10:F2} MB");
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
        private static void WriteTopProcessesByActive(OutputWriter output, IReadOnlyList<IResidentSetPage> pages)
        {
            output.WriteSubHeader($"Top {output.TopN} processes by Active working set (MB):");

            var allActiveByProcess = pages
                .Where(p => p.MemoryManagerListType == MemoryManagerListType.Active && p.Process != null)
                .GroupBy(p => p.Process)
                .Select(g => new
                {
                    Process = g.Key,
                    TotalMb = (g.Count() * PageSizeBytes) / 1024.0 / 1024.0,
                    ByCategory = g.GroupBy(x => x.Category)
                                  .Select(cg => new { Cat = cg.Key, Mb = (cg.Count() * PageSizeBytes) / 1024.0 / 1024.0 })
                                  .OrderByDescending(x => x.Mb)
                                  .ToList()
                })
                .OrderByDescending(x => x.TotalMb)
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
                string line = $"  {ImageFormatter.FormatProcess(row.Process)}  {row.TotalMb,10:F2} MB";
                output.WriteRanked(rank, topProcesses.Count, line);
                foreach (var cat in row.ByCategory.Take(output.TopK))
                {
                    output.WriteNormal($"      {cat.Cat,-28} {cat.Mb,8:F2} MB");
                }
            }

            // Tail summary
            if (allActiveByProcess.Count > topProcesses.Count)
            {
                int tailCount = allActiveByProcess.Count - topProcesses.Count;
                double tailMb = allActiveByProcess.Skip(topProcesses.Count).Sum(x => x.TotalMb);
                output.WriteTail($"  + {tailCount} more processes totaling {tailMb:F2} MB");
            }
        }

        /// <summary>
        /// Emits the Top N <see cref="ResidentSetPageCategory.DriverLockedSystemPage"/>
        /// contributors grouped by image path. Pages in this category are
        /// non-paged pool that a kernel-mode driver has locked in physical
        /// RAM. A driver that dominates this list is a classic candidate
        /// for a memory-footprint regression bug.
        /// </summary>
        private static void WriteDriverLockedNonPaged(OutputWriter output, IReadOnlyList<IResidentSetPage> pages)
        {
            output.WriteSubHeader($"Top {output.TopN} driver-locked non-paged contributors (MB):");

            var allByDriver = pages
                .Where(p => p.Category == ResidentSetPageCategory.DriverLockedSystemPage)
                .GroupBy(p => p.Path ?? p.Tag ?? "(unknown)")
                .Select(g => new { Path = g.Key, Mb = (g.Count() * PageSizeBytes) / 1024.0 / 1024.0 })
                .OrderByDescending(x => x.Mb)
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
                output.WriteRanked(rank, topDrivers.Count, $"  {row.Mb,10:F2} MB  {row.Path}");
            }

            // Tail summary
            if (allByDriver.Count > topDrivers.Count)
            {
                int tailCount = allByDriver.Count - topDrivers.Count;
                double tailMb = allByDriver.Skip(topDrivers.Count).Sum(x => x.Mb);
                output.WriteTail($"  + {tailCount} more entries totaling {tailMb:F2} MB");
            }
        }
    }
}
