// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;

namespace MemoryUsageChecker
{
    internal static class Exercise1_ResidentSet
    {
        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<IResidentSetDataSource> pendingResidentSet)
        {
            output.WriteHeader("=== Exercise 1: Resident Set ===");
            output.WriteSkipped("[not yet implemented]");
        }
    }
}
