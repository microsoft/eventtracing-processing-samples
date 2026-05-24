// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;

namespace MemoryUsageChecker
{
    internal static class Exercise2_VirtualAllocHeap
    {
        public static void Run(
            OutputWriter output,
            ITraceMetadata metadata,
            IPendingResult<IProcessDataSource> pendingProcesses,
            IPendingResult<ICommitDataSource> pendingCommit,
            IPendingResult<IHeapSnapshotDataSource> pendingHeap)
        {
            output.WriteHeader("=== Exercise 2: VirtualAlloc + Heap ===");
            output.WriteSkipped("[not yet implemented]");
        }
    }
}
