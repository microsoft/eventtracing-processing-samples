// © Microsoft Corporation. All rights reserved.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryUsageChecker
{
    /// <summary>
    /// A <see cref="TextWriter"/> shim used to wrap <see cref="Console.Out"/>
    /// while <c>LoadSymbolsForConsoleAsync</c> runs. Detects the SDK's
    /// per-image progress lines (e.g. <c>"45.2% (1053 of 2330; 1050 loaded)"</c>)
    /// and re-renders them as a single in-place progress bar instead of
    /// flooding the console with one new line per image. Any other text the
    /// SDK writes is forwarded through unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interceptor never owns the underlying writer (it is <see cref="Console.Out"/>)
    /// and therefore never disposes it. The caller is expected to <see cref="Complete"/>
    /// the interceptor in a <c>finally</c>, which terminates any in-flight bar with a
    /// newline and flushes the buffer so subsequent output starts on a clean line.
    /// </para>
    /// <para>
    /// Behavior in non-interactive output (<see cref="Console.IsOutputRedirected"/>):
    /// the carriage-return overwrite is suppressed and only milestone lines are emitted
    /// (every 10% and on the final 100% update). This keeps redirected logs readable
    /// without losing visibility of progress.
    /// </para>
    /// <para>
    /// Why intercept the console instead of switching to <c>LoadSymbolsAsync</c> with
    /// our own <c>IProgress</c>: the SDK's higher-level
    /// <c>LoadSymbolsForConsoleAsync</c> helper picks correct defaults for
    /// <c>prefetchSymbols</c> / <c>optimizePrefetching</c> / cancellation. Wrapping its
    /// output means we change presentation only — never which symbols actually load.
    /// </para>
    /// </remarks>
    internal sealed class SymbolProgressConsoleWriter : TextWriter
    {
        private static readonly Regex ProgressLineRegex = new Regex(
            @"^\s*(?<pct>\d+(?:[\.,]\d+)?)%\s*\(\s*(?<proc>\d+)\s+of\s+(?<total>\d+);\s*(?<loaded>\d+)\s+loaded\s*\)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly bool s_unicodeSupported = DetectUnicodeSupport();
        private static readonly char s_filledChar = s_unicodeSupported ? '\u2588' : '#'; // █
        private static readonly char s_emptyChar  = s_unicodeSupported ? '\u2591' : '-'; // ░

        private readonly TextWriter _inner;
        private readonly bool _redirected;
        private readonly int _barWidth;
        private readonly StringBuilder _buffer = new StringBuilder(160);
        private readonly object _sync = new object();
        private readonly TimeSpan _minRedrawInterval = TimeSpan.FromMilliseconds(100);

        private bool _barShown;
        private int _lastBarLength;
        private int _lastMilestoneDecile = -1;
        private DateTime _lastRenderUtc = DateTime.MinValue;

        /// <summary>
        /// Wraps <paramref name="inner"/> (typically the saved <see cref="Console.Out"/>).
        /// </summary>
        /// <param name="inner">Underlying writer to forward output to. Never disposed.</param>
        /// <param name="redirected">
        /// True when stdout is redirected (file / pipe). When true, the bar falls back to
        /// milestone-only emission so log files do not get a giant single line.
        /// </param>
        /// <param name="barWidth">Number of bar cells (clamped to a sensible minimum).</param>
        public SymbolProgressConsoleWriter(TextWriter inner, bool redirected, int barWidth = 30)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _redirected = redirected;
            _barWidth = Math.Max(10, barWidth);
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_sync) { AppendChar(value); }
        }

        public override void Write(string value)
        {
            if (value == null) return;
            lock (_sync)
            {
                for (int i = 0; i < value.Length; i++) AppendChar(value[i]);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (buffer == null) return;
            lock (_sync)
            {
                for (int i = 0; i < count; i++) AppendChar(buffer[index + i]);
            }
        }

        public override void Flush() => _inner.Flush();

        /// <summary>
        /// Finalizes any pending bar (writes a trailing newline) and drains any
        /// partially-buffered text. MUST be called from a <c>finally</c> after the
        /// SDK call returns, so subsequent console output starts on a clean line.
        /// </summary>
        public void Complete()
        {
            lock (_sync)
            {
                if (_buffer.Length > 0)
                {
                    string remainder = _buffer.ToString().TrimEnd('\r');
                    _buffer.Clear();
                    if (remainder.Length > 0)
                    {
                        if (ProgressLineRegex.IsMatch(remainder))
                        {
                            ProcessLine(remainder);
                        }
                        else
                        {
                            FinalizeBarIfShown();
                            _inner.Write(remainder);
                        }
                    }
                }
                FinalizeBarIfShown();
                _inner.Flush();
            }
        }

        private void AppendChar(char c)
        {
            if (c == '\n')
            {
                string line = _buffer.ToString().TrimEnd('\r');
                _buffer.Clear();
                ProcessLine(line);
            }
            else
            {
                _buffer.Append(c);
            }
        }

        private void ProcessLine(string line)
        {
            Match m = ProgressLineRegex.Match(line);
            if (!m.Success)
            {
                FinalizeBarIfShown();
                _inner.WriteLine(line);
                return;
            }

            if (!long.TryParse(m.Groups["proc"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long processed) ||
                !long.TryParse(m.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long total) ||
                !long.TryParse(m.Groups["loaded"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long loaded))
            {
                FinalizeBarIfShown();
                _inner.WriteLine(line);
                return;
            }

            double pct = total > 0 ? (100.0 * processed / total) : 0.0;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            bool isFinal = total > 0 && processed >= total;

            if (_redirected)
            {
                int decile = (int)(pct / 10.0);
                if (decile > _lastMilestoneDecile || isFinal)
                {
                    _lastMilestoneDecile = decile;
                    _inner.WriteLine(FormatLine(pct, processed, total, loaded));
                }
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!isFinal && _barShown && (now - _lastRenderUtc) < _minRedrawInterval)
            {
                return;
            }
            _lastRenderUtc = now;

            string display = FormatLine(pct, processed, total, loaded);
            int padLen = _lastBarLength > display.Length ? _lastBarLength - display.Length : 0;

            _inner.Write('\r');
            _inner.Write(display);
            if (padLen > 0) _inner.Write(new string(' ', padLen));
            _inner.Flush();

            _barShown = true;
            _lastBarLength = display.Length;

            if (isFinal)
            {
                _inner.WriteLine();
                _barShown = false;
                _lastBarLength = 0;
            }
        }

        private string FormatLine(double pct, long processed, long total, long loaded)
        {
            int filled = (int)Math.Round(_barWidth * pct / 100.0);
            if (filled < 0) filled = 0;
            if (filled > _barWidth) filled = _barWidth;
            return string.Format(
                CultureInfo.InvariantCulture,
                "Loading symbols: [{0}{1}] {2,5:F1}% ({3}/{4}; {5} loaded)",
                new string(s_filledChar, filled),
                new string(s_emptyChar, _barWidth - filled),
                pct,
                processed,
                total,
                loaded);
        }

        private void FinalizeBarIfShown()
        {
            if (_barShown)
            {
                _inner.WriteLine();
                _barShown = false;
                _lastBarLength = 0;
            }
        }

        private static bool DetectUnicodeSupport()
        {
            // We need both characters to round-trip through the current console
            // encoding without being replaced or substituted. PS7 / Windows
            // Terminal on modern Windows default to UTF-8 (CP 65001) where both
            // glyphs map trivially. PS5.x on Win10 typically inherits the OEM
            // code page — CP 437 (US), CP 850 (Latin-1), CP 932/936/949/950
            // (CJK) all map these glyphs to a single byte via their codepage
            // tables, so we can still render the block bar there. CP 1252
            // (Windows ANSI) has no real mapping; depending on the active
            // EncoderFallback the encoder may silently substitute '?' or do a
            // best-fit substitution to '¦' (broken bar) — neither is what the
            // user wants, so we fall back to the ASCII '#' / '-' bar.
            //
            // We probe by round-tripping the characters: encode them with the
            // active Console.OutputEncoding, decode the bytes back, and compare
            // ordinally to the originals. This correctly rejects both the '?'
            // replacement path and silent best-fit substitution that the
            // exception-fallback path on its own would miss.
            try
            {
                Encoding enc = Console.OutputEncoding;
                if (enc == null) return false;
                char[] probe = new[] { '\u2588', '\u2591' };
                byte[] encoded = enc.GetBytes(probe);
                string decoded = enc.GetString(encoded);
                return string.Equals(decoded, new string(probe), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
