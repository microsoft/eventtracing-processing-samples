// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using System;
using System.Collections.Generic;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: OutstandingHandleCountByProcess.exe <trace.etl>");
            return 1;
        }

        string tracePath = args[0];
        TraceProcessorSettings settings = new TraceProcessorSettings { AllowLostEvents = true };

        using (ITraceProcessor trace = TraceProcessor.Create(tracePath, settings))
        {
            IPendingResult<IHandleDataSource> pendingHandleData = trace.UseHandles();
            
            trace.Process();

            IHandleDataSource handleData = pendingHandleData.Result;
            // Dictionary Key is Owning Process Name
            // Dictionary Value is a struct containing outstanding handle counts by type (Process Handles & Other Handles)
            Dictionary<string, HandleCounts> outstandingHandleCounts = new Dictionary<string, HandleCounts>();

            foreach (IProcessHandle processHandle in handleData.ProcessHandles)
            {
                if (!processHandle.CloseTime.HasValue)
                {
                    string owningProcessName = processHandle.Owner?.ImageName ?? "Unknown";

                    if (outstandingHandleCounts.ContainsKey(owningProcessName))
                    {
                        HandleCounts handleCounts = outstandingHandleCounts[owningProcessName];
                        ++handleCounts.ProcessHandleCount;
                        outstandingHandleCounts[owningProcessName] = handleCounts;
                    }
                    else
                    {
                        outstandingHandleCounts[owningProcessName] = new HandleCounts(1, 0);
                    }
                }
            }

            foreach (IHandle otherHandle in handleData.OtherHandles)
            {
                if (!otherHandle.CloseTime.HasValue)
                {
                    string owningProcessName = otherHandle.Owner?.ImageName ?? "Unknown";

                    if (outstandingHandleCounts.ContainsKey(owningProcessName))
                    {
                        HandleCounts handleCounts = outstandingHandleCounts[owningProcessName];
                        ++handleCounts.OtherHandleCount;
                        outstandingHandleCounts[owningProcessName] = handleCounts;
                    }
                    else
                    {
                        outstandingHandleCounts[owningProcessName] = new HandleCounts(0, 1);
                    }
                }
            }

            foreach (string process in outstandingHandleCounts.Keys)
            {
                int openProcessHandleCount = outstandingHandleCounts[process].ProcessHandleCount;
                int openOtherHandleCount = outstandingHandleCounts[process].OtherHandleCount;
                Console.WriteLine($"Owning process: {process}");
                Console.WriteLine($"\t{openProcessHandleCount} outstanding Process Handles" +
                    $"\t{openOtherHandleCount} outstanding Other Handles");
            }

            return 0;
        }
    }

    private struct HandleCounts
    {
        public int ProcessHandleCount;
        public int OtherHandleCount;

        public HandleCounts (int processHandleCount, int otherHandleCount)
        {
            ProcessHandleCount = processHandleCount;
            OtherHandleCount = otherHandleCount;
        }
    }
}
