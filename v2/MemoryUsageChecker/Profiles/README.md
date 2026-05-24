# MemoryUsageChecker WPR profile

`MemoryUsageChecker.wprp` is a self-contained Windows Performance Recorder profile that captures every data source `MemoryUsageChecker.exe` consumes:

| Captured | Used by |
|---|---|
| Image load / unload, processes, threads | All exercises (symbols, attribution) |
| Resident-set / working-set sampling | Exercise 1; Exercise 3 driver code footprint |
| VirtualAlloc commit / decommit (with stacks) | Exercise 2 Part A |
| User-mode heap allocations (with stacks) | Exercise 2 Part B |
| Kernel pool allocations (with stacks) | Exercise 3 Part A |

## 1. Capture a trace

### Option A — Click-and-run (recommended for new testers)

Double-click **`Collect-MemoryUsageTrace.cmd`** in this folder. The script will:

1. Prompt for Administrator elevation (WPR requires it).
2. Confirm before cancelling any in-progress WPR session on the machine.
3. Start the trace, then pause so you can reproduce your workload.
4. Stop the trace and save it next to the script as `MemoryUsageChecker-YYYYMMDD-HHMMSS.etl`.

A `.gitignore` in this folder keeps captured `.etl` files out of source control.

### Option B — Manual `wpr` from a terminal

Open an **elevated** PowerShell or `cmd.exe`:

```cmd
:: 1. Start collecting (selects the Verbose profile shipped in this file)
wpr -start MemoryUsageChecker.wprp!MemoryUsageChecker -filemode

:: 2. Reproduce the workload you want to analyze

:: 3. Stop and save the trace
wpr -stop MyTrace.etl
```

`wpr -cancel` discards an in-flight recording without writing a file.

> **Tip — heap snapshots**
> User-mode heap allocations are only captured for processes that opt in via a per-image registry flag. Set this once, **before** launching the process you want to trace:
>
> ```cmd
> reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<app.exe>" /v TracingFlags /t REG_DWORD /d 1 /f
> ```
>
> Remove it after collection with `reg delete … /v TracingFlags /f`.
> See the official guidance: <https://learn.microsoft.com/windows-hardware/test/wpt/heap-recording>

## 2. Analyze

From the directory containing `MyTrace.etl`:

```cmd
MemoryUsageChecker.exe MyTrace.etl
```

Each exercise auto-detects whether its required providers were captured. Sections whose data was not recorded print a clearly-labeled `[skipped - …]` notice and the rest of the analysis continues.

## 3. References

- [Windows Performance Recorder — Recording Profiles](https://learn.microsoft.com/windows-hardware/test/wpt/recording-profiles)
- [WPR XSD reference](https://learn.microsoft.com/windows-hardware/test/wpt/wprcontrolprofiles-schema)
- WPT Memory Footprint Optimization exercises: [1](https://learn.microsoft.com/windows-hardware/test/wpt/memory-footprint-optimization-exercise-1) · [2](https://learn.microsoft.com/windows-hardware/test/wpt/memory-footprint-optimization-exercise-2) · [3](https://learn.microsoft.com/windows-hardware/test/wpt/memory-footprint-optimization-exercise-3)
