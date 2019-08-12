// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Symbols;
using System;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: FindZombieProcess.exe <trace.etl>");
            return 1;
        }

        string tracePath = args[0];

        TraceProcessorSettings settings = new TraceProcessorSettings { AllowLostEvents = true };

        using (ITraceProcessor trace = TraceProcessor.Create(tracePath, settings))
        {
            IPendingResult<IHandleDataSource> pendingHandleData = trace.UseHandles();
            IPendingResult<ISymbolDataSource> pendingSymbolData = trace.UseSymbols();

            trace.Process();

            IHandleDataSource handleData = pendingHandleData.Result;
            ISymbolDataSource symbolData = pendingSymbolData.Result;

            symbolData.LoadSymbolsForConsoleAsync(SymCachePath.Automatic, SymbolPath.Automatic).GetAwaiter().GetResult();

            foreach (IProcessHandle processHandle in handleData.ProcessHandles)
            {
                // Zombie processes are processes which have exited but which still have a running process holding a handle to them
                if (processHandle.Process != null && !processHandle.CloseTime.HasValue
                    && processHandle.Process.ExitTime.HasValue)
                {
                    string owningProcessName = processHandle.Owner?.ImageName ?? "Unknown";
                    string targetProcessName = processHandle.Process?.ImageName ?? "Unknown";
                    Console.WriteLine($"Owning process: {owningProcessName} has handle to: {targetProcessName}");
                }
            }

            return 0;
        }
    }
}
