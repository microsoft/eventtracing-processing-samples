# MemoryUsageChecker

A v2 sample that consumes a single ETL trace and emits a color-coded analysis covering all three Microsoft WPT memory footprint optimization exercises:

- [Exercise 1 — Resident Set analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-1)
- [Exercise 2 — VirtualAlloc + Heap](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-2)
- [Exercise 3 — Pool + driver code footprint](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-3)

Each exercise runs independently. Sections whose required providers were not captured are clearly marked as `[skipped - ...]` and the rest of the analysis continues. Lists are sorted descending by memory size, and the largest offenders are highlighted in red so a tester or IHV can spot the worst hotspot at a glance and paste the row directly into a bug report.

## 1. Collecting a trace

The easiest way is to use the WPR profile shipped alongside this sample. After `dotnet publish` (see [§4 below](#4-building-and-publishing)) the single-file `MemoryUsageChecker.exe` ships with `MemoryUsageChecker.wprp` and `MemoryUsageTrace.cmd` as sidecar files in the same folder, so a tester can collect a trace and analyze it without leaving the deploy folder.

### 1.1 One-click (recommended for new testers)

Open an **elevated** `cmd.exe` and run **`MemoryUsageTrace.cmd`**. The script lives next to the `MemoryUsageChecker.exe` in the deploy folder after publish, or under `v2\MemoryUsageChecker\Profiles\` in the source tree.

1. At the first menu, select "**Start Tracing**".
2. At the second menu, select "**MemoryUsageChecker**" (full profile).
3. At the next menu, choose "**Start Now**" (or "**Start From Next Boot Session**" for boot-time issues).
4. Reproduce the workload you want to analyze.
5. Press any key to stop tracing.

By default the trace files land in the same folder as `MemoryUsageTrace.cmd` itself, so on a USB drop they stay inside the deploy folder right next to `MemoryUsageChecker.exe`:

| File | Description |
|---|---|
| `MemoryUsage-Trace.etl` | The ETL the analyzer consumes. |
| `MemoryUsage-TraceInfo.txt` | `wpr -status profiles collectors -details` output, OS build numbers, total/free RAM, page-file usage. |
| `MemoryUsage-System.evtx` | Exported Windows System event log (useful for low-memory / out-of-memory events around the repro). |

The script prints the list of files to share when collection finishes.

### 1.2 Heap snapshots (Exercise 2 Part B)

User-mode heap allocations are only captured for processes that opt in via a per-image registry flag. **Set the flag before launching the process you want to trace.** For an app called `YourApp.exe`:

```cmd
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /t REG_DWORD /d 1 /f
```

Remove the flag after collection:

```cmd
reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /f
```

The opt-in is documented at <https://learn.microsoft.com/windows-hardware/test/wpt/heap-recording>. If the flag is not set, every other exercise still works — Exercise 2 Part B simply prints `[skipped - no heap snapshots in trace]` and processing continues.

### 1.3 Manual wpr (advanced)

If you prefer to drive WPR by hand instead of using the script, open an **elevated** PowerShell or `cmd.exe`:

```cmd
:: Start (selects the Verbose profile shipped in this file)
wpr -start v2\MemoryUsageChecker\Profiles\MemoryUsageChecker.wprp!MemoryUsageChecker -filemode

:: Reproduce the workload you want to analyze

:: Stop and save the trace
wpr -stop MyTrace.etl

:: Or discard an in-flight recording without writing a file
wpr -cancel
```

### 1.4 Building your own profile (alternative)

If you prefer to assemble your own profile in **Windows Performance Recorder (WPR)**, enable the providers below to populate all three exercise sections from a single trace:

| Provider / Profile | Needed for |
|---|---|
| First Level Triage | Processes, threads, images (always required) |
| Resident Set analysis | Exercise 1, Exercise 3 driver code footprint |
| VirtualAlloc | Exercise 2 Part A |
| Heap | Exercise 2 Part B (also requires per-process `TracingFlags=1` registry key; see above) |
| Pool usage | Exercise 3 Part A |

Capture with **Logging mode = File** and save the resulting `.etl` to disk.

## 2. Running the sample

```
MemoryUsageChecker.exe <trace.etl> [--top N] [--symbols <path>] [--no-symbols]
```

| Argument | Default | Description |
|---|---|---|
| `<trace.etl>` | (required) | Path to the ETL file. |
| `--top N` | `10` | Caps the number of top entries displayed per category. |
| `--symbols <path>` | (see below) | Override the symbol search path. Accepts any `symsrv`-compatible string, including `SRV*<cache>*<server>`. |
| `--no-symbols` | (off) | Skip symbol resolution. Exercises 2 and 3 still run but stack frames show `[no symbols]`. |

Symbol-path resolution precedence (when `--no-symbols` is not specified):

1. `--symbols <path>` if provided on the command line.
2. The `_NT_SYMBOL_PATH` environment variable if it is set and non-empty.
3. Otherwise, the Microsoft Public Symbol Server is used by default:
   `SRV*%LOCALAPPDATA%\SymbolCache*https://msdl.microsoft.com/download/symbols`
   The downstream cache directory is created on first use so the next run is incremental.

The active symbol source is printed at the top of every run, so you can confirm which path the sample resolved before stacks are decoded.

A timestamped result file `MemoryUsage_Result_yyyyMMdd_HHmm.txt` is written next to the working directory and mirrors the console output (without colors), ready to attach to a bug.

A companion **diagnostic log** `MemoryUsage_Diag_yyyyMMdd_HHmm.log` is written next to the result file on every run. It captures the assembly version, the .NET runtime version, the OS description, the parsed command-line, the resolved symbol path, the `HasResult` flag and item count for every ETL data source, and per-phase `BEGIN`/`END (elapsed=Xs)` timings. If any exercise throws, the full exception type, message, stack trace, and up to five levels of inner-exception detail are appended. **Please attach this log together with the result file when reporting an issue** — it lets the maintainer reproduce the run state without re-collecting the trace.

A companion **JSON sidecar** `MemoryUsage_Result_yyyyMMdd_HHmm.json` is also written next to the text result on every run. The JSON twin carries the same data the text file shows, in a stable, versioned, machine-readable shape that is ideal for **A/B comparison** between two captures (before vs after, device A vs device B).

## 3. Reading the output

Lists across the analysis (top processes by working set, top drivers by pool usage, top stacks, etc.) are **always sorted descending by size** and color-coded by rank so the worst offender is the most visually prominent. Numeric columns are right-aligned for easy scanning, and each row includes a bug-report-quality identifier (process `pid`, full driver path, top stack frame `Image!Function`, pool 4-char tag, etc.) so you can paste any row directly into a report to Microsoft, an IHV, or an internal owner.

### Color legend

| Color | Meaning |
|---|---|
| **Red** (Critical) | The #1 row in a ranked list, OR a threshold breach (e.g. VirtualAlloc Impacting ≥ 10 MB, NonPaged pool ≥ 1 MB per driver, driver code ≥ 2 MB resident). Always investigate first. |
| **Yellow** (High) | The next tier (~top third) of a ranked list. |
| **White** (Normal) | Remaining rows inside the displayed Top-N. |
| **DarkGray** (Tail) | The `+ N more ... totaling X.XX MB` summary line after a truncated list, plus stack frames and `[skipped - ...]` notices. |
| Cyan | Subsection header. |
| Yellow (header) | Trace banner and exercise title. |
| Blue | Plain data / parent rows. |
| Green | Healthy finding (e.g. `Impacting = 0`). |
| Magenta | Notable observation that did not breach a critical threshold. |

The result file (`MemoryUsage_Result_*.txt`) contains the same content without color and is what you should attach to a bug report.

### Sample output (annotated)

```
=== Exercise 3: Pool ===
--- Pool Allocations ---
Top 10 drivers by NonPaged Impacting size (KB):
  Ndu.sys                       NP-Imp   12288.0  NP-Tr     128.0  P-Imp     0.0  P-Tr     0.0  KB  (4096 allocs)   <-- Red (Critical: ≥ 1 MB NP-Imp)
  Tcpip.sys                     NP-Imp     820.5  NP-Tr     256.0  P-Imp    32.0  P-Tr    16.0  KB  ( 312 allocs)   <-- Yellow (High)
  netbt.sys                     NP-Imp      96.0  NP-Tr      48.0  P-Imp     0.0  P-Tr     0.0  KB  (  18 allocs)   <-- White (Normal)
  ...
  + 47 more drivers totaling 312.4 KB NP-Imp                                                                        <-- DarkGray (Tail)

Top 5 pool alloc stacks for Ndu.sys (NonPaged only)
Impacting:
  [1] 11264.0 KB across 3584 allocs   Ndu.sys!NduAcquireFromPool
        Ndu.sys!NduAcquireFromPool
        Ndu.sys!NduFlowAlloc
        ...

--- Driver Code Footprint (File Backed Pages) ---
Top 10 drivers by code resident footprint (MB):
      4.12 MB     1056 pages  nvlddmkm.sys                  \SystemRoot\System32\drivers\nvlddmkm.sys                 <-- Red (≥ 2 MB)
      1.78 MB      456 pages  Tcpip.sys                     \SystemRoot\System32\drivers\Tcpip.sys                    <-- Yellow
      0.42 MB      108 pages  Ndu.sys                       \SystemRoot\System32\drivers\Ndu.sys                      <-- White
  + 21 more drivers totaling 1.94 MB                                                                                  <-- DarkGray
```

(The exact values above are illustrative; what matters is the ranked color tiering, the right-aligned numerics, the per-row bug-report identifier, and the single tail summary line.)

## 4. Building and publishing

This sample targets **.NET 10** with `Microsoft.Windows.EventTracing` `2.0.711-preview` so it can ship as a single self-contained `.exe`. (Other samples in this repo target `net8.0` with `2.0.679-preview`; that is intentional — `MemoryUsageChecker` is kept on the latest .NET and latest package to keep security-patch overhead low and to enable single-file publish.)

### Build for development

```
dotnet build v2/MemoryUsageChecker/MemoryUsageChecker.csproj -c Release
```

### Publish as a single self-contained `.exe`

```
:: x64
dotnet publish v2/MemoryUsageChecker -p:PublishProfile=win-x64

:: arm64
dotnet publish v2/MemoryUsageChecker -p:PublishProfile=win-arm64
```

The result is a roughly 47 MB single executable at:

```
v2\MemoryUsageChecker\bin\Release\net10.0\win-x64\publish\MemoryUsageChecker.exe
```

Three files sit alongside the `.exe` as deployable sidecars — they are NOT embedded into the single-file bundle so testers can edit them without re-publishing:

```
publish\
  MemoryUsageChecker.exe       <-- single self-contained executable (~47 MB)
  MemoryUsageChecker.wprp      <-- WPR profile (drop into wpr -start)
  MemoryUsageTrace.cmd         <-- one-click collection helper
  README.md                    <-- this document (for the external drop)
```

(`win-arm64` lands in the corresponding `…\win-arm64\publish\` folder with the same four files.) Those four files are everything an external user needs — you can zip the entire `publish\` directory and hand it to a tester, IHV, or internal engineer without any extra files. The `.exe` is self-extracting, requires no installed .NET runtime, and can be dropped onto any Windows 10/11 box for ad-hoc trace analysis. Trimming is intentionally disabled — `Microsoft.Windows.EventTracing` uses reflection to parse ETW payloads and trimming would remove types it needs at runtime.

## 5. References

- [v1→v2 Migration Guide](../../EventTracing%20v1%20to%20v2%20Migration%20Guide.md)
- Microsoft Learn — WPT Memory Footprint Optimization Exercises: [1](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-1), [2](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-2), [3](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-3)
- [Windows Performance Recorder — Recording Profiles](https://learn.microsoft.com/windows-hardware/test/wpt/recording-profiles)
- [WPR XSD reference](https://learn.microsoft.com/windows-hardware/test/wpt/wprcontrolprofiles-schema)
- [Heap recording opt-in](https://learn.microsoft.com/windows-hardware/test/wpt/heap-recording)
