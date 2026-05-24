# MemoryUsageChecker

A v2 sample that consumes a single ETL trace and emits a color-coded analysis covering all three Microsoft WPT memory footprint optimization exercises:

- [Exercise 1 — Resident Set analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-1)
- [Exercise 2 — VirtualAlloc + Heap](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-2)
- [Exercise 3 — Pool + driver code footprint](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/memory-footprint-optimization-exercise-3)

Each exercise runs independently. Sections whose required providers were not captured are clearly marked as `[skipped - ...]` and the rest of the analysis continues. Lists are sorted descending by memory size and **colored by improvement direction against per-item budgets** — rows that exceed the tier's per-item budget are painted **red `✗`** with an explicit `→ trim ≥ X MB to fit Y MB` suffix, rows in the 80–100 % near-budget band are **yellow `!`**, and healthy rows are **green `✓`**. Each per-category total (user-mode WS, driver NP-pool, driver code) also ends with a one-line **`✓ PASS` / `! WATCH` / `✗ FAIL`** banner so a tester or OEM can paste the row — or the executive summary — directly into a bug report.

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

**To analyze the trace you just collected, simply double-click `MemoryUsageChecker.exe` in the same folder** — it auto-discovers `MemoryUsage-Trace.etl` sitting next to it and starts analyzing. No shell required. See [§2](#2-running-the-sample) for the auto-discovery rules and command-line equivalents.

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
MemoryUsageChecker.exe [<trace.etl>]
    [--profile 16gb|8gb|4gb]
    [--top-processes N] [--top-drivers N] [--top-stacks N]
    [--per-process-ws-budget-mb V]   [--per-process-va-budget-mb V]
    [--per-driver-pool-budget-mb V]  [--per-driver-code-budget-mb V]
    [--total-user-ws-budget-mb V]
    [--total-driver-pool-budget-mb V] [--total-driver-code-budget-mb V]
    [--min-display-mb V]
    [--profiles-file <path>] [--write-default-profiles]
    [--symbols <path>] [--no-symbols]
    [--top N]   # legacy alias = --top-processes N AND --top-drivers N
```

When `<trace.etl>` is omitted (e.g. when `MemoryUsageChecker.exe` is launched by **double-clicking** it in Explorer), the tool auto-selects the most recently modified `*.etl` file located in the **same folder as the .exe**, preferring `MemoryUsage-Trace.etl` (the canonical name produced by `MemoryUsageTrace.cmd`). The selected path is printed in cyan before processing starts. This makes the canonical one-folder workflow — collect with `MemoryUsageTrace.cmd`, analyze by double-clicking `MemoryUsageChecker.exe` — work without ever opening a shell.

### 2.1 Budget profiles (default `8gb`)

The tool is calibrated for OEM/ODM teams who must validate that their preload (apps + drivers) fits a Windows image into a target device class — **16 GB** (relaxed ceiling), **8 GB** (default), or **4 GB** (tightest). Pick the tier with **`--profile`**; every per-item and per-category budget below derives from it.

| Knob                                | `--profile 16gb` | `--profile 8gb` (default) | `--profile 4gb` | Source flag                          |
| ----------------------------------- | ---------------- | ------------------------- | --------------- | ------------------------------------ |
| Top-N processes                     | 15               | 15                        | 15              | `--top-processes`                    |
| Top-N drivers                       | 10               | 10                        | 10              | `--top-drivers`                      |
| Per-process Active WS budget        | 400 MB           | 200 MB                    | 100 MB          | `--per-process-ws-budget-mb`         |
| Per-process VirtualAlloc Impacting  | 200 MB           | 100 MB                    | 50 MB           | `--per-process-va-budget-mb`         |
| Per-driver NonPaged-pool Impacting  | 10 MB            | 5 MB                      | 2 MB            | `--per-driver-pool-budget-mb`        |
| Per-driver code-resident footprint  | 4 MB             | 2 MB                      | 1 MB            | `--per-driver-code-budget-mb`        |
| **Total** user-mode Active WS       | 3072 MB          | 1536 MB                   | 750 MB          | `--total-user-ws-budget-mb`          |
| **Total** driver NonPaged-pool      | 512 MB           | 256 MB                    | 128 MB          | `--total-driver-pool-budget-mb`      |
| **Total** driver code resident      | 128 MB           | 64 MB                     | 32 MB           | `--total-driver-code-budget-mb`      |
| `--min-display-mb` floor            | 4 MB             | 2 MB                      | 1 MB            | `--min-display-mb`                   |

Any individual `--…-budget-mb` flag overrides the tier-supplied value. When you override anything, the report header shows `Budget Profile: custom (base: 8gb)` so you can tell at a glance the run is no longer a tier-vs-tier comparable.

The defaults were calibrated against a real Windows 11 24H2 reference trace:

* Process Active WS distribution has its knee at rank ≈ **15** (top-15 covers **69 %** of attributable bytes).
* Driver NonPaged-pool distribution has its knee at rank ≈ **10** (top-10 covers **92 %** of pool bytes; only 17 drivers ever hit 2 MB).
* Driver code-resident footprint has only 3 drivers ≥ 2 MB; top-15 covers everything actionable.

### 2.2 `MemoryUsageChecker.profiles.json` — the editable defaults file

**A `MemoryUsageChecker.profiles.json` file ships in the release drop next to `MemoryUsageChecker.exe`**. It carries every number the tool uses — all three tiers' per-item budgets, per-category totals, Top-N values, the `--min-display-mb` floor, *and* the warn / fail verdict percentages. Open it in Notepad (or any text editor) to retune the image-fit verdict; the **next run picks up the changes automatically** without any command-line argument or `--help` lookup. This is the recommended workflow for OEMs / ODMs who only ever double-click the .exe to load the latest ETL.

Skeleton:

```json
{
  "schemaVersion": "1.0",
  "defaultProfile": "8gb",
  "verdictThresholds": { "warnAtPercent": 80, "failAtPercent": 100 },
  "profiles": {
    "16gb": { "perProcessWorkingSetBudgetMb": 400, "totalUserWorkingSetBudgetMb": 3072, ... },
    "8gb":  { "perProcessWorkingSetBudgetMb": 200, "totalUserWorkingSetBudgetMb": 1536, ... },
    "4gb":  { "perProcessWorkingSetBudgetMb": 100, "totalUserWorkingSetBudgetMb":  750, ... }
  }
}
```

Lookup precedence at startup:

1. `--profiles-file <path>` if passed on the command line.
2. `MemoryUsageChecker.profiles.json` in the **same folder as the .exe** (the location the release drop ships it to).
3. `MemoryUsageChecker.profiles.json` in the **current working directory**.
4. (none — built-in defaults baked into the .exe.)

If the file is absent at the exe-dir location, the .exe **seeds a fresh copy** with the canonical defaults on first run, so OEMs always find an editable template even when they extract the .exe out of the release drop on its own. To restore the file to defaults after a bad edit, delete it and re-run, **or** run `MemoryUsageChecker.exe --write-default-profiles` (writes to the exe-dir location; pass `--profiles-file <path>` to redirect).

The defaults file is also the canonical answer to "what threshold turns this row red?" — the `verdictThresholds.failAtPercent` (default `100`) and `verdictThresholds.warnAtPercent` (default `80`) fields gate every per-row colour and every per-category PASS / WATCH / FAIL banner; the rule is `actual > failAt% × budget = Fail; actual ≥ warnAt% × budget = Warn; else Pass`.

### 2.3 Improvement-direction color legend

Every Top-N row is coloured by **how it stacks up against the active per-item budget** (and the per-category total gets a single PASS / FAIL banner). The colors are the only place to look:

| Glyph & color   | Meaning                                                                                                        |
| --------------- | -------------------------------------------------------------------------------------------------------------- |
| `✗` red row     | **Over budget** (> 100 % of per-item ceiling). Row also gets a `→ trim ≥ X MB to fit Y MB` suffix.             |
| `!` yellow row  | **Near budget** (80–100 % of per-item ceiling). Watch / reduce if you can.                                     |
| `✓` green row   | Under budget — healthy.                                                                                        |
| `✓ PASS` line   | Category total is at or under the tier's whole-image budget.                                                   |
| `! WATCH` line  | Category total is in the 80–100 % band of the tier budget — borderline.                                        |
| `✗ FAIL` line   | Category total exceeds the tier budget. The line shows the exact MB-gap to close.                              |

The `100 %` / `80 %` thresholds are themselves tunable in the JSON `verdictThresholds` block (see [§2.2](#22-memoryusagecheckerprofilesjson--the-editable-defaults-file)). PASS / WATCH / FAIL category lines also appear in the **executive summary** under `--- Image-Fit Verdict (<profile>) ---`, so you can paste a 6-line digest into a bug report and tell at a glance whether the image fits the tier.

### 2.4 Other arguments

| Argument | Default | Description |
|---|---|---|
| `<trace.etl>` | auto-discovered next to the .exe | Path to the ETL file. Omit to use the most recent `*.etl` sitting next to `MemoryUsageChecker.exe`. |
| `--top-processes N` | `15` | Caps the number of rows displayed in the **process** Top-N tables (Exercise 1 Active WS, Exercise 2 VirtualAlloc/heap). |
| `--top-drivers N` | `10` | Caps the number of rows displayed in the **driver** Top-N tables (Exercise 1 driver-locked, Exercise 3 pool and code footprint). |
| `--top-stacks N` | `10` | Caps how many of the outer Top-N rows get the verbose **inner stack drill-down**. Pass `0` to suppress all drill-downs. |
| `--min-display-mb V` | tier-dependent (4 / 2 / 1) | Hides rows below **V MiB** in **both** the outer Top-N tables **and** the inner Top-K bucket / stack rows. Pass `0` to disable filtering. The `+ N more …` tail summary always accounts for the full tail so nothing is lost. The active filter is shown in each Top-N sub-header. |
| `--profiles-file <path>` | exe-dir → cwd lookup | Override the path to `MemoryUsageChecker.profiles.json`. Useful when you keep one canonical profiles file in a shared location and want every machine to load it. |
| `--write-default-profiles` | (off) | Write a fresh canonical defaults JSON to the resolved path and exit. Use this to restore the file after a bad manual edit (or to seed it in a new location specified with `--profiles-file`). |
| `--symbols <path>` | (see below) | Override the symbol search path. Accepts any `symsrv`-compatible string, including `SRV*<cache>*<server>`. |
| `--no-symbols` | (off) | Skip symbol resolution. Outer Top-N tables, the per-pool-tag breakdown, the executive summary, and the JSON sidecar are still emitted, but the per-row stack drill-down subsections in Exercises 2 and 3 are suppressed (raw `module!0xRVA` addresses are not actionable — re-run without `--no-symbols` to see them). |
| `--top N` | (none) | Legacy alias — equivalent to setting `--top-processes N` **and** `--top-drivers N` together. New scripts should use the split flags. |

Symbol-path resolution precedence (when `--no-symbols` is not specified):

1. `--symbols <path>` if provided on the command line. Used as-is — the tool assumes you know exactly what you want.
2. The `_NT_SYMBOL_PATH` environment variable if it is set and non-empty. If the path doesn't already declare a downstream cache (no `cache*<dir>` element and no `srv*<localpath>*<url>` form), the tool **auto-prepends `cache*%LOCALAPPDATA%\SymbolCache;`** so downloaded PDBs persist across runs. The augmented path is logged.
3. Otherwise, the Microsoft Public Symbol Server is used by default:
   `SRV*%LOCALAPPDATA%\SymbolCache*https://msdl.microsoft.com/download/symbols`
   The downstream cache directory is created on first use so the next run is incremental.

The pre-processed TraceProcessing `.symcache` files go to:

1. `_NT_SYMCACHE_PATH` if set.
2. Otherwise `C:\SymCache` (the convention shared with WPA / PerfView / xperf) when the current session can write there (admin sessions).
3. Otherwise `%LOCALAPPDATA%\MemoryUsageChecker\SymCache` (per-user fallback for non-admin sessions). The legacy `SymCachePath.Automatic` default of `C:\SymCache` silently degrades for non-admin sessions, which is why the tool probes for write access and falls back explicitly.

Both cache locations are printed at startup along with their current size, e.g.
```
Symbol path: Microsoft Public Symbol Server (cache: C:\Users\you\AppData\Local\SymbolCache)
SymCache dir (default (shared with WPA/PerfView)): C:\SymCache
  Cache pre-populated: 5,170 symcache files (8.46 GiB), 2,330 PDB files (3.21 GiB). Matched symbols will be reused (no re-download).
```
so you can confirm cache hits between runs. To wipe the caches, delete the directories above.

The active symbol source is printed at the top of every run, so you can confirm which path the sample resolved before stacks are decoded.

A timestamped result file `MemoryUsage_Result_yyyyMMdd_HHmm.txt` is written next to the working directory and mirrors the console output (without colors), ready to attach to a bug.

A companion **executive summary** `MemoryUsage_Summary_yyyyMMdd_HHmm.txt` (a ~30-line plain-text digest of the top actionable findings — worst process, worst driver, dominant pool tag, single recommended next step) is also written next to the result file. The same content is appended to the bottom of the main result file under a `=== EXECUTIVE SUMMARY ===` banner. This is the file to paste into a bug report when you need brevity.

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
Top 30 drivers by NonPaged Impacting size (KB) (>= 2 MB):
  Ndu.sys                       NP-Imp   12288.0  NP-Tr     128.0  P-Imp     0.0  P-Tr     0.0  KB  (4096 allocs)   <-- Red (Critical: ≥ 1 MB NP-Imp)
  Tcpip.sys                     NP-Imp    3072.0  NP-Tr     256.0  P-Imp    32.0  P-Tr    16.0  KB  ( 312 allocs)   <-- Yellow (High)
  storport.sys                  NP-Imp    2096.0  NP-Tr      48.0  P-Imp     0.0  P-Tr     0.0  KB  (  18 allocs)   <-- White (Normal)
  ...
  + 47 more drivers totaling 408.4 KB NP-Imp                                                                        <-- DarkGray (Tail; includes drivers trimmed because < 2 MB NP-Imp)

Top 5 pool alloc stacks for Ndu.sys (NonPaged only)
Impacting:
  [1] 11264.0 KB across 3584 allocs   Ndu.sys!NduAcquireFromPool
        Ndu.sys!NduAcquireFromPool
        Ndu.sys!NduFlowAlloc
        ...

--- Driver Code Footprint (File Backed Pages) ---
Top 30 drivers by code resident footprint (MB) (>= 2 MB):
      4.12 MB     1056 pages  nvlddmkm.sys                  \SystemRoot\System32\drivers\nvlddmkm.sys                 <-- Red (≥ 2 MB)
      2.78 MB      712 pages  Tcpip.sys                     \SystemRoot\System32\drivers\Tcpip.sys                    <-- Yellow
      2.04 MB      522 pages  Ndu.sys                       \SystemRoot\System32\drivers\Ndu.sys                      <-- White
  + 21 more drivers totaling 8.31 MB                                                                                  <-- DarkGray (Tail; includes drivers trimmed because < 2 MB resident)
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
