// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Processes;
using System;

namespace CheckForAppCrash
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage CheckForAppCrash <trace.etl>");
                return -1;
            }

            string tracePath = args[0];

            using (ITraceProcessor trace = TraceProcessor.Create(tracePath))
            {
                IPendingResult<IProcessDataSource> pendingProcesses = trace.UseProcesses();

                trace.Process();

                IProcessDataSource processData = pendingProcesses.Result;

                foreach (IProcess process in processData.Processes)
                {
                    if (string.Equals("werfault.exe", process.ImageName, StringComparison.OrdinalIgnoreCase))
                    {
                        return 1;
                    }
                }
            }

            return 0;
        }
    }
}
