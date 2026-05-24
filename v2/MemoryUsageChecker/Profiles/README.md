# MemoryUsageChecker tracing

This folder contains everything needed to capture an ETW trace that the `MemoryUsageChecker.exe` sample can analyze.

`MemoryUsageChecker.wprp` is a single Windows Performance Recorder profile that captures every data source the sample consumes:

| Captured | Used by |
|---|---|
| Image load / unload, processes, threads | All exercises (symbols, attribution) |
| Resident-set / working-set sampling | Exercise 1; Exercise 3 driver code footprint |
| VirtualAlloc commit / decommit (with stacks) | Exercise 2 Part A |
| User-mode heap allocations (with stacks) | Exercise 2 Part B (per-app opt-in — see step 2) |
| Kernel pool allocations (with stacks) | Exercise 3 Part A |

## 1. Collect a trace

- Get the tracing files (choose **(i)** OR **(ii)**, not both):
  1. Clone the repo
     - Clone `https://github.com/microsoft/eventtracing-processing-samples` and open this folder (`v2\MemoryUsageChecker\Profiles\`) in an elevated `cmd.exe`.
  2. OR download the two files directly
     - [MemoryUsageChecker.wprp](https://raw.githubusercontent.com/microsoft/eventtracing-processing-samples/master/v2/MemoryUsageChecker/Profiles/MemoryUsageChecker.wprp)
     - [MemoryUsageTrace.cmd](https://raw.githubusercontent.com/microsoft/eventtracing-processing-samples/master/v2/MemoryUsageChecker/Profiles/MemoryUsageTrace.cmd)
     - Save both into the same folder, then open that folder from an **elevated** `cmd.exe`.
- Run **`MemoryUsageTrace.cmd`** from the elevated prompt.
- At the first menu, select "**Start Tracing**".
- At the second menu, select "**MemoryUsageChecker**" (full profile).
- At the next menu, choose "**Start Now**" (or "**Start From Next Boot Session**" for boot-time issues).
- Reproduce the workload you want to analyze, then press any key to stop tracing.
- Follow the on-screen instructions when collection finishes — the script prints the list of files to share.

By default the trace files land in `%SystemRoot%\Tracing\`:

| File | Description |
|---|---|
| `MemoryUsage-Trace.etl` | The ETL the analyzer consumes. |
| `MemoryUsage-TraceInfo.txt` | `wpr -status profiles collectors -details` output, OS build numbers, total/free RAM, page-file usage. |
| `MemoryUsage-System.evtx` | Exported Windows System event log (useful for low-memory / out-of-memory events around the repro). |

> **Tip:** After `dotnet publish` produces the single-file `MemoryUsageChecker.exe`, the same `MemoryUsageChecker.wprp` and `MemoryUsageTrace.cmd` are copied to the deploy folder as sidecar files. Testers who receive a published build can run `MemoryUsageTrace.cmd` directly from that folder — no need to clone the repo or download the profile separately.

## 2. Heap snapshots (Exercise 2 Part B)

User-mode heap allocations are only captured for processes that opt in via a per-image registry flag. **Set the flag before launching the process you want to trace.** For an app called `YourApp.exe`:

```cmd
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /t REG_DWORD /d 1 /f
```

Remove the flag after collection:

```cmd
reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /f
```

The opt-in is documented at <https://learn.microsoft.com/windows-hardware/test/wpt/heap-recording>. If the flag is not set, every other exercise still works — Exercise 2 Part B simply prints `[skipped - no heap snapshots in trace]` and processing continues.

## 3. Analyze the trace

From a terminal in the directory containing the trace:

```cmd
MemoryUsageChecker.exe %SystemRoot%\Tracing\MemoryUsage-Trace.etl
```

Each exercise auto-detects whether its required providers were captured. Sections whose data was not recorded print a clearly-labeled `[skipped - …]` notice and the rest of the analysis continues.

## 4. Manual `wpr` (advanced)

If you would rather drive WPR by hand instead of using the script, open an elevated `cmd.exe`:

```cmd
:: Start (picks the Verbose profile by Name)
wpr -start MemoryUsageChecker.wprp!MemoryUsageChecker -filemode

:: Reproduce the workload

:: Stop and save
wpr -stop MyTrace.etl

:: Or discard an in-flight recording without writing a file
wpr -cancel
```

## 5. References

- [Windows Performance Recorder — Recording Profiles](https://learn.microsoft.com/windows-hardware/test/wpt/recording-profiles)
- [WPR XSD reference](https://learn.microsoft.com/windows-hardware/test/wpt/wprcontrolprofiles-schema)
- WPT Memory Footprint Optimization exercises: [1](https://learn.microsoft.com/windows-hardware/test/wpt/memory-footprint-optimization-exercise-1) · [2](https://learn.microsoft.com/windows-hardware/test/wpt/memory-footprint-optimization-exercise-2) · [3](https://learn.microsoft.com/windows-hardware/test/wpt/memory-footprint-optimization-exercise-3)
