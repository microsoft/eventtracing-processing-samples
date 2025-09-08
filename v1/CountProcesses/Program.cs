// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Processes;
using System;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: CountProcesses.exe <trace.etl>");
            return 1;
        }

        string tracePath = args[0];
        Run(tracePath);
        return 0;
    }

    static void Run(string tracePath)
    {
        using (ITraceProcessor trace = TraceProcessor.Create(tracePath))
        {
            IPendingResult<IProcessDataSource> pendingProcessData = trace.UseProcesses();

            trace.Process();

            IProcessDataSource processData = pendingProcessData.Result;

            Console.WriteLine(processData.Processes.Count);
        }
    }
}
