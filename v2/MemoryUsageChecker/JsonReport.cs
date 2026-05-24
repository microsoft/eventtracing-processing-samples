// © Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryUsageChecker
{
    /// <summary>
    /// Strongly-typed, AI-comparison-friendly schema for the JSON sidecar
    /// emitted next to <c>MemoryUsage_Result_*.txt</c> on every run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Design goals (see <c>plan.md</c> for the full rationale):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Stable, versioned</b> — top-level <see cref="SchemaVersion"/>
    ///         lets consumers branch on schema changes.</item>
    ///   <item><b>camelCase</b> property names via
    ///         <see cref="JsonNamingPolicy.CamelCase"/>.</item>
    ///   <item><b>Raw byte counts</b> for numeric values; MB rollups only where
    ///         the text output uses them, with a <c>...Megabytes</c> suffix
    ///         so an AI cannot mistake them for bytes.</item>
    ///   <item><b>Reusable identity blocks</b> (<see cref="ProcessIdentity"/>,
    ///         <see cref="DriverIdentity"/>) so two runs can be matched on
    ///         <c>imageName</c> + <c>version</c> (NOT <c>pid</c>, which is
    ///         run-local).</item>
    ///   <item><b>Null fields omitted</b> via
    ///         <see cref="JsonIgnoreCondition.WhenWritingNull"/> so diffs
    ///         stay small and unset sections collapse.</item>
    /// </list>
    /// </remarks>
    internal sealed class JsonReport
    {
        /// <summary>Schema version. Bumped only on breaking changes.</summary>
        public string SchemaVersion { get; set; } = "1.0";

        /// <summary>Information about the tool that produced this report.</summary>
        public ToolInfo Tool { get; set; } = new ToolInfo();

        /// <summary>Information about the trace the report was produced from.</summary>
        public TraceInfo Trace { get; set; } = new TraceInfo();

        /// <summary>Information about symbol resolution for this run.</summary>
        public SymbolsInfo Symbols { get; set; } = new SymbolsInfo();

        /// <summary>
        /// Presence + count of every ETL data source the analyzer reads.
        /// Lets consumers detect "missing data" cases (e.g. heap snapshots
        /// not collected) before they conclude a section is empty.
        /// </summary>
        public DataSourcesInfo DataSources { get; set; } = new DataSourcesInfo();

        /// <summary>Exercise 1 (resident set) analysis, when data was present.</summary>
        public Exercise1Section Exercise1ResidentSet { get; set; }

        /// <summary>Exercise 2 (VirtualAlloc + heap) analysis, when data was present.</summary>
        public Exercise2Section Exercise2VirtualAllocHeap { get; set; }

        /// <summary>Exercise 3 (pool + driver code footprint) analysis, when data was present.</summary>
        public Exercise3Section Exercise3Pool { get; set; }

        // ---- Top-level info blocks --------------------------------------

        /// <summary>Producer-tool identity (assembly version + runtime + host OS).</summary>
        public sealed class ToolInfo
        {
            /// <summary>Always <c>"MemoryUsageChecker"</c>.</summary>
            public string Name { get; set; } = "MemoryUsageChecker";
            /// <summary>Assembly version string (e.g. <c>"1.0.0.0"</c>).</summary>
            public string Version { get; set; }
            /// <summary>.NET runtime description (e.g. <c>".NET 10.0.8"</c>).</summary>
            public string Runtime { get; set; }
            /// <summary>OS that ran the analyzer (host), distinct from <see cref="TraceInfo.OsSummary"/> (trace target).</summary>
            public string HostOs { get; set; }
        }

        /// <summary>Trace-level metadata that's useful for both display and A/B matching.</summary>
        public sealed class TraceInfo
        {
            /// <summary>Absolute trace path the analyzer was invoked against.</summary>
            public string Path { get; set; }
            /// <summary>ETL file size in bytes.</summary>
            public long SizeBytes { get; set; }
            /// <summary>Trace start time, UTC ISO-8601.</summary>
            public DateTime? StartTimeUtc { get; set; }
            /// <summary>Trace stop time, UTC ISO-8601.</summary>
            public DateTime? StopTimeUtc { get; set; }
            /// <summary>Trace duration in seconds (Stop - Start).</summary>
            public double? DurationSeconds { get; set; }
            /// <summary>Human-readable OS summary as printed in the text header (e.g. <c>"Windows 11 Enterprise Insider Preview / Version 25H2 (OS Build 26220.8491) / Amd64 / Machine=Flow_Z13"</c>).</summary>
            public string OsSummary { get; set; }
            /// <summary>Best-effort raw OS version including the Update Build Revision (e.g. <c>"10.0.26220.8491"</c>), composed from <c>ISystemMetadata.BuildInfo</c> when available.</summary>
            public string OsVersion { get; set; }
            /// <summary>Best-effort OS product name from <c>BuildInfo.ProductName</c> (e.g. <c>"Windows 11 Enterprise Insider Preview"</c>).</summary>
            public string OsProductName { get; set; }
            /// <summary>Windows marketing release label (e.g. <c>"25H2"</c>) derived from the build number. Not stored in the ETL — best-effort lookup.</summary>
            public string OsDisplayVersion { get; set; }
            /// <summary>Best-effort raw OS build-lab string, when the SDK exposes it.</summary>
            public string OsBuildLab { get; set; }
            /// <summary>Best-effort architecture string, when the SDK exposes it.</summary>
            public string Architecture { get; set; }
            /// <summary>Best-effort host machine name, when the SDK exposes it.</summary>
            public string MachineName { get; set; }
        }

        /// <summary>Whether symbols were loaded and which symbol path resolved them.</summary>
        public sealed class SymbolsInfo
        {
            /// <summary>
            /// Human-readable description of the symbol source actually used
            /// (e.g. <c>"Microsoft Public Symbol Server (cache: ...)"</c>,
            /// <c>"_NT_SYMBOL_PATH: ..."</c>, <c>"--symbols (explicit): ..."</c>,
            /// or <c>"Disabled (--no-symbols)"</c>).
            /// </summary>
            public string Source { get; set; }
            /// <summary><c>true</c> iff symbol loading was attempted and completed without throwing.</summary>
            public bool Loaded { get; set; }
        }

        /// <summary>Presence + count for every ETL data source the analyzer reads.</summary>
        public sealed class DataSourcesInfo
        {
            public DataSourceState Processes { get; set; }
            public DataSourceState ResidentSet { get; set; }
            public DataSourceState Commit { get; set; }
            public DataSourceState Heap { get; set; }
            public DataSourceState Pool { get; set; }
            public DataSourceState Symbols { get; set; }
        }

        /// <summary>Did this data source return a result? If so, how many items?</summary>
        public sealed class DataSourceState
        {
            /// <summary><c>true</c> iff the underlying <c>IPendingResult&lt;T&gt;</c> had a result.</summary>
            public bool HasResult { get; set; }
            /// <summary>Count of items in the data source (snapshots, lifetimes, intervals, etc.); omitted when not applicable.</summary>
            public long? ItemCount { get; set; }
        }

        // ---- Reusable identity blocks ----------------------------------

        /// <summary>
        /// Process identity. <see cref="ImageName"/> + <see cref="Version"/>
        /// are stable across runs (and across devices); <see cref="Pid"/>
        /// is run-local and should NOT be used as a join key for A/B
        /// comparison.
        /// </summary>
        public sealed class ProcessIdentity
        {
            /// <summary>Image name (e.g. <c>"msedge.exe"</c>); always present.</summary>
            public string ImageName { get; set; }
            /// <summary>Process id at the time the trace was captured. Run-local; do not use as a join key.</summary>
            public int Pid { get; set; }
            /// <summary>Friendly name from PE <c>FileDescription</c> resource, when available.</summary>
            public string FriendlyName { get; set; }
            /// <summary>Version string from PE <c>FileVersion</c> resource (full UBR, e.g. <c>"10.0.26100.4061"</c>), when available.</summary>
            public string Version { get; set; }
        }

        /// <summary>
        /// Driver / image identity. <see cref="FileName"/> + <see cref="Version"/>
        /// are stable across runs.
        /// </summary>
        public sealed class DriverIdentity
        {
            /// <summary>Leaf file name (e.g. <c>"dxgkrnl.sys"</c>); always present.</summary>
            public string FileName { get; set; }
            /// <summary>Absolute file path, when known.</summary>
            public string Path { get; set; }
            /// <summary>Friendly name from PE <c>FileDescription</c> resource, when available.</summary>
            public string FriendlyName { get; set; }
            /// <summary>Version string from PE <c>FileVersion</c> resource, when available.</summary>
            public string Version { get; set; }
        }

        // ---- Ranked-stack block ----------------------------------------

        /// <summary>One bucket in a "top stacks" list: total bytes + count + sample frames.</summary>
        public sealed class RankedStack
        {
            public int Rank { get; set; }
            public long TotalBytes { get; set; }
            public long AllocationCount { get; set; }
            /// <summary>
            /// Up to the first 12 frames of a representative stack from this
            /// bucket, each formatted as <c>image!function+0xOFFSET</c>
            /// (or <c>image!0xRVA [no symbols]</c> when symbols missing).
            /// </summary>
            public List<string> Frames { get; set; } = new List<string>();
        }

        // ---- Exercise 1 -------------------------------------------------

        /// <summary>Exercise 1 root: one entry per resident-set snapshot in the trace.</summary>
        public sealed class Exercise1Section
        {
            public List<Exercise1Snapshot> Snapshots { get; set; } = new List<Exercise1Snapshot>();
        }

        public sealed class Exercise1Snapshot
        {
            /// <summary>Wall-clock time of the snapshot, UTC.</summary>
            public DateTime? TimestampUtc { get; set; }
            /// <summary>Trace-relative time in seconds.</summary>
            public double TraceRelativeSeconds { get; set; }
            /// <summary>Number of pages in this snapshot.</summary>
            public long PageCount { get; set; }
            /// <summary>MMList totals (Active / Standby / Modified / Free / Zeroed / Bad) in MB.</summary>
            public List<MmListBucket> MmListTotalsMegabytes { get; set; } = new List<MmListBucket>();
            /// <summary>Top-N processes by Active working set.</summary>
            public List<RankedProcessActive> TopProcessesByActiveWorkingSet { get; set; } = new List<RankedProcessActive>();
            /// <summary>Tail summary line, when the full list was truncated.</summary>
            public TailSummary TailProcesses { get; set; }
            /// <summary>Top-N driver-locked NonPaged contributors.</summary>
            public List<RankedDriverLocked> TopDriverLockedNonPaged { get; set; } = new List<RankedDriverLocked>();
            /// <summary>Tail summary line, when the full list was truncated.</summary>
            public TailSummary TailDriverLockedNonPaged { get; set; }
        }

        public sealed class MmListBucket
        {
            /// <summary>1-based rank in the descending-by-size list.</summary>
            public int Rank { get; set; }
            /// <summary>MMList type (Active / Standby / Modified / Free / Zeroed / Bad).</summary>
            public string List { get; set; }
            /// <summary>Raw bytes, authoritative.</summary>
            public long Bytes { get; set; }
            /// <summary>Bytes / 1024 / 1024 — derived; matches the text-output column.</summary>
            public double Megabytes { get; set; }
        }

        public sealed class RankedProcessActive
        {
            public int Rank { get; set; }
            public ProcessIdentity Process { get; set; }
            /// <summary>Raw bytes, authoritative.</summary>
            public long TotalBytes { get; set; }
            public double TotalMegabytes { get; set; }
            public List<CategoryMb> ByCategoryMegabytes { get; set; } = new List<CategoryMb>();
        }

        public sealed class CategoryMb
        {
            public string Category { get; set; }
            /// <summary>Raw bytes, authoritative.</summary>
            public long Bytes { get; set; }
            public double Megabytes { get; set; }
        }

        public sealed class RankedDriverLocked
        {
            public int Rank { get; set; }
            /// <summary>Path or tag identifier of the contributor; matches the text-output column.</summary>
            public string Identifier { get; set; }
            /// <summary>Raw bytes, authoritative.</summary>
            public long Bytes { get; set; }
            public double Megabytes { get; set; }
        }

        // ---- Exercise 2 -------------------------------------------------

        public sealed class Exercise2Section
        {
            public Exercise2VirtualAlloc VirtualAlloc { get; set; }
            public Exercise2Heap Heap { get; set; }
        }

        public sealed class Exercise2VirtualAlloc
        {
            public List<RankedProcessVirtualAlloc> TopProcessesByImpactingBytes { get; set; } = new List<RankedProcessVirtualAlloc>();
            public TailSummary TailProcesses { get; set; }
        }

        public sealed class RankedProcessVirtualAlloc
        {
            public int Rank { get; set; }
            public ProcessIdentity Process { get; set; }
            public long ImpactingBytes { get; set; }
            public long TransientBytes { get; set; }
            public long TotalBytes { get; set; }
            public List<RankedStack> TopImpactingStacks { get; set; } = new List<RankedStack>();
            public List<RankedStack> TopTransientStacks { get; set; } = new List<RankedStack>();
        }

        public sealed class Exercise2Heap
        {
            public List<RankedProcessHeap> TopProcessesByOutstandingBytes { get; set; } = new List<RankedProcessHeap>();
            public TailSummary TailProcesses { get; set; }
        }

        public sealed class RankedProcessHeap
        {
            public int Rank { get; set; }
            public ProcessIdentity Process { get; set; }
            public int HeapCount { get; set; }
            public long AllocationCount { get; set; }
            public long OutstandingBytes { get; set; }
            /// <summary>Heap handle (raw pointer) for the largest heap in this process. Omitted when no allocations.</summary>
            public string LargestHeapHandle { get; set; }
            public List<RankedStack> TopAllocationStacks { get; set; } = new List<RankedStack>();
        }

        // ---- Exercise 3 -------------------------------------------------

        public sealed class Exercise3Section
        {
            public Exercise3PoolAllocations PoolAllocations { get; set; }
            public Exercise3DriverCodeFootprint DriverCodeFootprint { get; set; }
        }

        public sealed class Exercise3PoolAllocations
        {
            public List<RankedDriverPool> TopDriversByNonPagedImpactingBytes { get; set; } = new List<RankedDriverPool>();
            public TailSummary TailDrivers { get; set; }
            /// <summary>Per-pool-tag breakdown for the #1 driver.</summary>
            public List<RankedTag> TopDriverTagBreakdown { get; set; } = new List<RankedTag>();
            /// <summary>Tail summary for the #1-driver tag breakdown, when truncated.</summary>
            public TailSummary TailTagBreakdown { get; set; }
        }

        public sealed class RankedDriverPool
        {
            public int Rank { get; set; }
            public DriverIdentity Driver { get; set; }
            public long NonPagedImpactingBytes { get; set; }
            public long NonPagedTransientBytes { get; set; }
            public long PagedImpactingBytes { get; set; }
            public long PagedTransientBytes { get; set; }
            public long AllocationCount { get; set; }
            public List<RankedStack> TopImpactingStacks { get; set; } = new List<RankedStack>();
            public List<RankedStack> TopTransientStacks { get; set; } = new List<RankedStack>();
        }

        public sealed class RankedTag
        {
            public int Rank { get; set; }
            public string Tag { get; set; }
            public long NonPagedImpactingBytes { get; set; }
            public long AllocationCount { get; set; }
        }

        public sealed class Exercise3DriverCodeFootprint
        {
            /// <summary>Wall-clock time of the resident-set snapshot used, UTC.</summary>
            public DateTime? SnapshotTimestampUtc { get; set; }
            public List<RankedDriverFootprint> TopDriversByResidentBytes { get; set; } = new List<RankedDriverFootprint>();
            public TailSummary TailDrivers { get; set; }
        }

        public sealed class RankedDriverFootprint
        {
            public int Rank { get; set; }
            public DriverIdentity Driver { get; set; }
            public long ResidentBytes { get; set; }
        }

        // ---- Shared tail summary ---------------------------------------

        /// <summary>Aggregate summary of items past the displayed Top-N.</summary>
        public sealed class TailSummary
        {
            /// <summary>Number of additional items not shown in the ranked list.</summary>
            public int Count { get; set; }
            /// <summary>Total bytes across the tail items. Authoritative when the metric is bytes.</summary>
            public long? Bytes { get; set; }
            /// <summary>Total megabytes across the tail items. Derived from <see cref="Bytes"/> when the metric is bytes; otherwise the source value.</summary>
            public double? Megabytes { get; set; }
        }
    }

    /// <summary>
    /// Serializes <see cref="JsonReport"/> instances with the project's
    /// canonical formatting (camelCase, indented, null-omitting).
    /// </summary>
    internal static class JsonReportWriter
    {
        private static readonly JsonSerializerOptions s_options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Writes <paramref name="report"/> to <paramref name="path"/>.
        /// Throws on any I/O failure — callers should wrap and log, so a
        /// JSON write failure does not silently drop the artifact.
        /// </summary>
        public static void Save(JsonReport report, string path)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions
            {
                Indented = true,
                Encoder = s_options.Encoder
            });
            JsonSerializer.Serialize(writer, report, s_options);
        }
    }
}
