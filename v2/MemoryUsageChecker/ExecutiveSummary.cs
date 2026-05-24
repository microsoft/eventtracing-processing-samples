// © Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Renders a short, scannable digest of the most actionable findings
    /// across all three exercises. Produced AFTER the three exercise
    /// analyzers have populated <see cref="JsonReport"/>, so this class
    /// reads exclusively from the populated report — no re-walks of the
    /// raw ETL. Emits the same content twice:
    /// <list type="bullet">
    ///   <item>Appended to the main result file via <see cref="OutputWriter"/>
    ///         under a <c>=== EXECUTIVE SUMMARY ===</c> banner so the reader
    ///         can jump to the punchline with a single search.</item>
    ///   <item>Written to a tiny companion <c>MemoryUsage_Summary_*.txt</c>
    ///         file with no color codes, intended to be pasted directly
    ///         into bug reports.</item>
    /// </list>
    /// Keep this class read-only with respect to the report data: any new
    /// finding logic must come from <see cref="JsonReport"/> fields that
    /// already exist, so a stale capture re-rendered with a newer build
    /// still produces a stable summary.
    /// </summary>
    internal static class ExecutiveSummary
    {
        /// <summary>
        /// Renders the executive summary to <paramref name="output"/> AND
        /// writes a plain-text copy to <paramref name="summaryFilePath"/>.
        /// Both renderings share the same line-construction code; the only
        /// difference is the destination (mirrored color file vs plain text).
        /// </summary>
        public static void WriteToOutputAndFile(OutputWriter output, JsonReport report, string summaryFilePath)
        {
            List<string> lines = BuildSummaryLines(report);

            // Mirror to the main report so the user only has to scroll to the
            // end (or search for "EXECUTIVE SUMMARY") to find the digest.
            output.WriteHeader("=== EXECUTIVE SUMMARY ===");
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line)) { output.WriteBlank(); continue; }
                if (line.StartsWith("--- ")) { output.WriteSubHeader(line); continue; }
                if (line.StartsWith("  >> ")) { output.WriteCritical(line); continue; }
                if (line.StartsWith("  RECOMMENDATION")) { output.WriteNotable(line); continue; }
                output.WriteNormal(line);
            }

            // Plain-text companion file. Failures here are non-fatal — the
            // executive summary is also present in the main result file.
            try
            {
                File.WriteAllLines(summaryFilePath, lines);
                output.WriteInfo($"Executive summary written: {summaryFilePath}");
            }
            catch (Exception ex)
            {
                Log.Error("Failed to write executive summary companion file", ex);
                output.WriteNotable($"Warning: failed to write executive summary file ({ex.Message}). Main report is unaffected.");
            }
        }

        /// <summary>
        /// Builds the executive summary as a list of pre-formatted lines.
        /// The structure is intentionally fixed so consumers (and tests)
        /// can grep for stable prefixes:
        /// <list type="bullet">
        ///   <item><c>--- Exercise N: …</c> sub-section headers</item>
        ///   <item><c>  &gt;&gt; </c> for "worst offender" highlight lines</item>
        ///   <item><c>  RECOMMENDATION: …</c> for the single actionable
        ///         next step per exercise</item>
        ///   <item>Empty string for blank separator lines</item>
        /// </list>
        /// </summary>
        private static List<string> BuildSummaryLines(JsonReport report)
        {
            List<string> lines = new List<string>();

            lines.Add($"Trace: {report.Trace?.Path ?? "(unknown)"}");
            if (report.Trace?.StartTimeUtc != null && report.Trace?.StopTimeUtc != null)
            {
                double durationSec = (report.Trace.StopTimeUtc.Value - report.Trace.StartTimeUtc.Value).TotalSeconds;
                lines.Add($"Duration: {durationSec:F1}s   OS: {report.Trace.OsSummary ?? "(unknown)"}");
            }
            lines.Add(string.Empty);

            AddExercise1(lines, report.Exercise1ResidentSet);
            lines.Add(string.Empty);
            AddExercise2(lines, report.Exercise2VirtualAllocHeap);
            lines.Add(string.Empty);
            AddExercise3(lines, report.Exercise3Pool);

            lines.Add(string.Empty);
            lines.Add("(Full per-process/per-driver detail is in the main result file above this section.)");

            return lines;
        }

        private static void AddExercise1(List<string> lines, JsonReport.Exercise1Section section)
        {
            lines.Add("--- Exercise 1: Resident Set ---");
            if (section == null || section.Snapshots.Count == 0)
            {
                lines.Add("  (no resident-set snapshots in trace — exercise skipped)");
                return;
            }

            // Pick the snapshot with per-process Active rows (the primary one).
            JsonReport.Exercise1Snapshot snap = section.Snapshots
                .FirstOrDefault(s => s.TopProcessesByActiveWorkingSet != null && s.TopProcessesByActiveWorkingSet.Count > 0)
                ?? section.Snapshots[0];

            // System-wide MMList totals
            JsonReport.MmListBucket active = snap.MmListTotalsMegabytes?.FirstOrDefault(b => string.Equals(b.List, "Active", StringComparison.OrdinalIgnoreCase));
            JsonReport.MmListBucket standby = snap.MmListTotalsMegabytes?.FirstOrDefault(b => string.Equals(b.List, "Standby", StringComparison.OrdinalIgnoreCase));
            if (active != null) lines.Add($"  System Active working set : {active.Megabytes,9:F2} MB");
            if (standby != null) lines.Add($"  System Standby            : {standby.Megabytes,9:F2} MB");

            // Worst process by Active working set
            JsonReport.RankedProcessActive worst = snap.TopProcessesByActiveWorkingSet?.FirstOrDefault();
            if (worst != null)
            {
                string procName = FormatProcessName(worst.Process);
                string dominantCategory = worst.ByCategoryMegabytes?.FirstOrDefault()?.Category;
                double dominantMb = worst.ByCategoryMegabytes?.FirstOrDefault()?.Megabytes ?? 0;
                lines.Add($"  >> Worst process: {procName} — {worst.TotalMegabytes:F2} MB Active"
                    + (dominantCategory != null ? $" (dominated by {dominantCategory} at {dominantMb:F2} MB)" : string.Empty));
                if (dominantCategory != null)
                {
                    lines.Add($"  RECOMMENDATION: Investigate {procName}'s {dominantCategory} usage in WPA (Memory Utilization or Heap graph).");
                }
            }

            // Worst driver-locked contributor
            JsonReport.RankedDriverLocked worstDriver = snap.TopDriverLockedNonPaged?.FirstOrDefault();
            if (worstDriver != null && !string.Equals(worstDriver.Identifier, "(unknown)", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"  >> Worst driver-locked: {LeafName(worstDriver.Identifier)} — {worstDriver.Megabytes:F2} MB locked");
            }
        }

        private static void AddExercise2(List<string> lines, JsonReport.Exercise2Section section)
        {
            lines.Add("--- Exercise 2: VirtualAlloc + Heap ---");
            if (section == null)
            {
                lines.Add("  (commit + heap data sources unavailable — exercise skipped)");
                return;
            }

            JsonReport.RankedProcessVirtualAlloc worstVa = section.VirtualAlloc?.TopProcessesByImpactingBytes?.FirstOrDefault();
            if (worstVa != null)
            {
                string procName = FormatProcessName(worstVa.Process);
                double impactingMb = worstVa.ImpactingBytes / 1048576.0;
                double transientMb = worstVa.TransientBytes / 1048576.0;
                lines.Add($"  >> Worst VirtualAlloc commit: {procName} — Impacting {impactingMb:F2} MB, Transient {transientMb:F2} MB");
                JsonReport.RankedStack worstStack = worstVa.TopImpactingStacks?.FirstOrDefault();
                if (worstStack != null && worstStack.Frames != null && worstStack.Frames.Count > 0)
                {
                    // Pick the first frame whose symbol resolved. When the whole
                    // stack is unresolved (typical with --no-symbols) we
                    // intentionally suppress the address and tell the user to
                    // re-run with symbols rather than printing a useless RVA.
                    string topFrame = worstStack.Frames.Take(8).FirstOrDefault(f => !string.IsNullOrEmpty(f) && !f.Contains("[no symbols]"));
                    double stackMb = worstStack.TotalBytes / 1048576.0;
                    if (topFrame != null)
                    {
                        lines.Add($"  RECOMMENDATION: Focus on the top commit stack in {procName} (rank-1 stack: {Shorten(topFrame, 80)}, {stackMb:F2} MB across {worstStack.AllocationCount} allocs).");
                    }
                    else
                    {
                        lines.Add($"  RECOMMENDATION: The rank-1 commit stack in {procName} holds {stackMb:F2} MB across {worstStack.AllocationCount} allocs but its frames are unresolved — re-run without --no-symbols (and ensure _NT_SYMBOL_PATH is set) to identify the responsible call site.");
                    }
                }
            }
            else
            {
                lines.Add("  (no VirtualAlloc commit lifetimes ranked above threshold)");
            }

            JsonReport.RankedProcessHeap worstHeap = section.Heap?.TopProcessesByOutstandingBytes?.FirstOrDefault();
            if (worstHeap != null)
            {
                string procName = FormatProcessName(worstHeap.Process);
                lines.Add($"  >> Worst Win32 heap: {procName} — {worstHeap.OutstandingBytes / 1048576.0:F2} MB outstanding across {worstHeap.HeapCount} heap handle(s)");
            }
        }

        private static void AddExercise3(List<string> lines, JsonReport.Exercise3Section section)
        {
            lines.Add("--- Exercise 3: Pool + Driver Code Footprint ---");
            if (section == null)
            {
                lines.Add("  (pool data source unavailable — exercise skipped)");
                return;
            }

            JsonReport.RankedDriverPool worstDriver = section.PoolAllocations?.TopDriversByNonPagedImpactingBytes?.FirstOrDefault();
            if (worstDriver != null)
            {
                string driverName = worstDriver.Driver?.FileName ?? "(unknown driver)";
                double npImpactingMb = worstDriver.NonPagedImpactingBytes / 1048576.0;
                double pgImpactingMb = worstDriver.PagedImpactingBytes / 1048576.0;
                lines.Add($"  >> Worst NonPaged pool: {driverName} — NP-Impacting {npImpactingMb:F2} MB, Paged-Impacting {pgImpactingMb:F2} MB ({worstDriver.AllocationCount:N0} allocs)");

                JsonReport.RankedTag dominantTag = section.PoolAllocations.TopDriverTagBreakdown?.FirstOrDefault();
                if (dominantTag != null && worstDriver.NonPagedImpactingBytes > 0)
                {
                    double tagShare = 100.0 * dominantTag.NonPagedImpactingBytes / worstDriver.NonPagedImpactingBytes;
                    double tagMb = dominantTag.NonPagedImpactingBytes / 1048576.0;
                    lines.Add($"     Dominant pool tag in {driverName}: '{dominantTag.Tag}' = {tagMb:F2} MB ({tagShare:F1}% of this driver's NP-Impacting)");
                    lines.Add($"  RECOMMENDATION: Investigate pool tag '{dominantTag.Tag}' allocations in {driverName} (use poolmon / WPA Pool graph).");
                }
                else
                {
                    JsonReport.RankedStack worstStack = worstDriver.TopImpactingStacks?.FirstOrDefault();
                    if (worstStack != null)
                    {
                        lines.Add($"  RECOMMENDATION: Investigate {driverName} pool alloc stack #1 ({worstStack.TotalBytes / 1048576.0:F2} MB, {worstStack.AllocationCount} alloc(s)).");
                    }
                }
            }
            else
            {
                lines.Add("  (no drivers ranked above the NonPaged threshold)");
            }

            // Driver code footprint
            JsonReport.RankedDriverFootprint worstCode = section.DriverCodeFootprint?.TopDriversByResidentBytes?.FirstOrDefault();
            if (worstCode != null)
            {
                string codeName = worstCode.Driver?.FileName ?? "(unknown driver)";
                lines.Add($"  >> Largest driver code footprint: {codeName} — {worstCode.ResidentBytes / 1048576.0:F2} MB resident");
            }
        }

        private static string FormatProcessName(JsonReport.ProcessIdentity p)
        {
            if (p == null) return "(unknown)";
            string name = p.ImageName ?? p.FriendlyName ?? "(unknown)";
            return $"{name} (pid {p.Pid})";
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "(unknown)";
            try { return Path.GetFileName(path) ?? path; }
            catch { return path; }
        }

        private static string Shorten(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            return s.Substring(0, maxLen - 1) + "…";
        }
    }
}
