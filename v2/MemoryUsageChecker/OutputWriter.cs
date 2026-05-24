// © Microsoft Corporation. All rights reserved.

using System;
using System.IO;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Color-coded console writer that simultaneously mirrors every line to a
    /// timestamped result file. Each public <c>Write*</c> method paints the
    /// console output in the color that matches the line's role, then writes
    /// the same line (without color) to the result file so the output can be
    /// pasted directly into a bug report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Severity convention</b> used by the analyzers (Exercises 1-3):
    /// </para>
    /// <list type="bullet">
    ///   <item><c>WriteHeader</c>           — Yellow.    Top-level section banners.</item>
    ///   <item><c>WriteSubHeader</c>        — Cyan.      Sub-sections and per-group titles.</item>
    ///   <item><c>WriteData</c>             — Blue.      Per-list labels (e.g. "Impacting:" before a ranked list).</item>
    ///   <item><c>WriteStackFrame</c>       — DarkGray.  Individual stack frames (one per line).</item>
    ///   <item><c>WriteFinding</c>          — Green.     Healthy findings.</item>
    ///   <item><c>WriteNotable</c>          — Magenta.   Informational warnings that don't fail the run.</item>
    ///   <item><c>WriteSkipped</c>          — DarkGray.  "[skipped - data missing]" lines.</item>
    ///   <item><c>WriteWarning</c>          — Red.       Hard failures.</item>
    ///   <item><c>WriteInfo</c>             — White.     Neutral info (symbol source, etc.).</item>
    /// </list>
    /// <para>
    /// <b>Size-ranked findings</b> use the <c>WriteCritical</c> / <c>WriteHigh</c> /
    /// <c>WriteNormal</c> / <c>WriteTail</c> family, or — more commonly — the
    /// convenience <see cref="WriteRanked"/> helper which selects the right
    /// color automatically based on rank.
    /// </para>
    /// <para>
    /// <b>Improvement-direction coloring</b> (added for budget-aware analysis):
    /// when a row can be evaluated against a per-item budget, prefer
    /// <see cref="WriteRowAgainstBudget"/> which paints the row by
    /// <see cref="BudgetVerdict"/> instead of rank tier. The goal is that an
    /// OEM tester can scan the report and answer "what do I have to shrink
    /// to ship this image in N GB?" without reading numbers:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>WriteOverBudget</c>  — Red    + <c>"✗ "</c>. Row exceeds its budget; must be cut to fit the tier.</item>
    ///   <item><c>WriteNearBudget</c>  — Yellow + <c>"! "</c>. Row is in 80–100 % of its budget; watch / reduce if possible.</item>
    ///   <item><c>WriteUnderBudget</c> — Green  + <c>"✓ "</c>. Row is healthy — well under its budget.</item>
    ///   <item><c>WriteVerdictPass</c> — Green  + <c>"✓ PASS"</c>. Category total fits the tier.</item>
    ///   <item><c>WriteVerdictFail</c> — Red    + <c>"✗ FAIL"</c>. Category total exceeds the tier; report names the gap.</item>
    /// </list>
    /// </remarks>
    internal sealed class OutputWriter : IDisposable
    {
        private readonly StreamWriter _file;

        /// <summary>
        /// Creates a writer that mirrors every line to the file at
        /// <paramref name="filePath"/>. The file is created or truncated.
        /// </summary>
        public OutputWriter(string filePath)
        {
            _file = new StreamWriter(filePath);
        }

        /// <summary>
        /// Back-compat alias retained for callers that still emit a single
        /// "Top N" header. New analyzers should prefer
        /// <see cref="TopProcesses"/> or <see cref="TopDrivers"/> because
        /// processes and drivers have very different "knee of the curve"
        /// behavior in real OEM traces (processes flatten out around
        /// rank-15, drivers around rank-10 — see <c>BudgetProfile</c>).
        /// </summary>
        public int TopN { get; init; } = 15;

        /// <summary>Maximum number of user-mode process rows displayed in each ranked table (default 15).</summary>
        public int TopProcesses { get; init; } = 15;

        /// <summary>Maximum number of driver rows displayed in each ranked table (default 10).</summary>
        public int TopDrivers { get; init; } = 10;

        /// <summary>Maximum number of rows displayed per "Top K" stack list (default 5).</summary>
        public int TopK { get; init; } = 5;

        /// <summary>
        /// Maximum number of <i>outer</i> Top-N rows that get an inner
        /// per-row stack-dump / per-tag breakdown subsection (default 10).
        /// This is independent of <see cref="TopN"/>: the outer table still
        /// shows up to <see cref="TopN"/> entries (default 30), but only the
        /// first <see cref="TopStacks"/> of them are drilled into with the
        /// verbose Top-K stack list. Keeps the report scannable when
        /// <see cref="TopN"/> is large.
        /// </summary>
        public int TopStacks { get; init; } = 10;

        /// <summary>
        /// True when the analysis is running with <c>--no-symbols</c>. The
        /// per-row stack-dump subsections in Exercises 2 and 3 honor this:
        /// when set, they replace the noisy
        /// <c>module!0xRVA [no symbols]</c> frame dumps with a single
        /// "[stack frames omitted — re-run without --no-symbols for
        /// actionable stacks]" notice. Outer Top-N tables, ranked stacks
        /// in the JSON sidecar, and the executive summary are still
        /// emitted; only the per-frame text rendering is suppressed.
        /// </summary>
        public bool NoSymbols { get; init; }

        /// <summary>
        /// Minimum size (in bytes) for a row to be displayed in the text report.
        /// Applied to BOTH the outer Top-N tables (top processes / top drivers
        /// in Exercises 1, 2, and 3) AND the inner Top-K rows (per-process
        /// bucket breakdown in Exercise 1, per-process / per-driver stack
        /// lists in Exercises 2 and 3, and the per-pool-tag breakdown in
        /// Exercise 3). Default is 2 MiB (2 × 1048576 bytes) so the noisy
        /// sub-2 MB processes / kernel-stack / page-table tail rows that
        /// dominate quiet captures are filtered out. Set to <c>0</c> via
        /// <c>--min-display-mb 0</c> to see every row regardless of size
        /// (legacy behavior). The "+ N more …" tail summary lines
        /// automatically absorb the filtered entries because the source
        /// lists are sorted descending, so the threshold only ever trims a
        /// contiguous sub-threshold suffix of the displayed list.
        /// </summary>
        public long MinDisplayBytes { get; init; } = 2L * 1024L * 1024L;

        /// <summary>
        /// Returns a parenthesised suffix like <c>" (>= 2 MB)"</c> that can be
        /// appended to outer Top-N sub-headers to make the active size filter
        /// visible to the reader. Empty string when filtering is disabled
        /// (<see cref="MinDisplayBytes"/> = 0).
        /// </summary>
        public string MinDisplaySuffix => MinDisplayBytes > 0
            ? $" (>= {MinDisplayBytes / 1048576.0:0.##} MB)"
            : string.Empty;

        /// <summary>
        /// Active budget tier (<c>8gb</c> / <c>4gb</c> / <c>custom</c>) used
        /// to color rows and emit the PASS/FAIL verdicts in the executive
        /// summary. Always non-null — callers default to
        /// <see cref="BudgetProfile.EightGb"/> when no profile is selected.
        /// </summary>
        public BudgetProfile Budget { get; init; } = BudgetProfile.EightGb();

        /// <summary>Yellow. Section banner / exercise title.</summary>
        public void WriteHeader(string message)     => Write(message, ConsoleColor.Yellow);
        /// <summary>Cyan. Sub-section header inside an exercise.</summary>
        public void WriteSubHeader(string message)  => Write(message, ConsoleColor.Cyan);
        /// <summary>Blue. Plain data / per-list label (e.g. <c>"Impacting:"</c>).</summary>
        public void WriteData(string message)       => Write(message, ConsoleColor.Blue);
        /// <summary>DarkGray. Single stack frame line.</summary>
        public void WriteStackFrame(string message) => Write(message, ConsoleColor.DarkGray);
        /// <summary>Green. Healthy finding (e.g. <c>Impacting = 0</c>).</summary>
        public void WriteFinding(string message)    => Write(message, ConsoleColor.Green);
        /// <summary>Magenta. Notable observation that did not breach a critical threshold.</summary>
        public void WriteNotable(string message)    => Write(message, ConsoleColor.Magenta);
        /// <summary>DarkGray. <c>[skipped - ...]</c> notice for a missing data source.</summary>
        public void WriteSkipped(string message)    => Write(message, ConsoleColor.DarkGray);
        /// <summary>Red. Hard failure / aborted exercise.</summary>
        public void WriteWarning(string message)    => Write(message, ConsoleColor.Red);
        /// <summary>White. Neutral informational line (e.g. resolved symbol source).</summary>
        public void WriteInfo(string message)       => Write(message, ConsoleColor.White);
        /// <summary>Writes a blank separator line to both console and result file.</summary>
        public void WriteBlank()                    => Write(string.Empty, ConsoleColor.White);

        // Size-ranked findings. Use these when emitting a list sorted descending
        // by memory size, so the worst offender is most visually prominent.
        // Convention: rank 1 -> WriteCritical, top tier (~third) -> WriteHigh,
        // remaining top-N -> WriteNormal, truncated tail -> WriteTail.

        /// <summary>Red. The #1 (largest) row in a ranked list, or any row that breached an absolute threshold (e.g. NonPaged pool ≥ 1 MB per driver).</summary>
        public void WriteCritical(string message)   => Write(message, ConsoleColor.Red);
        /// <summary>Yellow. The next tier (≈top third) of a ranked list.</summary>
        public void WriteHigh(string message)       => Write(message, ConsoleColor.Yellow);
        /// <summary>White. Remaining rows inside the displayed Top-N.</summary>
        public void WriteNormal(string message)     => Write(message, ConsoleColor.White);
        /// <summary>DarkGray. The <c>"+ N more ... totaling X.XX MB"</c> summary line after a truncated list.</summary>
        public void WriteTail(string message)       => Write(message, ConsoleColor.DarkGray);

        // -----------------------------------------------------------------
        // Improvement-direction (budget-aware) coloring.
        //
        // The prefix glyph is kept short and ASCII-safe so the result file
        // remains pasteable into bug reports / GitHub issues without the
        // reader needing a Unicode-aware terminal: '✗' = over budget,
        // '!' = near budget, '✓' = under budget. Console rendering uses
        // the matching Red / Yellow / Green color so a quick visual scan
        // surfaces "what do I need to shrink?".
        // -----------------------------------------------------------------

        /// <summary>Red. Row exceeds its per-item budget — must be cut for the image to fit the active tier. Prefixes "<c>✗ </c>".</summary>
        public void WriteOverBudget(string message) => Write("✗ " + message, ConsoleColor.Red);

        /// <summary>Yellow. Row is within 80–100 % of its per-item budget — watch, reduce if possible. Prefixes "<c>! </c>".</summary>
        public void WriteNearBudget(string message) => Write("! " + message, ConsoleColor.Yellow);

        /// <summary>Green. Row is well under its per-item budget — healthy. Prefixes "<c>✓ </c>".</summary>
        public void WriteUnderBudget(string message) => Write("✓ " + message, ConsoleColor.Green);

        /// <summary>Green. Aggregate "category total fits the tier budget" verdict line. Prefixes "<c>✓ PASS </c>".</summary>
        public void WriteVerdictPass(string message) => Write("✓ PASS " + message, ConsoleColor.Green);

        /// <summary>Yellow. Aggregate "category total is close to its budget" verdict line. Prefixes "<c>! WATCH </c>".</summary>
        public void WriteVerdictWatch(string message) => Write("! WATCH " + message, ConsoleColor.Yellow);

        /// <summary>Red. Aggregate "category total exceeds the tier budget" verdict line. Prefixes "<c>✗ FAIL </c>".</summary>
        public void WriteVerdictFail(string message) => Write("✗ FAIL " + message, ConsoleColor.Red);

        /// <summary>
        /// Writes a size-ranked row, coloring it by per-item budget if one
        /// is configured (<paramref name="budgetBytes"/> &gt; 0) and falling
        /// back to <see cref="WriteRanked"/> otherwise. Budget verdict
        /// always wins over rank-tier so the "what to shrink" signal is
        /// never hidden by the "this is rank #1" signal. The
        /// <c>"trim ≥ X MB"</c> suffix is appended on over-budget rows so
        /// the OEM sees the exact gap inline.
        /// </summary>
        /// <param name="rank">1-based rank in the ranked list.</param>
        /// <param name="total">Total number of rows actually displayed (after Top-N truncation).</param>
        /// <param name="bytes">Authoritative size of this row, in bytes.</param>
        /// <param name="budgetBytes">Per-item budget for this row, in bytes. Pass <c>0</c> to disable budget coloring.</param>
        /// <param name="message">Pre-formatted row text (without the verdict glyph or trim suffix).</param>
        /// <returns>The verdict that was applied (useful for callers that want to bump per-category counters).</returns>
        public BudgetVerdict WriteRowAgainstBudget(int rank, int total, long bytes, long budgetBytes, string message)
        {
            BudgetVerdict verdict = BudgetProfile.Evaluate(bytes, budgetBytes);
            switch (verdict)
            {
                case BudgetVerdict.Fail:
                {
                    double overMb = (bytes - budgetBytes) / 1048576.0;
                    double budgetMb = budgetBytes / 1048576.0;
                    WriteOverBudget($"{message}  → trim ≥ {overMb:F2} MB to fit {budgetMb:F0} MB / {Budget.Name} budget");
                    return verdict;
                }
                case BudgetVerdict.Warn:
                {
                    double budgetMb = budgetBytes / 1048576.0;
                    WriteNearBudget($"{message}  ({100.0 * bytes / budgetBytes:F0}% of {budgetMb:F0} MB / {Budget.Name} budget)");
                    return verdict;
                }
                case BudgetVerdict.Pass:
                    WriteUnderBudget(message);
                    return verdict;
                case BudgetVerdict.NotApplicable:
                default:
                    WriteRanked(rank, total, message);
                    return verdict;
            }
        }

        /// <summary>
        /// Emits one PASS/WATCH/FAIL line for a per-category total. The
        /// caller supplies a short category label (e.g. "<c>User-mode WS</c>")
        /// and the actual / budget byte counts; the verdict and color are
        /// derived from <see cref="BudgetProfile.Evaluate"/>. Returns the
        /// verdict so the caller can roll it up into an overall image-fit
        /// PASS/FAIL banner.
        /// </summary>
        public BudgetVerdict WriteCategoryVerdict(string label, long actualBytes, long budgetBytes)
        {
            double actualMb = actualBytes / 1048576.0;
            double budgetMb = budgetBytes / 1048576.0;
            BudgetVerdict verdict = BudgetProfile.Evaluate(actualBytes, budgetBytes);
            string body = $"{label}: {actualMb,7:F1} MB used / {budgetMb,6:F0} MB budget ({Budget.Name} tier)";
            switch (verdict)
            {
                case BudgetVerdict.Fail:
                {
                    double overMb = actualMb - budgetMb;
                    WriteVerdictFail($"{body}  → cut ≥ {overMb:F1} MB to fit");
                    break;
                }
                case BudgetVerdict.Warn:
                    WriteVerdictWatch(body);
                    break;
                case BudgetVerdict.Pass:
                    WriteVerdictPass(body);
                    break;
                default:
                    WriteNormal($"   {body}  (no budget configured)");
                    break;
            }
            return verdict;
        }

        /// <summary>
        /// Writes <paramref name="message"/> with a color picked by rank so that
        /// callers can iterate a size-sorted list and not hand-pick colors.
        /// Rank 1 is the largest. <paramref name="total"/> is the count of items
        /// being emitted (after Top-N truncation).
        /// </summary>
        public void WriteRanked(int rank, int total, string message)
        {
            if (rank <= 1)
            {
                WriteCritical(message);
            }
            else if (rank <= Math.Max(2, total / 3))
            {
                WriteHigh(message);
            }
            else
            {
                WriteNormal(message);
            }
        }

        /// <summary>
        /// Low-level write: prints <paramref name="message"/> in
        /// <paramref name="color"/> on the console and the same text (no
        /// color codes) to the result file. Always restores the console
        /// foreground color afterwards.
        /// </summary>
        public void Write(string message, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            _file.WriteLine(message);
            Console.ResetColor();
        }

        /// <summary>Flushes and closes the result file.</summary>
        public void Dispose() => _file.Dispose();
    }
}
