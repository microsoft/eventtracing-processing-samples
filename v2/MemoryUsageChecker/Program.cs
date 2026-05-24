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
            int topN = 10;
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
                    return 1;
                }
            }

            if (tracePath == null)
            {
                PrintUsage();
                return 1;
            }

            if (!File.Exists(tracePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("File does not exist! Please check the trace path again.");
                Console.ResetColor();
                return 1;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            string resultPath = Path.GetFullPath($"MemoryUsage_Result_{timestamp}.txt");
            string logPath = Path.GetFullPath($"MemoryUsage_Diag_{timestamp}.log");

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

            int exitCode;
            try
            {
                using var output = new OutputWriter(resultPath) { TopN = topN };

                ITraceProcessorSettings settings = new TraceProcessorSettings { AllowLostEvents = true };
                ITraceProcessor trace;
                using (Log.Scope("TraceProcessorBuilder.Build"))
                {
                    trace = new TraceProcessorBuilder().WithSettings(settings).Build(tracePath);
                }

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

                    output.WriteHeader($"Trace Path:\t{tracePath}");
                    output.WriteHeader($"Trace Start Time:\t{metadata.StartTime}");
                    output.WriteHeader($"Trace Stop Time:\t{metadata.StopTime}");
                    output.WriteHeader($"OS / System:\t{ImageFormatter.FormatOsHeader(metadata, systemMetadata)}");
                    output.WriteHeader($"Result File:\t{resultPath}");
                    output.WriteHeader($"Diagnostic Log:\t{logPath}");
                    output.WriteBlank();

                    LoadSymbols(output, pendingSymbols, symbolsOverride, noSymbols);
                    output.WriteBlank();

                    RunExercise(output, "Exercise 1", () => Exercise1_ResidentSet.Run(output, metadata, pendingProcesses, pendingResidentSet));
                    output.WriteBlank();

                    RunExercise(output, "Exercise 2", () => Exercise2_VirtualAllocHeap.Run(output, metadata, pendingProcesses, pendingCommit, pendingHeap));
                    output.WriteBlank();

                    RunExercise(output, "Exercise 3", () => Exercise3_Pool.Run(output, metadata, pendingProcesses, pendingPool, pendingResidentSet));
                    output.WriteBlank();
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
        private static void LoadSymbols(OutputWriter output, IPendingResult<ISymbolDataSource> pendingSymbols, string symbolsOverride, bool noSymbols)
        {
            if (noSymbols)
            {
                output.WriteInfo("Symbols disabled (--no-symbols). Stack frames will show [no symbols].");
                Log.Info("Symbol loading skipped because --no-symbols was specified.");
                return;
            }

            if (!pendingSymbols.HasResult)
            {
                output.WriteNotable("Warning: symbol data source did not return a result. Stacks will show [no symbols].");
                Log.Warn("pendingSymbols.HasResult = false; skipping symbol load.");
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
            using (Log.Scope("LoadSymbolsForConsoleAsync"))
            {
                try
                {
                    pendingSymbols.Result.LoadSymbolsForConsoleAsync(SymCachePath.Automatic, symbolPath).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Error("Symbol load failed", ex);
                    output.WriteNotable($"Warning: failed to load symbols ({ex.Message}). Stacks will show [no symbols].");
                }
            }
        }

        /// <summary>Prints a one-line usage banner to stderr.</summary>
        private static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: MemoryUsageChecker.exe <trace.etl> [--top N] [--symbols <path>] [--no-symbols]");
        }
    }
}
