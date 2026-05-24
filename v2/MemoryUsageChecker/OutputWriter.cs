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
    /// Severity convention used by the analyzers (Exercises 1-3):
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
    /// Size-ranked findings use the <c>WriteCritical</c> / <c>WriteHigh</c> /
    /// <c>WriteNormal</c> / <c>WriteTail</c> family, or — more commonly — the
    /// convenience <see cref="WriteRanked"/> helper which selects the right
    /// color automatically based on rank.
    /// </para>
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

        /// <summary>Maximum number of rows displayed per "Top N" table (default 30).</summary>
        public int TopN { get; init; } = 30;

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
