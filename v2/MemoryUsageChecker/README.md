# MemoryUsageChecker

A v2 sample that consumes a single ETL trace and emits a color-coded analysis covering all three Microsoft WPT memory footprint optimization exercises:

- [Exercise 1 — Resident Set analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-1)
- [Exercise 2 — VirtualAlloc + Heap](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-2)
- [Exercise 3 — Pool + driver code footprint](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-3)

Each exercise runs independently. Sections whose required providers were not captured are clearly marked as `[skipped - ...]` and the rest of the analysis continues. Lists are sorted descending by memory size, and the largest offenders are highlighted in red so a tester or IHV can spot the worst hotspot at a glance and paste the row directly into a bug report.

## 1. Collecting a trace

The easiest way is to use the WPR profile shipped alongside this sample. After `dotnet publish` (see [§4 below](#4-building-and-publishing)) the single-file `MemoryUsageChecker.exe` ships with `MemoryUsageChecker.wprp` and `MemoryUsageTrace.cmd` as sidecar files in the same folder, so a tester can collect a trace and analyze it without leaving the deploy folder.

**One-click (recommended for new testers):** open an **elevated** `cmd.exe`, run `MemoryUsageTrace.cmd` (from the deploy folder next to `MemoryUsageChecker.exe`, or from `v2\MemoryUsageChecker\Profiles\` in the source tree), pick **Start Tracing → MemoryUsageChecker → Start Now**, reproduce your workload, then press a key to stop. Output lands in `%SystemRoot%\Tracing\` as `MemoryUsage-Trace.etl` plus a `*-TraceInfo.txt` and `*-System.evtx` for bug reports.

**Manual:** from an **elevated** PowerShell or `cmd.exe`:

```cmd
:: 1. Start collecting (selects the Verbose profile shipped in this file)
wpr -start v2\MemoryUsageChecker\Profiles\MemoryUsageChecker.wprp!MemoryUsageChecker -filemode

:: 2. Reproduce the workload you want to analyze

:: 3. Stop and save the trace
wpr -stop MyTrace.etl
```

For user-mode heap snapshots (Exercise 2 Part B), set the per-image opt-in flag once before launching the process you want to trace:

```cmd
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<app.exe>" /v TracingFlags /t REG_DWORD /d 1 /f
```

See [`Profiles/README.md`](./Profiles/README.md) for the full collection walkthrough, the list of providers captured, and how to drive `wpr` by hand.

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
| `--symbols <path>` | `Automatic` | Override the symbol search path. `Automatic` honors `_NT_SYMBOL_PATH` and falls back to the Microsoft public symbol server. |
| `--no-symbols` | (off) | Skip symbol resolution. Exercises 2 and 3 still run but stack frames show `[no symbols]`. |

A timestamped result file `MemoryUsage_Result_yyyyMMdd_HHmm.txt` is written next to the working directory and mirrors the console output (without colors), ready to attach to a bug.

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

Two files sit alongside the `.exe` as deployable sidecars — they are NOT embedded into the single-file bundle so testers can edit them without re-publishing:

```
publish\
  MemoryUsageChecker.exe       <-- single self-contained executable (~47 MB)
  MemoryUsageChecker.wprp      <-- WPR profile (drop into wpr -start)
  MemoryUsageTrace.cmd         <-- one-click collection helper
```

(`win-arm64` lands in the corresponding `…\win-arm64\publish\` folder.) The `.exe` is self-extracting, requires no installed .NET runtime, and can be dropped onto any Windows 10/11 box for ad-hoc trace analysis. Trimming is intentionally disabled — `Microsoft.Windows.EventTracing` uses reflection to parse ETW payloads and trimming would remove types it needs at runtime.

## 5. References

- [v1→v2 Migration Guide](../../EventTracing%20v1%20to%20v2%20Migration%20Guide.md)
- Microsoft Learn — WPT Memory Footprint Optimization Exercises: [1](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-1), [2](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-2), [3](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-3)
- [Windows Performance Recorder — Recording Profiles](https://learn.microsoft.com/windows-hardware/test/wpt/recording-profiles)
- [Heap recording opt-in](https://learn.microsoft.com/windows-hardware/test/wpt/heap-recording)
