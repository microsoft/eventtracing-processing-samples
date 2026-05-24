// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using System;
using System.IO;

namespace MemoryUsageChecker
{
    internal class Program
    {
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

            string resultPath = Path.GetFullPath($"MemoryUsage_Result_{DateTime.Now:yyyyMMdd_HHmm}.txt");

            try
            {
                using var output = new OutputWriter(resultPath) { TopN = topN };

                ITraceProcessorSettings settings = new TraceProcessorSettings { AllowLostEvents = true };
                using ITraceProcessor trace = new TraceProcessorBuilder().WithSettings(settings).Build(tracePath);

                ITraceMetadata metadata = trace.UseMetadata();
                IPendingResult<IProcessDataSource>          pendingProcesses    = trace.UseProcesses();
                IPendingResult<IResidentSetDataSource>      pendingResidentSet  = trace.UseResidentSetData();
                IPendingResult<ICommitDataSource>           pendingCommit       = trace.UseCommitData();
                IPendingResult<IHeapSnapshotDataSource>     pendingHeap         = trace.UseHeapSnapshots();
                IPendingResult<IPoolAllocationDataSource>   pendingPool         = trace.UsePoolAllocations();
                IPendingResult<ISymbolDataSource>           pendingSymbols      = trace.UseSymbols();

                trace.Process();

                output.WriteHeader($"Trace Path:\t{tracePath}");
                output.WriteHeader($"Trace Start Time:\t{metadata.StartTime}");
                output.WriteHeader($"Trace Stop Time:\t{metadata.StopTime}");
                output.WriteHeader($"Result File:\t{resultPath}");
                output.WriteBlank();

                if (!noSymbols && pendingSymbols.HasResult)
                {
                    try
                    {
                        ISymbolPath symbolPath = symbolsOverride == null ? SymbolPath.Automatic : new SymbolPath(symbolsOverride);
                        pendingSymbols.Result.LoadSymbolsForConsoleAsync(SymCachePath.Automatic, symbolPath).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        output.WriteNotable($"Warning: failed to load symbols ({ex.Message}). Stacks will show [no symbols].");
                    }
                }
                else if (noSymbols)
                {
                    output.WriteInfo("Symbols disabled (--no-symbols). Stack frames will show [no symbols].");
                }

                output.WriteBlank();

                Exercise1_ResidentSet.Run(output, metadata, pendingProcesses, pendingResidentSet);
                output.WriteBlank();

                Exercise2_VirtualAllocHeap.Run(output, metadata, pendingProcesses, pendingCommit, pendingHeap);
                output.WriteBlank();

                Exercise3_Pool.Run(output, metadata, pendingProcesses, pendingPool, pendingResidentSet);
                output.WriteBlank();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"An error occurred: {ex.Message}");
                Console.ResetColor();
                return 2;
            }

            Console.WriteLine("Press any key to exit...");
            _ = Console.ReadKey();
            return 0;
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: MemoryUsageChecker.exe <trace.etl> [--top N] [--symbols <path>] [--no-symbols]");
        }
    }
}
