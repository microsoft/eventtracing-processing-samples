// © Microsoft Corporation. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Lightweight structured diagnostic log written next to the human-readable
    /// result file. This is the FIRST artifact a maintainer or external IHV
    /// should ask a reporter to attach when they say "MemoryUsageChecker
    /// crashed", "the output was empty for Exercise N", "the symbols failed
    /// to load", or "the tool printed something I do not understand".
    /// </summary>
    /// <remarks>
    /// One entry per line, format <c>HH:mm:ss.fff [LEVEL] message</c>.
    /// Exceptions are emitted multi-line so the full stack trace and any
    /// inner exception survive in the log file.
    /// <para>
    /// <see cref="Scope"/> returns an <see cref="IDisposable"/> that writes a
    /// "BEGIN name" line immediately and a matching "END name (elapsed=Xs)"
    /// line on dispose, so per-phase timing is greppable without parsing.
    /// </para>
    /// <para>
    /// The implementation is intentionally allocation-light and lock-protected
    /// so the log stays consistent even if a future change calls into it from
    /// background threads. All writes are best-effort: if opening or writing
    /// to the log file fails, the tool continues to run normally and the user
    /// still gets the human-readable result file and console output.
    /// </para>
    /// </remarks>
    internal static class Log
    {
        private static StreamWriter _writer;
        private static string _path;
        private static readonly object _lock = new object();

        /// <summary>
        /// Returns the absolute path of the active log file, or <c>null</c>
        /// when <see cref="Open"/> has not been called (or failed).
        /// </summary>
        public static string Path => _path;

        /// <summary>
        /// Opens (or recreates) the diagnostic log file at <paramref name="path"/>.
        /// Failures are swallowed so they cannot mask the actual analysis error
        /// the user is trying to debug.
        /// </summary>
        public static void Open(string path)
        {
            lock (_lock)
            {
                try
                {
                    _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true
                    };
                    _path = path;
                }
                catch
                {
                    _writer = null;
                    _path = null;
                }
            }
        }

        /// <summary>
        /// Flushes and closes the diagnostic log. Safe to call multiple times.
        /// </summary>
        public static void Close()
        {
            lock (_lock)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                _path = null;
            }
        }

        /// <summary>Writes an INFO-level entry.</summary>
        public static void Info(string message) => Write("INFO", message, null);

        /// <summary>Writes a WARN-level entry for non-fatal anomalies.</summary>
        public static void Warn(string message) => Write("WARN", message, null);

        /// <summary>
        /// Writes an ERROR-level entry. When <paramref name="ex"/> is supplied,
        /// the exception type, message, stack trace, and any inner exception
        /// are appended on subsequent indented lines.
        /// </summary>
        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        /// <summary>
        /// Begins a timed, named scope. The returned token writes a paired
        /// END entry (with elapsed seconds) when disposed. Always wrap with
        /// <c>using</c> so the END entry is emitted even when an exception
        /// unwinds the stack.
        /// </summary>
        public static IDisposable Scope(string name) => new ScopeImpl(name);

        private static void Write(string level, string message, Exception ex)
        {
            if (_writer == null) return;
            string when = DateTime.Now.ToString("HH:mm:ss.fff");
            var sb = new StringBuilder(160);
            sb.Append(when).Append(" [").Append(level).Append("] ").Append(message ?? string.Empty);
            if (ex != null)
            {
                sb.AppendLine();
                sb.Append("    Exception : ").AppendLine(ex.GetType().FullName);
                sb.Append("    Message   : ").AppendLine(ex.Message);
                sb.AppendLine("    StackTrace:");
                foreach (var line in (ex.StackTrace ?? string.Empty).Split('\n'))
                {
                    sb.Append("      ").AppendLine(line.TrimEnd('\r'));
                }
                Exception inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    sb.Append("    InnerException[").Append(depth).Append("]: ")
                      .Append(inner.GetType().FullName).Append(" : ").AppendLine(inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }
            }
            lock (_lock)
            {
                try { _writer.WriteLine(sb.ToString()); } catch { /* best-effort */ }
            }
        }

        private sealed class ScopeImpl : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _stopwatch;

            public ScopeImpl(string name)
            {
                _name = name ?? "(unnamed)";
                _stopwatch = Stopwatch.StartNew();
                Info("BEGIN " + _name);
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                Info($"END   {_name} (elapsed={_stopwatch.Elapsed.TotalSeconds:F3}s)");
            }
        }
    }
}
