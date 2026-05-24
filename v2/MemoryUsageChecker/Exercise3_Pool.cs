// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;

namespace MemoryUsageChecker
{
    internal static class Exercise3_Pool
    {
        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<IPoolAllocationDataSource> pendingPool,
            IPendingResult<IResidentSetDataSource> pendingResidentSet)
        {
            output.WriteHeader("=== Exercise 3: Pool ===");
            output.WriteSkipped("[not yet implemented]");
        }
    }
}
