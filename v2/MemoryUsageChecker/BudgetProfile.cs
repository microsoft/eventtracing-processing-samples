// © Microsoft Corporation. All rights reserved.

using System;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Memory-budget tier used to evaluate whether an OEM image fits a
    /// target Windows device class (8 GB or 4 GB total physical RAM).
    /// Holds the per-item and per-category total budgets surfaced by the
    /// analyzers in Exercises 1-3, plus the default Top-N truncation
    /// numbers calibrated against the natural "knee" of real OEM traces
    /// (see <c>plan.md</c> for the data backing each number).
    /// </summary>
    /// <remarks>
    /// <para>
    /// All budget fields are expressed in <b>bytes</b> so callers can
    /// compare directly against the SDK's raw <c>Bytes</c> properties
    /// without rounding drift. Use the static factories
    /// <see cref="EightGb"/> / <see cref="FourGb"/> to get the canonical
    /// presets, then override individual fields via <c>with { … }</c> when
    /// a command-line flag was specified.
    /// </para>
    /// <para>
    /// <b>Why these numbers?</b> They are derived from the trace shipped
    /// alongside this change (24H2, ~7 GB Active working set, OEM-like
    /// preload). The cumulative-coverage curves had a clear knee at
    /// rank-15 for processes and rank-10 for drivers, and only ~17
    /// drivers in the whole trace ever exceeded 2 MB of NonPaged-pool
    /// usage. The per-item budgets are calibrated so the worst offenders
    /// in the trace (MsMpEng 226 MB WS, Wdf01000 171 MB NP-pool) light
    /// up red on the default 8 GB tier — which is the actionable signal
    /// OEMs need to ship a clean image.
    /// </para>
    /// </remarks>
    internal sealed record BudgetProfile
    {
        private const long MiB = 1024L * 1024L;
        private const long GiB = 1024L * MiB;

        /// <summary>
        /// Default verdict threshold (in percent of the budget) above
        /// which a row or category total is painted red and marked
        /// <c>Fail</c>. Overridable from <c>MemoryUsageChecker.profiles.json</c>.
        /// </summary>
        public const int DefaultFailAtPercent = 100;

        /// <summary>
        /// Default verdict threshold (in percent of the budget) at or
        /// above which a row or category total is painted yellow and
        /// marked <c>Warn</c>. Overridable from
        /// <c>MemoryUsageChecker.profiles.json</c>.
        /// </summary>
        public const int DefaultWarnAtPercent = 80;

        /// <summary>
        /// Effective <c>Fail</c> threshold (percent of budget) used by
        /// <see cref="Evaluate"/>. Set once at startup from the
        /// profiles JSON; falls back to <see cref="DefaultFailAtPercent"/>
        /// when the JSON omits the value.
        /// </summary>
        public static int FailAtPercent { get; set; } = DefaultFailAtPercent;

        /// <summary>
        /// Effective <c>Warn</c> threshold (percent of budget) used by
        /// <see cref="Evaluate"/>. Set once at startup from the
        /// profiles JSON; falls back to <see cref="DefaultWarnAtPercent"/>
        /// when the JSON omits the value.
        /// </summary>
        public static int WarnAtPercent { get; set; } = DefaultWarnAtPercent;

        /// <summary>
        /// Friendly tier name surfaced in logs, console banners, and the
        /// JSON sidecar. One of <c>"16gb"</c>, <c>"8gb"</c>, <c>"4gb"</c>,
        /// or <c>"custom"</c> when the user has overridden any preset value.
        /// </summary>
        public string Name { get; init; } = "8gb";

        /// <summary>Top-N user-mode processes shown in each ranked table.</summary>
        public int TopProcesses { get; init; } = 15;

        /// <summary>Top-N drivers shown in each ranked table.</summary>
        public int TopDrivers { get; init; } = 10;

        /// <summary>Per-process Active working-set ceiling (Exercise 1).</summary>
        public long PerProcessWorkingSetBudgetBytes { get; init; } = 200 * MiB;

        /// <summary>Per-process VirtualAlloc Impacting ceiling (Exercise 2).</summary>
        public long PerProcessVirtualAllocBudgetBytes { get; init; } = 100 * MiB;

        /// <summary>Per-driver NonPaged-pool Impacting ceiling (Exercise 3 Part A).</summary>
        public long PerDriverPoolBudgetBytes { get; init; } = 5 * MiB;

        /// <summary>Per-driver code-resident footprint ceiling (Exercise 3 Part B).</summary>
        public long PerDriverCodeBudgetBytes { get; init; } = 2 * MiB;

        /// <summary>
        /// Whole-image budget for the sum of all user-mode process
        /// Active working set. Compared against the named-process total
        /// (i.e. excluding kernel / driver-locked pages, which are
        /// budgeted separately under driver pool / code footprint).
        /// </summary>
        public long TotalUserWorkingSetBudgetBytes { get; init; } = (long)(1.5 * GiB);

        /// <summary>Whole-image budget for the sum of NonPaged-pool Impacting across all drivers.</summary>
        public long TotalDriverPoolBudgetBytes { get; init; } = 256 * MiB;

        /// <summary>Whole-image budget for the sum of driver code-resident footprint.</summary>
        public long TotalDriverCodeBudgetBytes { get; init; } = 64 * MiB;

        /// <summary>
        /// Row threshold (bytes) below which a row is omitted from the
        /// displayed Top-N tables. Distinct from per-item budget — this
        /// just controls visual density. Matches the existing
        /// <c>--min-display-mb</c> flag default.
        /// </summary>
        public long MinDisplayBytes { get; init; } = 2 * MiB;

        /// <summary>
        /// Returns the relaxed 16 GB tier. Targeted at higher-end OEM
        /// devices that have enough physical RAM to absorb a larger
        /// preload but still want a hard ceiling so the image does not
        /// drift over time. All per-item and total budgets are roughly
        /// doubled vs the 8 GB tier; the row-display floor is raised to
        /// 4 MB so only meaningful contributors are listed.
        /// </summary>
        public static BudgetProfile SixteenGb() => new BudgetProfile
        {
            Name = "16gb",
            TopProcesses = 15,
            TopDrivers = 10,
            PerProcessWorkingSetBudgetBytes = 400 * MiB,
            PerProcessVirtualAllocBudgetBytes = 200 * MiB,
            PerDriverPoolBudgetBytes = 10 * MiB,
            PerDriverCodeBudgetBytes = 4 * MiB,
            TotalUserWorkingSetBudgetBytes = 3L * GiB,
            TotalDriverPoolBudgetBytes = 512 * MiB,
            TotalDriverCodeBudgetBytes = 128 * MiB,
            MinDisplayBytes = 4 * MiB,
        };

        /// <summary>
        /// Returns the canonical 8 GB tier (default). Calibrated for a
        /// device with 8 GB total RAM where the OEM image should leave at
        /// minimum ~5 GB free for the end-user's workloads.
        /// </summary>
        public static BudgetProfile EightGb() => new BudgetProfile
        {
            Name = "8gb",
            TopProcesses = 15,
            TopDrivers = 10,
            PerProcessWorkingSetBudgetBytes = 200 * MiB,
            PerProcessVirtualAllocBudgetBytes = 100 * MiB,
            PerDriverPoolBudgetBytes = 5 * MiB,
            PerDriverCodeBudgetBytes = 2 * MiB,
            TotalUserWorkingSetBudgetBytes = (long)(1.5 * GiB),
            TotalDriverPoolBudgetBytes = 256 * MiB,
            TotalDriverCodeBudgetBytes = 64 * MiB,
            MinDisplayBytes = 2 * MiB,
        };

        /// <summary>
        /// Returns the tighter 4 GB tier. All per-item and total budgets
        /// are roughly halved, and the row-display floor is dropped to
        /// 1 MB so smaller offenders surface in the table.
        /// </summary>
        public static BudgetProfile FourGb() => new BudgetProfile
        {
            Name = "4gb",
            TopProcesses = 15,
            TopDrivers = 10,
            PerProcessWorkingSetBudgetBytes = 100 * MiB,
            PerProcessVirtualAllocBudgetBytes = 50 * MiB,
            PerDriverPoolBudgetBytes = 2 * MiB,
            PerDriverCodeBudgetBytes = 1 * MiB,
            TotalUserWorkingSetBudgetBytes = 750 * MiB,
            TotalDriverPoolBudgetBytes = 128 * MiB,
            TotalDriverCodeBudgetBytes = 32 * MiB,
            MinDisplayBytes = 1 * MiB,
        };

        /// <summary>
        /// Resolves a preset by name (case-insensitive). Returns
        /// <c>null</c> when <paramref name="name"/> is not a known
        /// preset so the caller can emit a usage error.
        /// </summary>
        public static BudgetProfile TryFromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return name.Trim().ToLowerInvariant() switch
            {
                "16gb" or "16" => SixteenGb(),
                "8gb" or "8" => EightGb(),
                "4gb" or "4" => FourGb(),
                _ => null,
            };
        }

        /// <summary>
        /// Verdict tier for one comparison of <paramref name="actualBytes"/>
        /// against <paramref name="budgetBytes"/>. The boundaries are
        /// taken from <see cref="FailAtPercent"/> / <see cref="WarnAtPercent"/>
        /// (settable at startup from <c>MemoryUsageChecker.profiles.json</c>)
        /// so per-row coloring AND per-category PASS/FAIL totals always agree.
        /// </summary>
        public static BudgetVerdict Evaluate(long actualBytes, long budgetBytes)
        {
            if (budgetBytes <= 0) return BudgetVerdict.NotApplicable;
            long failAt = (long)(budgetBytes * (FailAtPercent / 100.0));
            long warnAt = (long)(budgetBytes * (WarnAtPercent / 100.0));
            if (actualBytes > failAt) return BudgetVerdict.Fail;
            if (actualBytes >= warnAt) return BudgetVerdict.Warn;
            return BudgetVerdict.Pass;
        }
    }

    /// <summary>
    /// Outcome of comparing a measured value against a budget. Picked
    /// deliberately small (4 buckets) so console color logic in
    /// <see cref="OutputWriter"/> stays a switch with no dead branches.
    /// </summary>
    internal enum BudgetVerdict
    {
        /// <summary>No budget configured (budget &lt;= 0). Caller should fall back to rank-tier color.</summary>
        NotApplicable = 0,

        /// <summary>Value &lt; 80 % of budget. Healthy — paint green / "✓".</summary>
        Pass = 1,

        /// <summary>Value in 80–100 % of budget. Watch — paint yellow / "!".</summary>
        Warn = 2,

        /// <summary>Value &gt; 100 % of budget. Must shrink — paint red / "✗".</summary>
        Fail = 3,
    }
}
