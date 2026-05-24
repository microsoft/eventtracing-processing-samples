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
            // Force UTF-8 console output so the budget-verdict glyphs
            // ('✓' / '✗' emitted by OutputWriter.WriteOverBudget /
            // WriteUnderBudget / WriteVerdictPass / WriteVerdictFail) render
            // correctly in the default Windows console (code page 437/1252).
            // Without this, OEMs reading the report on a stock command
            // prompt see '?' boxes instead of the green/red improvement-
            // direction glyph and the visual scan signal is lost.
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* non-interactive host */ }

            string tracePath = null;
            BudgetProfile profile = BudgetProfile.EightGb();
            bool profileExplicit = false;
            string profilesFileOverride = null;
            bool writeDefaultProfilesAndExit = false;
            string profileNameRequested = null;
            int? topProcessesOverride = null;
            int? topDriversOverride = null;
            int topStacks = 10;
            double? minDisplayMbOverride = null;
            double? perProcessWsMbOverride = null;
            double? perProcessVaMbOverride = null;
            double? perDriverPoolMbOverride = null;
            double? perDriverCodeMbOverride = null;
            double? totalUserWsMbOverride = null;
            double? totalDriverPoolMbOverride = null;
            double? totalDriverCodeMbOverride = null;
            string symbolsOverride = null;
            bool noSymbols = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--profile" && i + 1 < args.Length)
                {
                    string name = args[++i];
                    BudgetProfile resolved = BudgetProfile.TryFromName(name);
                    if (resolved == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine($"--profile must be '16gb', '8gb', or '4gb' (got '{name}').");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                    profile = resolved;
                    profileExplicit = true;
                    profileNameRequested = name.Trim().ToLowerInvariant() switch
                    {
                        "16" => "16gb",
                        "8" => "8gb",
                        "4" => "4gb",
                        _ => name.Trim().ToLowerInvariant(),
                    };
                }
                else if (a == "--profiles-file" && i + 1 < args.Length)
                {
                    profilesFileOverride = args[++i];
                }
                else if (a == "--write-default-profiles")
                {
                    writeDefaultProfilesAndExit = true;
                }
                else if (a == "--top" && i + 1 < args.Length)
                {
                    // Back-compat: --top applies to BOTH processes and drivers.
                    // Prefer --top-processes / --top-drivers in new scripts.
                    if (!int.TryParse(args[++i], out int legacy) || legacy <= 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("--top requires a positive integer.");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                    topProcessesOverride = legacy;
                    topDriversOverride = legacy;
                }
                else if (a == "--top-processes" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out int v) || v <= 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("--top-processes requires a positive integer.");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                    topProcessesOverride = v;
                }
                else if (a == "--top-drivers" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out int v) || v <= 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("--top-drivers requires a positive integer.");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                    topDriversOverride = v;
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
                    if (!TryParseNonNegativeDouble(args[++i], out double v))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("--min-display-mb requires a non-negative number (e.g. 2, 0.5, or 0 to disable filtering).");
                        Console.ResetColor();
                        WaitForKeyIfInteractive();
                        return 1;
                    }
                    minDisplayMbOverride = v;
                }
                else if (a == "--per-process-ws-budget-mb" && i + 1 < args.Length)
                {
                    if (!TryParseNonNegativeDouble(args[++i], out double v)) { BadBudgetArg(a); return 1; }
                    perProcessWsMbOverride = v;
                }
                else if (a == "--per-process-va-budget-mb" && i + 1 < args.Length)
                {
                    if (!TryParseNonNegativeDouble(args[++i], out double v)) { BadBudgetArg(a); return 1; }
                    perProcessVaMbOverride = v;
                }
                else if (a == "--per-driver-pool-budget-mb" && i + 1 < args.Length)
                {
                    if (!TryParseNonNegativeDouble(args[++i], out double v)) { BadBudgetArg(a); return 1; }
                    perDriverPoolMbOverride = v;
                }
                else if (a == "--per-driver-code-budget-mb" && i + 1 < args.Length)
                {
                    if (!TryParseNonNegativeDouble(args[++i], out double v)) { BadBudgetArg(a); return 1; }
                    perDriverCodeMbOverride = v;
                }
                else if (a == "--total-user-ws-budget-mb" && i + 1 < args.Length)
                {
                    if (!TryParseNonNegativeDouble(args[++i], out double v)) { BadBudgetArg(a); return 1; }
                    totalUserWsMbOverride = v;
                }
                else if (a == "--total-driver-pool-budget-mb" && i + 1 < args.Length)
                {
                    if (!TryParseNonNegativeDouble(args[++i], out double v)) { BadBudgetArg(a); return 1; }
                    totalDriverPoolMbOverride = v;
                }
                else if (a == "--total-driver-code-budget-mb" && i + 1 < args.Length)
                {
                    if (!TryParseNonNegativeDouble(args[++i], out double v)) { BadBudgetArg(a); return 1; }
                    totalDriverCodeMbOverride = v;
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

            // --write-default-profiles short-circuit. Emit the canonical
            // defaults file (either to the user-supplied path or next to
            // the .exe) and exit. Lets OEMs regenerate the file after
            // editing it incorrectly without having to copy from source.
            if (writeDefaultProfilesAndExit)
            {
                string writePath = !string.IsNullOrEmpty(profilesFileOverride)
                    ? profilesFileOverride
                    : Path.Combine(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory(), BudgetProfileFile.DefaultFileName);
                try
                {
                    BudgetProfileFile.Save(BudgetProfileFile.BuildDefaults(), writePath);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Wrote default budget profiles to: {writePath}");
                    Console.ResetColor();
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Failed to write '{writePath}': {ex.Message}");
                    Console.ResetColor();
                    return 1;
                }
            }

            // Load (or seed) the user-editable budget profiles JSON shipped
            // next to the .exe. Precedence: --profiles-file > exe-dir file >
            // cwd file > built-in defaults. When the file is absent at the
            // exe-dir location we seed it with the defaults so OEMs always
            // find a self-documenting template next to MemoryUsageChecker.exe.
            string profilesFilePath = BudgetProfileFile.ResolvePath(profilesFileOverride);
            BudgetProfileFile.FileModel profilesFile = null;
            string profilesFileSeededAt = null;
            if (profilesFilePath == null && string.IsNullOrEmpty(profilesFileOverride))
            {
                string exeDir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string seedPath = Path.Combine(exeDir, BudgetProfileFile.DefaultFileName);
                    try
                    {
                        if (BudgetProfileFile.EnsureFile(seedPath))
                        {
                            profilesFileSeededAt = seedPath;
                            profilesFilePath = seedPath;
                        }
                    }
                    catch
                    {
                        // Seeding is best-effort - fall back to built-in defaults silently.
                    }
                }
            }
            if (profilesFilePath != null)
            {
                try
                {
                    profilesFile = BudgetProfileFile.Load(profilesFilePath);
                    // Apply the verdict-threshold percentages globally so
                    // Evaluate() agrees with the per-row coloring everywhere.
                    if (profilesFile.VerdictThresholds != null)
                    {
                        if (profilesFile.VerdictThresholds.WarnAtPercent > 0)
                            BudgetProfile.WarnAtPercent = profilesFile.VerdictThresholds.WarnAtPercent;
                        if (profilesFile.VerdictThresholds.FailAtPercent > 0)
                            BudgetProfile.FailAtPercent = profilesFile.VerdictThresholds.FailAtPercent;
                    }
                    // If the user didn't specify --profile, honor the file's
                    // defaultProfile field; otherwise use what was requested
                    // on the command line.
                    string tierToLoad = profileExplicit
                        ? (profileNameRequested ?? profile.Name)
                        : (string.IsNullOrEmpty(profilesFile.DefaultProfile) ? profile.Name : profilesFile.DefaultProfile);
                    BudgetProfile fromFile = BudgetProfileFile.ToProfile(profilesFile, tierToLoad);
                    if (fromFile != null) profile = fromFile;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Failed to load '{profilesFilePath}': {ex.Message}");
                    Console.Error.WriteLine("Falling back to built-in defaults. Run with --write-default-profiles to regenerate.");
                    Console.ResetColor();
                }
            }

            // Apply per-flag overrides on top of the resolved tier. Any
            // override flips the profile name to "custom" so the JSON
            // sidecar and the executive summary honestly reflect that the
            // budgets are no longer the canonical preset.
            profile = ApplyOverrides(
                profile,
                profileExplicit,
                topProcessesOverride, topDriversOverride,
                minDisplayMbOverride,
                perProcessWsMbOverride, perProcessVaMbOverride,
                perDriverPoolMbOverride, perDriverCodeMbOverride,
                totalUserWsMbOverride, totalDriverPoolMbOverride, totalDriverCodeMbOverride);

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
            Log.Info($"Budget profile   : {profile.Name}  (top-processes={profile.TopProcesses}, top-drivers={profile.TopDrivers})");
            if (profilesFilePath != null)
            {
                Log.Info($"  Profiles file              : {profilesFilePath}{(profilesFileSeededAt != null ? "  (seeded with defaults this run)" : string.Empty)}");
            }
            else
            {
                Log.Info($"  Profiles file              : (none — using built-in defaults; create '{BudgetProfileFile.DefaultFileName}' next to the .exe to customize)");
            }
            Log.Info($"  Verdict thresholds         : Warn>={BudgetProfile.WarnAtPercent}% of budget, Fail>{BudgetProfile.FailAtPercent}% of budget");
            Log.Info($"  Per-process WS budget       : {profile.PerProcessWorkingSetBudgetBytes / 1048576.0:F0} MB");
            Log.Info($"  Per-process VA-Imp budget   : {profile.PerProcessVirtualAllocBudgetBytes / 1048576.0:F0} MB");
            Log.Info($"  Per-driver pool budget      : {profile.PerDriverPoolBudgetBytes / 1048576.0:F0} MB");
            Log.Info($"  Per-driver code budget      : {profile.PerDriverCodeBudgetBytes / 1048576.0:F0} MB");
            Log.Info($"  Total user-mode WS budget   : {profile.TotalUserWorkingSetBudgetBytes / 1048576.0:F0} MB");
            Log.Info($"  Total driver pool budget    : {profile.TotalDriverPoolBudgetBytes / 1048576.0:F0} MB");
            Log.Info($"  Total driver code budget    : {profile.TotalDriverCodeBudgetBytes / 1048576.0:F0} MB");
            Log.Info($"Parsed --top-stacks : {topStacks}");
            Log.Info($"Parsed --min-display-mb : {profile.MinDisplayBytes / 1048576.0:F2}");
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
                },
                Budget = new JsonReport.BudgetInfo
                {
                    Profile = profile.Name,
                    TopProcesses = profile.TopProcesses,
                    TopDrivers = profile.TopDrivers,
                    PerProcessWorkingSetBudgetBytes = profile.PerProcessWorkingSetBudgetBytes,
                    PerProcessVirtualAllocBudgetBytes = profile.PerProcessVirtualAllocBudgetBytes,
                    PerDriverPoolBudgetBytes = profile.PerDriverPoolBudgetBytes,
                    PerDriverCodeBudgetBytes = profile.PerDriverCodeBudgetBytes,
                    TotalUserWorkingSetBudgetBytes = profile.TotalUserWorkingSetBudgetBytes,
                    TotalDriverPoolBudgetBytes = profile.TotalDriverPoolBudgetBytes,
                    TotalDriverCodeBudgetBytes = profile.TotalDriverCodeBudgetBytes,
                    MinDisplayBytes = profile.MinDisplayBytes,
                }
            };

            int exitCode;
            try
            {
                using var output = new OutputWriter(resultPath)
                {
                    TopN = Math.Max(profile.TopProcesses, profile.TopDrivers),
                    TopProcesses = profile.TopProcesses,
                    TopDrivers = profile.TopDrivers,
                    TopStacks = topStacks,
                    MinDisplayBytes = profile.MinDisplayBytes,
                    NoSymbols = noSymbols,
                    Budget = profile,
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
                    output.WriteHeader($"Budget Profile:\t{profile.Name}  (per-process WS {profile.PerProcessWorkingSetBudgetBytes / 1048576.0:F0} MB, per-driver pool {profile.PerDriverPoolBudgetBytes / 1048576.0:F0} MB; image total {profile.TotalUserWorkingSetBudgetBytes / 1048576.0:F0} MB user-mode + {profile.TotalDriverPoolBudgetBytes / 1048576.0:F0} MB driver pool + {profile.TotalDriverCodeBudgetBytes / 1048576.0:F0} MB driver code)");
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
        /// Default per-user PDB download cache. Used by the symsrv layer
        /// as a downstream store for any <c>srv*</c> elements that don't
        /// already carry one. Persistent across runs so re-analysis of
        /// the same trace skips the network download. Kept at the legacy
        /// <c>%LOCALAPPDATA%\SymbolCache</c> path so users who already have
        /// a populated cache don't lose it.
        /// </summary>
        private static readonly string DefaultPdbCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SymbolCache");

        /// <summary>
        /// Per-user fallback TraceProcessing <c>.symcache</c> directory used
        /// only when the conventional <c>C:\SymCache</c> location is not
        /// writable (non-admin sessions). The convention is to share
        /// <c>C:\SymCache</c> with WPA / PerfView so caches are reused
        /// cross-tool; this fallback exists so non-admin sessions still get
        /// a persistent symcache across runs instead of silently dropping
        /// the data into a temp folder.
        /// </summary>
        private static readonly string FallbackSymCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemoryUsageChecker", "SymCache");

        /// <summary>
        /// Resolves the symbol path with the documented precedence
        /// (<c>--symbols</c> &gt; <c>_NT_SYMBOL_PATH</c> &gt; Microsoft Public
        /// Symbol Server with a per-user cache) and triggers symbol loading.
        /// When <c>_NT_SYMBOL_PATH</c> is set without a downstream
        /// <c>cache*</c> directive, one is prepended so downloaded PDBs
        /// persist across runs. The TraceProcessing <c>.symcache</c> path is
        /// taken from <c>_NT_SYMCACHE_PATH</c> when set, otherwise it prefers
        /// the conventional <c>C:\SymCache</c> (shared with WPA) and falls
        /// back to a per-user directory when that path is not writable.
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

            string symCacheDir;
            string symCacheSource;
            string envSymCache = Environment.GetEnvironmentVariable("_NT_SYMCACHE_PATH");
            if (!string.IsNullOrWhiteSpace(envSymCache))
            {
                symCacheDir = envSymCache.Trim();
                symCacheSource = "_NT_SYMCACHE_PATH";
            }
            else if (TryEnsureWritableDirectory(@"C:\SymCache"))
            {
                symCacheDir = @"C:\SymCache";
                symCacheSource = "default (shared with WPA/PerfView)";
            }
            else
            {
                symCacheDir = FallbackSymCacheDir;
                symCacheSource = "per-user fallback (C:\\SymCache not writable; needs admin)";
            }
            try { Directory.CreateDirectory(symCacheDir); }
            catch (Exception ex)
            {
                Log.Warn($"Could not create SymCache directory '{symCacheDir}': {ex.Message}. Falling back to per-user path.");
                symCacheDir = FallbackSymCacheDir;
                symCacheSource = "per-user fallback (initial path failed)";
                Directory.CreateDirectory(symCacheDir);
            }
            ISymCachePath symCachePath = new RawSymCachePath(symCacheDir);

            ISymbolPath symbolPath;
            string symbolSource;
            string downstreamPdbCacheForStats = null;
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
                    Directory.CreateDirectory(DefaultPdbCacheDir);
                    string effective = EnsureDownstreamCache(envPath, DefaultPdbCacheDir, out bool augmented);
                    symbolPath = new SymbolPath(effective);
                    symbolSource = augmented
                        ? $"_NT_SYMBOL_PATH (auto-prepended 'cache*{DefaultPdbCacheDir};' so downloads persist): {effective}"
                        : $"_NT_SYMBOL_PATH: {envPath}";
                    downstreamPdbCacheForStats = augmented ? DefaultPdbCacheDir : ExtractFirstDownstreamCache(envPath);
                }
                else
                {
                    Directory.CreateDirectory(DefaultPdbCacheDir);
                    string defaultPath = $"SRV*{DefaultPdbCacheDir}*https://msdl.microsoft.com/download/symbols";
                    symbolPath = new SymbolPath(defaultPath);
                    symbolSource = $"Microsoft Public Symbol Server (cache: {DefaultPdbCacheDir})";
                    downstreamPdbCacheForStats = DefaultPdbCacheDir;
                }
            }

            output.WriteInfo($"Symbol path: {symbolSource}");
            output.WriteInfo($"SymCache dir ({symCacheSource}): {symCacheDir}");
            (long files, long bytes) symCacheStats = QuickCacheStats(symCacheDir);
            (long files, long bytes) pdbCacheStats = QuickCacheStats(downstreamPdbCacheForStats);
            if (symCacheStats.files > 0 || pdbCacheStats.files > 0)
            {
                string pdbPart = downstreamPdbCacheForStats != null
                    ? $", {pdbCacheStats.files:N0} PDB files ({FormatMiB(pdbCacheStats.bytes)})"
                    : "";
                output.WriteInfo($"  Cache pre-populated: {symCacheStats.files:N0} symcache files ({FormatMiB(symCacheStats.bytes)}){pdbPart}. Matched symbols will be reused (no re-download).");
            }
            else
            {
                output.WriteInfo("  Caches empty: first-run download expected. Subsequent runs will reuse what's downloaded here.");
            }
            Log.Info($"Symbol source resolved to: {symbolSource}");
            Log.Info($"SymCache resolved to: {symCacheDir} (source: {symCacheSource})");
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
                        pendingSymbols.Result.LoadSymbolsForConsoleAsync(symCachePath, symbolPath).GetAwaiter().GetResult();
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
        /// Returns <paramref name="path"/> unchanged if it already declares a
        /// downstream cache (either a <c>cache*</c> element or an
        /// <c>srv*&lt;localpath&gt;*&lt;url&gt;</c> element). Otherwise
        /// prepends <c>cache*&lt;defaultCacheDir&gt;;</c> so any
        /// <c>srv*</c> downloads get written to a persistent local store and
        /// reused on subsequent runs.
        /// </summary>
        private static string EnsureDownstreamCache(string path, string defaultCacheDir, out bool augmented)
        {
            if (HasDownstreamCache(path))
            {
                augmented = false;
                return path;
            }
            augmented = true;
            return $"cache*{defaultCacheDir};{path}";
        }

        private static bool HasDownstreamCache(string path)
        {
            return ExtractFirstDownstreamCache(path) != null;
        }

        /// <summary>
        /// Returns the first downstream cache directory declared in a
        /// symsrv-format path string, or <c>null</c> when no cache is
        /// declared. Used both to detect cache presence in
        /// <see cref="HasDownstreamCache"/> and to surface the actual cache
        /// directory in the startup "cache pre-populated" log line so the
        /// user can see what's being reused.
        /// </summary>
        private static string ExtractFirstDownstreamCache(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string raw in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string element = raw.Trim();
                if (element.Length == 0) continue;
                if (element.StartsWith("cache*", StringComparison.OrdinalIgnoreCase))
                {
                    string after = element.Substring("cache*".Length);
                    int star = after.IndexOf('*');
                    return star >= 0 ? after.Substring(0, star) : after;
                }
                if (element.StartsWith("srv*", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = element.Split('*');
                    if (parts.Length >= 3 && !LooksLikeUrl(parts[1]))
                    {
                        return parts[1];
                    }
                }
            }
            return null;
        }

        private static bool LooksLikeUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Tests whether <paramref name="dir"/> can be created and written
        /// to by the current process. Used to decide whether to use the
        /// conventional <c>C:\SymCache</c> location (admin sessions, shared
        /// with WPA) or fall back to a per-user directory (non-admin).
        /// </summary>
        private static bool TryEnsureWritableDirectory(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, $".muc_write_probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Best-effort file count + byte total for a cache directory. Returns
        /// (0, 0) when the directory is missing or <paramref name="dir"/> is
        /// null; (-1, -1) on enumeration failure. Capped at 200 000 files /
        /// 60 s wall to keep startup fast even against pathologically large
        /// caches.
        /// </summary>
        private static (long files, long bytes) QuickCacheStats(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return (0, 0);
            try
            {
                if (!Directory.Exists(dir)) return (0, 0);
                long files = 0;
                long bytes = 0;
                Stopwatch sw = Stopwatch.StartNew();
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(f).Length; } catch { /* deleted mid-scan */ }
                    files++;
                    if (files >= 200_000 || sw.Elapsed.TotalSeconds > 60) break;
                }
                return (files, bytes);
            }
            catch
            {
                return (-1, -1);
            }
        }

        private static string FormatMiB(long bytes)
        {
            if (bytes < 0) return "?";
            double mib = bytes / 1024.0 / 1024.0;
            if (mib >= 1024.0) return $"{mib / 1024.0:F2} GiB";
            return $"{mib:F2} MiB";
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
            Console.Error.WriteLine("Usage: MemoryUsageChecker.exe [<trace.etl>] [--profile 16gb|8gb|4gb] [--top-processes N] [--top-drivers N]");
            Console.Error.WriteLine("                              [--top-stacks N] [--min-display-mb V] [--per-process-ws-budget-mb V]");
            Console.Error.WriteLine("                              [--per-process-va-budget-mb V] [--per-driver-pool-budget-mb V]");
            Console.Error.WriteLine("                              [--per-driver-code-budget-mb V] [--total-user-ws-budget-mb V]");
            Console.Error.WriteLine("                              [--total-driver-pool-budget-mb V] [--total-driver-code-budget-mb V]");
            Console.Error.WriteLine("                              [--profiles-file <path>] [--write-default-profiles]");
            Console.Error.WriteLine("                              [--symbols <path>] [--no-symbols]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("When <trace.etl> is omitted (e.g. when MemoryUsageChecker.exe is launched");
            Console.Error.WriteLine("by double-clicking it in Explorer), the tool auto-selects the most recently");
            Console.Error.WriteLine("modified *.etl file located in the same folder as the .exe, preferring");
            Console.Error.WriteLine("MemoryUsage-Trace.etl (the canonical name produced by MemoryUsageTrace.cmd).");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Budget profile (use the preset, then override individual knobs if you want):");
            Console.Error.WriteLine("  --profile 16gb|8gb|4gb   Pick a calibrated tier for the target device class.");
            Console.Error.WriteLine("                           Default is '8gb' (override via the defaultProfile field");
            Console.Error.WriteLine("                           in MemoryUsageChecker.profiles.json).");
            Console.Error.WriteLine();
            Console.Error.WriteLine("                       16gb tier defaults:           8gb tier defaults:           4gb tier defaults:");
            Console.Error.WriteLine("                         Per-process WS    400 MB      Per-process WS    200 MB      Per-process WS    100 MB");
            Console.Error.WriteLine("                         Per-process VA    200 MB      Per-process VA    100 MB      Per-process VA     50 MB");
            Console.Error.WriteLine("                         Per-driver pool    10 MB      Per-driver pool     5 MB      Per-driver pool     2 MB");
            Console.Error.WriteLine("                         Per-driver code     4 MB      Per-driver code     2 MB      Per-driver code     1 MB");
            Console.Error.WriteLine("                         Total user WS   3072 MB      Total user WS   1536 MB      Total user WS    750 MB");
            Console.Error.WriteLine("                         Total driver pool 512 MB      Total driver pool 256 MB      Total driver pool 128 MB");
            Console.Error.WriteLine("                         Total driver code 128 MB      Total driver code  64 MB      Total driver code  32 MB");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Editable defaults file (shipped next to the .exe; loaded automatically on every run):");
            Console.Error.WriteLine("  --profiles-file <path>    Override the path to MemoryUsageChecker.profiles.json.");
            Console.Error.WriteLine("                            Default lookup: <exe folder>\\MemoryUsageChecker.profiles.json,");
            Console.Error.WriteLine("                            then the current directory. If absent the file is seeded with");
            Console.Error.WriteLine("                            built-in defaults so an OEM can edit it without source access.");
            Console.Error.WriteLine("  --write-default-profiles  Write a fresh defaults file to the path above and exit.");
            Console.Error.WriteLine("                            Use this to restore the file after a bad edit.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Display + filter:");
            Console.Error.WriteLine("  --top-processes N   User-mode process rows in each Top-N table (default 15).");
            Console.Error.WriteLine("  --top-drivers N     Driver rows in each Top-N table (default 10).");
            Console.Error.WriteLine("  --top N             Back-compat alias: applies N to BOTH processes and drivers.");
            Console.Error.WriteLine("  --top-stacks N      Per-row inner stack drill-down shown only for the first N");
            Console.Error.WriteLine("                      outer rows (default 10; 0 disables all drill-downs).");
            Console.Error.WriteLine("  --min-display-mb V  Hide rows below V MiB (default 4 on 16gb, 2 on 8gb, 1 on 4gb).");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Per-item & total budget overrides (override the profile preset; switches profile to 'custom'):");
            Console.Error.WriteLine("  --per-process-ws-budget-mb V    Per-process Active working-set ceiling (Exercise 1).");
            Console.Error.WriteLine("  --per-process-va-budget-mb V    Per-process VirtualAlloc Impacting ceiling (Exercise 2).");
            Console.Error.WriteLine("  --per-driver-pool-budget-mb V   Per-driver NonPaged-pool Impacting ceiling (Exercise 3A).");
            Console.Error.WriteLine("  --per-driver-code-budget-mb V   Per-driver code-resident footprint ceiling (Exercise 3B).");
            Console.Error.WriteLine("  --total-user-ws-budget-mb V     Whole-image user-mode WS ceiling (Exercise 1 PASS/FAIL).");
            Console.Error.WriteLine("  --total-driver-pool-budget-mb V Whole-image driver NP-pool ceiling (Exercise 3A PASS/FAIL).");
            Console.Error.WriteLine("  --total-driver-code-budget-mb V Whole-image driver code ceiling (Exercise 3B PASS/FAIL).");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Symbols:");
            Console.Error.WriteLine("  --symbols <path>    Override the symbol search path passed to the EventTracing SDK.");
            Console.Error.WriteLine("                      Used as-is; assumed to already declare a downstream cache.");
            Console.Error.WriteLine("  --no-symbols        Skip symbol load entirely. Per-row stack drill-downs in");
            Console.Error.WriteLine("                      Exercises 2 and 3 are also suppressed because raw addresses");
            Console.Error.WriteLine("                      are not actionable; outer Top-N tables and the executive");
            Console.Error.WriteLine("                      summary are still produced.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Color-coded improvement direction (printed on every per-item and per-category line):");
            Console.Error.WriteLine("  Red    '✗'  Row / total exceeds its budget — must be shrunk to fit the active tier.");
            Console.Error.WriteLine("  Yellow '!'  Row / total is at 80–100% of its budget — watch / reduce if possible.");
            Console.Error.WriteLine("  Green  '✓'  Row / total is healthy (well under budget). Image-fit PASS uses the same glyph.");
            Console.Error.WriteLine("  (Warn/Fail percentages above are tunable in the profiles.json 'verdictThresholds' block.)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Symbol caching (so repeat runs don't re-download):");
            Console.Error.WriteLine("  PDB cache (default): %LOCALAPPDATA%\\SymbolCache");
            Console.Error.WriteLine("    Used when neither --symbols nor _NT_SYMBOL_PATH is set. When _NT_SYMBOL_PATH");
            Console.Error.WriteLine("    is set without a 'cache*<dir>' (or 'srv*<localpath>*<url>') element, this");
            Console.Error.WriteLine("    path is auto-prepended as a cache* directive so downloads persist.");
            Console.Error.WriteLine("  SymCache (default):  C:\\SymCache (shared with WPA), or %LOCALAPPDATA%\\");
            Console.Error.WriteLine("    MemoryUsageChecker\\SymCache as a per-user fallback when C:\\SymCache is not");
            Console.Error.WriteLine("    writable (non-admin). Override with _NT_SYMCACHE_PATH.");
            Console.Error.WriteLine("  Both paths and their pre-existing sizes are printed at startup so you can");
            Console.Error.WriteLine("  see cache hits.");
        }

        /// <summary>
        /// Parses a non-negative invariant-culture <see cref="double"/>.
        /// Wraps the <see cref="double.TryParse(string, System.Globalization.NumberStyles, IFormatProvider, out double)"/>
        /// boilerplate so the per-budget flag handlers stay one-line.
        /// </summary>
        private static bool TryParseNonNegativeDouble(string raw, out double value)
        {
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0)
            {
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>
        /// Emits a uniform "this flag requires a non-negative MB value"
        /// error to stderr. Centralised so every per-budget flag handler
        /// produces the same wording (and the caller stays a 1-liner).
        /// </summary>
        private static void BadBudgetArg(string flag)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"{flag} requires a non-negative number (in MB, e.g. 200 or 0.5).");
            Console.ResetColor();
            WaitForKeyIfInteractive();
        }

        /// <summary>
        /// Materialises the effective <see cref="BudgetProfile"/> by
        /// layering each command-line override onto the resolved preset.
        /// If any override is present, the returned profile's
        /// <see cref="BudgetProfile.Name"/> is flipped to <c>"custom"</c>
        /// so downstream consumers (executive summary, JSON sidecar)
        /// don't claim a canonical tier when the budgets have been
        /// modified.
        /// </summary>
        private static BudgetProfile ApplyOverrides(
            BudgetProfile baseProfile,
            bool profileExplicit,
            int? topProcesses,
            int? topDrivers,
            double? minDisplayMb,
            double? perProcWsMb,
            double? perProcVaMb,
            double? perDrvPoolMb,
            double? perDrvCodeMb,
            double? totalUserWsMb,
            double? totalDrvPoolMb,
            double? totalDrvCodeMb)
        {
            bool anyOverride =
                topProcesses.HasValue || topDrivers.HasValue || minDisplayMb.HasValue ||
                perProcWsMb.HasValue || perProcVaMb.HasValue ||
                perDrvPoolMb.HasValue || perDrvCodeMb.HasValue ||
                totalUserWsMb.HasValue || totalDrvPoolMb.HasValue || totalDrvCodeMb.HasValue;
            if (!anyOverride) return baseProfile;

            // Preserve the preset name when only --top-processes / --top-drivers
            // were overridden, because those don't change the *budget* — they
            // only change how many rows are displayed. Any actual budget
            // override flips the name to "custom".
            bool budgetTouched =
                perProcWsMb.HasValue || perProcVaMb.HasValue ||
                perDrvPoolMb.HasValue || perDrvCodeMb.HasValue ||
                totalUserWsMb.HasValue || totalDrvPoolMb.HasValue || totalDrvCodeMb.HasValue ||
                minDisplayMb.HasValue;

            string newName = budgetTouched
                ? (profileExplicit ? $"custom (base: {baseProfile.Name})" : "custom")
                : baseProfile.Name;

            long MbToBytes(double mb) => (long)(mb * 1024 * 1024);

            return baseProfile with
            {
                Name = newName,
                TopProcesses = topProcesses ?? baseProfile.TopProcesses,
                TopDrivers = topDrivers ?? baseProfile.TopDrivers,
                MinDisplayBytes = minDisplayMb.HasValue ? MbToBytes(minDisplayMb.Value) : baseProfile.MinDisplayBytes,
                PerProcessWorkingSetBudgetBytes = perProcWsMb.HasValue ? MbToBytes(perProcWsMb.Value) : baseProfile.PerProcessWorkingSetBudgetBytes,
                PerProcessVirtualAllocBudgetBytes = perProcVaMb.HasValue ? MbToBytes(perProcVaMb.Value) : baseProfile.PerProcessVirtualAllocBudgetBytes,
                PerDriverPoolBudgetBytes = perDrvPoolMb.HasValue ? MbToBytes(perDrvPoolMb.Value) : baseProfile.PerDriverPoolBudgetBytes,
                PerDriverCodeBudgetBytes = perDrvCodeMb.HasValue ? MbToBytes(perDrvCodeMb.Value) : baseProfile.PerDriverCodeBudgetBytes,
                TotalUserWorkingSetBudgetBytes = totalUserWsMb.HasValue ? MbToBytes(totalUserWsMb.Value) : baseProfile.TotalUserWorkingSetBudgetBytes,
                TotalDriverPoolBudgetBytes = totalDrvPoolMb.HasValue ? MbToBytes(totalDrvPoolMb.Value) : baseProfile.TotalDriverPoolBudgetBytes,
                TotalDriverCodeBudgetBytes = totalDrvCodeMb.HasValue ? MbToBytes(totalDrvCodeMb.Value) : baseProfile.TotalDriverCodeBudgetBytes,
            };
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
