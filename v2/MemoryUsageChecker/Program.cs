// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Command-line entry point for the MemoryUsageChecker sample.
    /// Parses arguments, opens the trace, dispatches the three exercise
    /// analyzers, and writes both a human-readable result file and a
    /// machine-grep-able diagnostic log next to the working directory.
    /// </summary>
    /// <remarks>
    /// Exit codes:
    /// <list type="bullet">
    ///   <item><c>0</c> - analysis completed (one or more exercises may have skipped).</item>
    ///   <item><c>1</c> - usage error (bad argument, missing trace file).</item>
    ///   <item><c>2</c> - unrecoverable error while opening or processing the trace; see the diagnostic log for the full stack.</item>
    /// </list>
    /// </remarks>
    internal class Program
    {
        /// <summary>
        /// Process entry point. See <see cref="PrintUsage"/> for the argument grammar.
        /// </summary>
        private static int Main(string[] args)
        {
            string tracePath = null;
            int topN = 30;
            int topStacks = 10;
            double minDisplayMb = 2.0;
            string symbolsOverride = null;
            bool noSymbols = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--top" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out topN) || topN <= 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("--top requires a positive integer.");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                }
                else if (a == "--top-stacks" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out topStacks) || topStacks < 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("--top-stacks requires a non-negative integer (0 disables all per-row stack dumps).");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                }
                else if (a == "--min-display-mb" && i + 1 < args.Length)
                {
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minDisplayMb) || minDisplayMb < 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("--min-display-mb requires a non-negative number (e.g. 2, 0.5, or 0 to disable filtering).");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                }
                else if (a == "--symbols" && i + 1 < args.Length)
                {
                    symbolsOverride = args[++i];
                }
                else if (a == "--no-symbols")
                {
                    noSymbols = true;
                }
                else if (tracePath == null && !a.StartsWith("--"))
                {
                    tracePath = a;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Unexpected argument: {a}");
                    Console.ResetColor();
                    PrintUsage();
                    WaitForKeyIfInteractive();
                    return 1;
                }
            }

            if (tracePath == null)
            {
                string autoExeDir;
                string autoPath = TryAutoDiscoverTrace(out autoExeDir);
                if (autoPath != null)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("No trace path specified. Auto-selected the most recent *.etl next to MemoryUsageChecker.exe:");
                    Console.WriteLine($"  {autoPath}");
                    Console.ResetColor();
                    Console.WriteLine();
                    tracePath = autoPath;
                }
                else
                {
                    PrintUsage();
                    Console.Error.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"No *.etl file was found next to MemoryUsageChecker.exe ({autoExeDir ?? "(exe folder unknown)"}).");
                    Console.ResetColor();
                    Console.Error.WriteLine("Tip: capture a trace with the bundled MemoryUsageTrace.cmd (elevated cmd.exe), then re-run by");
                    Console.Error.WriteLine("     double-clicking MemoryUsageChecker.exe or by passing the ETL path explicitly.");
                    WaitForKeyIfInteractive();
                    return 1;
                }
            }

            if (!File.Exists(tracePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"File does not exist: {tracePath}");
                Console.ResetColor();
                WaitForKeyIfInteractive();
                return 1;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            string resultPath = Path.GetFullPath($"MemoryUsage_Result_{timestamp}.txt");
            string logPath = Path.GetFullPath($"MemoryUsage_Diag_{timestamp}.log");
            // The JSON sidecar is always produced — it carries the same data
            // as the text result in a stable, versioned, machine-readable
            // shape so two captures can be A/B compared by AI / scripts
            // without re-parsing colored text or re-opening the raw ETL.
            string jsonPath = Path.GetFullPath($"MemoryUsage_Result_{timestamp}.json");
            // The summary file is a tiny plain-text companion (~30 lines)
            // containing only the executive summary block, intended for
            // direct paste into bug reports.
            string summaryPath = Path.GetFullPath($"MemoryUsage_Summary_{timestamp}.txt");

            // Open the diagnostic log FIRST so even a failure inside the
            // OutputWriter constructor or TraceProcessorBuilder lands in the log.
            Log.Open(logPath);
            Log.Info($"MemoryUsageChecker starting (pid={Process.GetCurrentProcess().Id})");
            Log.Info($"Assembly version : {typeof(Program).Assembly.GetName().Version}");
            Log.Info($"Runtime          : {RuntimeInformation.FrameworkDescription}");
            Log.Info($"OS               : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            Log.Info($"Working directory: {Environment.CurrentDirectory}");
            Log.Info($"Args             : {string.Join(" ", args)}");
            Log.Info($"Parsed --top     : {topN}");
            Log.Info($"Parsed --top-stacks : {topStacks}");
            Log.Info($"Parsed --min-display-mb : {minDisplayMb:F2}");
            Log.Info($"Parsed --symbols : {symbolsOverride ?? "(not specified)"}");
            Log.Info($"Parsed --no-symbols: {noSymbols}");
            try
            {
                long sizeBytes = new FileInfo(tracePath).Length;
                Log.Info($"Trace path       : {tracePath} ({sizeBytes / 1048576.0:F1} MB)");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stat trace file: {ex.Message}");
            }
            Log.Info($"Result file      : {resultPath}");
            Log.Info($"Diagnostic log   : {logPath}");
            Log.Info($"JSON sidecar     : {jsonPath}");

            // Build the JSON report alongside the text output so AI / scripts
            // can A/B-compare two runs (before vs after, device A vs device B)
            // without re-parsing color-coded text or re-opening the raw ETL.
            // See JsonReport.cs for the schema.
            JsonReport jsonReport = new JsonReport
            {
                Tool = new JsonReport.ToolInfo
                {
                    Version = typeof(Program).Assembly.GetName().Version?.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    HostOs = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"
                }
            };

            int exitCode;
            try
            {
                using var output = new OutputWriter(resultPath)
                {
                    TopN = topN,
                    TopStacks = topStacks,
                    MinDisplayBytes = (long)(minDisplayMb * 1024 * 1024),
                    NoSymbols = noSymbols
                };

                ITraceProcessorSettings settings = new TraceProcessorSettings { AllowLostEvents = true };
                ITraceProcessor trace;
                using (Log.Scope("TraceProcessorBuilder.Build"))
                {
                    trace = new TraceProcessorBuilder().WithSettings(settings).Build(tracePath);
                }

                // Block system sleep and display-off for the duration of the
                // trace processing pass + symbol download + per-exercise
                // analyzers. Symbol downloads against the public symbol server
                // can take many minutes on a cold cache, which is well past
                // typical idle-sleep timeouts, so without this an unattended
                // run silently fails partway through. Cleared automatically
                // when the using-block exits (success or exception).
                using (PowerRequest.Create("MemoryUsageChecker: processing ETL trace and loading symbols"))
                using (trace)
                {
                    ITraceMetadata metadata = trace.UseMetadata();
                    IPendingResult<ISystemMetadata> pendingSystemMetadata = trace.UseSystemMetadata();
                    IPendingResult<IProcessDataSource>          pendingProcesses    = trace.UseProcesses();
                    IPendingResult<IResidentSetDataSource>      pendingResidentSet  = trace.UseResidentSetData();
                    IPendingResult<ICommitDataSource>           pendingCommit       = trace.UseCommitData();
                    IPendingResult<IHeapSnapshotDataSource>     pendingHeap         = trace.UseHeapSnapshots();
                    IPendingResult<IPoolAllocationDataSource>   pendingPool         = trace.UsePoolAllocations();
                    IPendingResult<ISymbolDataSource>           pendingSymbols      = trace.UseSymbols();

                    using (Log.Scope("trace.Process"))
                    {
                        trace.Process();
                    }

                    ISystemMetadata systemMetadata = pendingSystemMetadata.HasResult ? pendingSystemMetadata.Result : null;

                    // Log a quick HasResult snapshot so missing data sources can
                    // be confirmed from the log without re-running the trace.
                    Log.Info($"DataSource Processes    : HasResult={pendingProcesses.HasResult}");
                    Log.Info($"DataSource ResidentSet  : HasResult={pendingResidentSet.HasResult}, snapshots={(pendingResidentSet.HasResult ? pendingResidentSet.Result.Snapshots.Count : 0)}");
                    Log.Info($"DataSource Commit       : HasResult={pendingCommit.HasResult}, lifetimes={(pendingCommit.HasResult ? pendingCommit.Result.CommitLifetimes.Count : 0)}");
                    Log.Info($"DataSource Heap         : HasResult={pendingHeap.HasResult}, snapshots={(pendingHeap.HasResult ? pendingHeap.Result.Snapshots.Count : 0)}");
                    Log.Info($"DataSource Pool         : HasResult={pendingPool.HasResult}, intervals={(pendingPool.HasResult ? pendingPool.Result.Intervals.Count : 0)}");
                    Log.Info($"DataSource Symbols      : HasResult={pendingSymbols.HasResult}");

                    PopulateReportMetadata(jsonReport, tracePath, metadata, systemMetadata,
                        pendingProcesses, pendingResidentSet, pendingCommit, pendingHeap, pendingPool, pendingSymbols);

                    output.WriteHeader($"Trace Path:\t{tracePath}");
                    output.WriteHeader($"Trace Start Time:\t{metadata.StartTime}");
                    output.WriteHeader($"Trace Stop Time:\t{metadata.StopTime}");
                    output.WriteHeader($"OS / System:\t{ImageFormatter.FormatOsHeader(metadata, systemMetadata)}");
                    output.WriteHeader($"Result File:\t{resultPath}");
                    output.WriteHeader($"Diagnostic Log:\t{logPath}");
                    output.WriteHeader($"JSON Sidecar:\t{jsonPath}");
                    output.WriteHeader($"Summary File:\t{summaryPath}");
                    output.WriteBlank();
                    output.WriteNotable("=> The 'EXECUTIVE SUMMARY' section at the end of this file (and the standalone Summary File above) lists the top actionable findings. The per-exercise detail follows below.");
                    output.WriteBlank();

                    LoadSymbols(output, pendingSymbols, symbolsOverride, noSymbols, jsonReport);
                    output.WriteBlank();

                    RunExercise(output, "Exercise 1", () => Exercise1_ResidentSet.Run(output, metadata, pendingProcesses, pendingResidentSet, jsonReport));
                    output.WriteBlank();

                    RunExercise(output, "Exercise 2", () => Exercise2_VirtualAllocHeap.Run(output, metadata, pendingProcesses, pendingCommit, pendingHeap, jsonReport));
                    output.WriteBlank();

                    RunExercise(output, "Exercise 3", () => Exercise3_Pool.Run(output, metadata, pendingProcesses, pendingPool, pendingResidentSet, jsonReport));
                    output.WriteBlank();

                    // Executive summary: a short, scannable digest of the
                    // most actionable findings across all three exercises.
                    // Rendered as a section at the end of the result file so
                    // the user only has to scroll to the bottom (or jump to
                    // the EXECUTIVE SUMMARY banner) to see the punchline,
                    // and also persisted as a tiny companion *.summary.txt
                    // file that can be pasted into bug reports.
                    ExecutiveSummary.WriteToOutputAndFile(output, jsonReport, summaryPath);
                    Log.Info($"Executive summary written: {summaryPath}");
                }

                // Persist the JSON sidecar AFTER the trace has been disposed
                // so file handles for the ETL are released first.
                using (Log.Scope("WriteJsonReport"))
                {
                    try
                    {
                        JsonReportWriter.Save(jsonReport, jsonPath);
                        output.WriteInfo($"JSON sidecar written: {jsonPath}");
                        Log.Info($"JSON sidecar written: {jsonPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Failed to write JSON sidecar", ex);
                        output.WriteNotable($"Warning: failed to write JSON sidecar ({ex.Message}). Text result and diagnostic log are unaffected.");
                    }
                }

                exitCode = 0;
            }
            catch (Exception ex)
            {
                Log.Error("Fatal exception in Main", ex);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"An error occurred: {ex.Message}");
                Console.Error.WriteLine($"See diagnostic log for the full stack trace: {logPath}");
                Console.ResetColor();
                exitCode = 2;
            }
            finally
            {
                Log.Info($"MemoryUsageChecker exiting (exitCode pending Console.ReadKey)");
                Log.Close();
            }

            Console.WriteLine("Press any key to exit...");
            // Safely handle redirected stdin (e.g. batch invocations where
            // there is no console attached). Without this guard, the .NET
            // runtime throws InvalidOperationException and the process exits
            // with the .NET unhandled-exception code, masking our real
            // exit code from callers that just want to chain commands.
            try { _ = Console.ReadKey(); } catch (InvalidOperationException) { /* stdin redirected; skip */ }
            return exitCode;
        }

        /// <summary>
        /// Wraps the execution of a single Exercise so any unhandled
        /// exception is logged with full stack trace and reported as a
        /// human-readable skip line, instead of aborting the entire run.
        /// This means a regression in (say) Exercise 3 still produces useful
        /// Exercise 1 and Exercise 2 output.
        /// </summary>
        private static void RunExercise(OutputWriter output, string name, Action body)
        {
            using (Log.Scope(name))
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    Log.Error($"{name} threw an unhandled exception", ex);
                    output.WriteSkipped($"[{name} aborted: {ex.GetType().Name}: {ex.Message} — see {Log.Path ?? "diagnostic log"}]");
                }
            }
        }

        /// <summary>
        /// Resolves the symbol path with the documented precedence
        /// (<c>--symbols</c> &gt; <c>_NT_SYMBOL_PATH</c> &gt; Microsoft Public
        /// Symbol Server with a per-user cache) and triggers symbol loading.
        /// Failures are logged and surfaced via <see cref="OutputWriter.WriteNotable"/>
        /// but never abort the analysis: stacks just show <c>[no symbols]</c>.
        /// </summary>
        private static void LoadSymbols(OutputWriter output, IPendingResult<ISymbolDataSource> pendingSymbols, string symbolsOverride, bool noSymbols, JsonReport jsonReport)
        {
            if (noSymbols)
            {
                output.WriteInfo("Symbols disabled (--no-symbols). Stack frames will show [no symbols].");
                Log.Info("Symbol loading skipped because --no-symbols was specified.");
                if (jsonReport != null)
                {
                    jsonReport.Symbols.Source = "Disabled (--no-symbols)";
                    jsonReport.Symbols.Loaded = false;
                }
                return;
            }

            if (!pendingSymbols.HasResult)
            {
                output.WriteNotable("Warning: symbol data source did not return a result. Stacks will show [no symbols].");
                Log.Warn("pendingSymbols.HasResult = false; skipping symbol load.");
                if (jsonReport != null)
                {
                    jsonReport.Symbols.Source = "(symbol data source did not return a result)";
                    jsonReport.Symbols.Loaded = false;
                }
                return;
            }

            ISymbolPath symbolPath;
            string symbolSource;
            if (symbolsOverride != null)
            {
                symbolPath = new SymbolPath(symbolsOverride);
                symbolSource = $"--symbols (explicit): {symbolsOverride}";
            }
            else
            {
                string envPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
                if (!string.IsNullOrWhiteSpace(envPath))
                {
                    symbolPath = new SymbolPath(envPath);
                    symbolSource = $"_NT_SYMBOL_PATH: {envPath}";
                }
                else
                {
                    string cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SymbolCache");
                    Directory.CreateDirectory(cacheDir);
                    string defaultPath = $"SRV*{cacheDir}*https://msdl.microsoft.com/download/symbols";
                    symbolPath = new SymbolPath(defaultPath);
                    symbolSource = $"Microsoft Public Symbol Server (cache: {cacheDir})";
                }
            }

            output.WriteInfo($"Symbol path: {symbolSource}");
            Log.Info($"Symbol source resolved to: {symbolSource}");
            if (jsonReport != null)
            {
                jsonReport.Symbols.Source = symbolSource;
            }
            using (Log.Scope("LoadSymbolsForConsoleAsync"))
            {
                // Wrap Console.Out so the SDK's per-image progress lines
                // (e.g. "45.2% (1053 of 2330; 1050 loaded)") are folded into
                // a single in-place progress bar instead of one new console
                // line per image. The interceptor passes all other output
                // through unchanged. Complete() finalizes any pending bar
                // with a newline so subsequent writes start on a clean line.
                TextWriter savedConsoleOut = Console.Out;
                bool stdoutRedirected = Console.IsOutputRedirected;
                SymbolProgressConsoleWriter interceptor = new SymbolProgressConsoleWriter(savedConsoleOut, stdoutRedirected);
                try
                {
                    try
                    {
                        Console.SetOut(interceptor);
                        pendingSymbols.Result.LoadSymbolsForConsoleAsync(SymCachePath.Automatic, symbolPath).GetAwaiter().GetResult();
                        if (jsonReport != null) jsonReport.Symbols.Loaded = true;
                    }
                    finally
                    {
                        Console.SetOut(savedConsoleOut);
                        interceptor.Complete();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Symbol load failed", ex);
                    output.WriteNotable($"Warning: failed to load symbols ({ex.Message}). Stacks will show [no symbols].");
                    if (jsonReport != null) jsonReport.Symbols.Loaded = false;
                }
            }
        }

        /// <summary>
        /// Populates the trace + data-sources blocks of the JSON report from
        /// the resolved trace metadata. Mirrors <see cref="ImageFormatter.FormatOsHeader"/>
        /// for the human OS-summary string, then captures the individual OS
        /// fields (when the SDK exposes them) so consumers can match on
        /// <c>osVersion</c> + <c>architecture</c> without parsing the
        /// rendered string.
        /// </summary>
        private static void PopulateReportMetadata(
            JsonReport jsonReport,
            string tracePath,
            ITraceMetadata metadata,
            ISystemMetadata systemMetadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<IResidentSetDataSource> pendingResidentSet,
            IPendingResult<ICommitDataSource> pendingCommit,
            IPendingResult<IHeapSnapshotDataSource> pendingHeap,
            IPendingResult<IPoolAllocationDataSource> pendingPool,
            IPendingResult<ISymbolDataSource> pendingSymbols)
        {
            if (jsonReport == null) return;

            jsonReport.Trace.Path = tracePath;
            try { jsonReport.Trace.SizeBytes = new FileInfo(tracePath).Length; } catch { /* best-effort */ }

            try
            {
                jsonReport.Trace.StartTimeUtc = metadata.StartTime.UtcDateTime;
                jsonReport.Trace.StopTimeUtc = metadata.StopTime.UtcDateTime;
                jsonReport.Trace.DurationSeconds = (metadata.StopTime - metadata.StartTime).TotalSeconds;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not populate trace times for JSON: {ex.Message}");
            }

            jsonReport.Trace.OsSummary = ImageFormatter.FormatOsHeader(metadata, systemMetadata);
            jsonReport.Trace.OsVersion = ImageFormatter.GetOsVersionWithRevision(metadata, systemMetadata);
            jsonReport.Trace.OsProductName = ImageFormatter.GetOsProductName(systemMetadata);
            jsonReport.Trace.OsDisplayVersion = ImageFormatter.GetOsDisplayVersion(systemMetadata);
            jsonReport.Trace.OsBuildLab = ImageFormatter.GetOsBuildLab(metadata, systemMetadata);
            jsonReport.Trace.Architecture = ImageFormatter.SafeReadProperty(metadata, "Architecture")
                                          ?? ImageFormatter.SafeReadProperty(systemMetadata, "Architecture")
                                          ?? ImageFormatter.SafeReadProperty(metadata, "ProcessorArchitecture")
                                          ?? ImageFormatter.SafeReadProperty(systemMetadata, "ProcessorArchitecture");
            jsonReport.Trace.MachineName = ImageFormatter.SafeReadProperty(metadata, "MachineName")
                                         ?? ImageFormatter.SafeReadProperty(systemMetadata, "MachineName")
                                         ?? ImageFormatter.SafeReadProperty(metadata, "ComputerName")
                                         ?? ImageFormatter.SafeReadProperty(systemMetadata, "ComputerName");

            jsonReport.DataSources.Processes   = new JsonReport.DataSourceState { HasResult = pendingProcesses.HasResult };
            jsonReport.DataSources.ResidentSet = new JsonReport.DataSourceState
            {
                HasResult = pendingResidentSet.HasResult,
                ItemCount = pendingResidentSet.HasResult ? pendingResidentSet.Result.Snapshots.Count : (long?)null
            };
            jsonReport.DataSources.Commit = new JsonReport.DataSourceState
            {
                HasResult = pendingCommit.HasResult,
                ItemCount = pendingCommit.HasResult ? pendingCommit.Result.CommitLifetimes.Count : (long?)null
            };
            jsonReport.DataSources.Heap = new JsonReport.DataSourceState
            {
                HasResult = pendingHeap.HasResult,
                ItemCount = pendingHeap.HasResult ? pendingHeap.Result.Snapshots.Count : (long?)null
            };
            jsonReport.DataSources.Pool = new JsonReport.DataSourceState
            {
                HasResult = pendingPool.HasResult,
                ItemCount = pendingPool.HasResult ? pendingPool.Result.Intervals.Count : (long?)null
            };
            jsonReport.DataSources.Symbols = new JsonReport.DataSourceState { HasResult = pendingSymbols.HasResult };
        }

        /// <summary>Prints a one-line usage banner to stderr.</summary>
        private static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: MemoryUsageChecker.exe [<trace.etl>] [--top N] [--top-stacks N] [--min-display-mb V] [--symbols <path>] [--no-symbols]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("When <trace.etl> is omitted (e.g. when MemoryUsageChecker.exe is launched");
            Console.Error.WriteLine("by double-clicking it in Explorer), the tool auto-selects the most recently");
            Console.Error.WriteLine("modified *.etl file located in the same folder as the .exe, preferring");
            Console.Error.WriteLine("MemoryUsage-Trace.etl (the canonical name produced by MemoryUsageTrace.cmd).");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --top N             Show up to N rows in each outer Top-N table (default 30).");
            Console.Error.WriteLine("  --top-stacks N      Show the per-row inner stack drill-down only for the first");
            Console.Error.WriteLine("                      N outer rows (default 10; 0 disables all per-row stack dumps).");
            Console.Error.WriteLine("                      The outer Top-N table still lists up to --top entries.");
            Console.Error.WriteLine("  --min-display-mb V  Hide rows below V MiB in BOTH outer Top-N tables AND inner");
            Console.Error.WriteLine("                      Top-K bucket / stack rows (default 2; pass 0 to disable");
            Console.Error.WriteLine("                      filtering and show every row).");
            Console.Error.WriteLine("  --symbols <path>    Override the symbol search path passed to the EventTracing SDK.");
            Console.Error.WriteLine("  --no-symbols        Skip symbol load entirely. Per-row stack drill-downs in");
            Console.Error.WriteLine("                      Exercises 2 and 3 are also suppressed because raw addresses");
            Console.Error.WriteLine("                      are not actionable; outer Top-N tables and the executive");
            Console.Error.WriteLine("                      summary are still produced.");
        }

        /// <summary>
        /// Locates the most recently modified <c>*.etl</c> in the folder
        /// containing <c>MemoryUsageChecker.exe</c>. Prefers the canonical
        /// <c>MemoryUsage-Trace.etl</c> name produced by the bundled
        /// <c>MemoryUsageTrace.cmd</c> collection script so a tester who
        /// just captured a fresh trace can double-click the .exe and have
        /// it picked up automatically. Falls back to the newest <c>*.etl</c>
        /// by <c>LastWriteTimeUtc</c> when the canonical name is missing,
        /// so renamed / archived captures still work.
        /// </summary>
        /// <param name="searchedDir">
        /// The directory that was inspected, returned to the caller so the
        /// "no trace found" error message can identify exactly where the tool
        /// looked. <c>null</c> when the .exe folder could not be resolved.
        /// </param>
        /// <returns>Absolute path of the selected ETL, or <c>null</c> when none was found.</returns>
        /// <remarks>
        /// We use <see cref="Environment.ProcessPath"/> (not
        /// <see cref="AppContext.BaseDirectory"/>) because the project ships
        /// as a self-contained single-file publish — <c>BaseDirectory</c>
        /// points at the extraction temp folder, while <c>ProcessPath</c>
        /// points at the actual on-disk .exe alongside the user's ETL.
        /// </remarks>
        private static string TryAutoDiscoverTrace(out string searchedDir)
        {
            searchedDir = null;
            try
            {
                string exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return null;
                string exeDir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return null;
                searchedDir = exeDir;

                string preferred = Path.Combine(exeDir, "MemoryUsage-Trace.etl");
                if (File.Exists(preferred)) return preferred;

                FileInfo[] etls = new DirectoryInfo(exeDir).GetFiles("*.etl");
                if (etls.Length == 0) return null;
                Array.Sort(etls, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                return etls[0].FullName;
            }
            catch (Exception ex)
            {
                Log.Warn($"Auto-discovery of *.etl in EXE folder failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pauses with the standard "Press any key to exit..." prompt only
        /// when there is a real interactive console attached. Used on the
        /// early-exit error paths so that a user who double-clicked the
        /// .exe (and therefore has no parent shell to hold the window open)
        /// can actually read the error message before Windows closes the
        /// console. Silently skipped when stdin is redirected (CI, batch
        /// pipelines, etc.) so it never deadlocks unattended runs.
        /// </summary>
        private static void WaitForKeyIfInteractive()
        {
            try
            {
                if (Console.IsInputRedirected) return;
            }
            catch
            {
                return;
            }
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            try { _ = Console.ReadKey(); } catch (InvalidOperationException) { /* stdin redirected mid-flight; skip */ }
        }
    }
}
