@echo off
setlocal EnableExtensions

REM ==========================================================================
REM Collect-MemoryUsageTrace.cmd
REM
REM Double-click to capture an ETW trace suitable for the MemoryUsageChecker
REM sample using MemoryUsageChecker.wprp (located next to this script).
REM
REM The script will:
REM   1. Self-elevate to Administrator (wpr.exe requires it).
REM   2. Start the trace.
REM   3. Wait while you reproduce the workload you want to analyze.
REM   4. Stop the trace into a timestamped .etl file saved next to this script.
REM ==========================================================================

title MemoryUsageChecker - ETW collection

REM -------------------------------------------------------------------------
REM 1. Self-elevate if not already running as Administrator.
REM -------------------------------------------------------------------------
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting Administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

set "SCRIPT_DIR=%~dp0"
set "WPRP=%SCRIPT_DIR%MemoryUsageChecker.wprp"
set "PROFILE_TOKEN=MemoryUsageChecker"

if not exist "%WPRP%" (
    echo.
    echo ERROR: Cannot find MemoryUsageChecker.wprp next to this script.
    echo Looked for: %WPRP%
    echo.
    pause
    exit /b 1
)

REM -------------------------------------------------------------------------
REM 2. If WPR is already recording, offer to cancel before continuing.
REM -------------------------------------------------------------------------
wpr -status 2>&1 | findstr /C:"recording is in progress" >nul
if not errorlevel 1 (
    echo.
    echo A WPR recording is already in progress on this machine.
    echo To collect a new trace the existing session must be cancelled first.
    echo.
    choice /C YN /N /M "Cancel the existing WPR session and continue? [Y/N]: "
    if errorlevel 2 (
        echo Aborted.
        pause
        exit /b 1
    )
    wpr -cancel
)

REM -------------------------------------------------------------------------
REM 3. Build a timestamped output filename: MemoryUsageChecker-YYYYMMDD-HHMMSS.etl
REM -------------------------------------------------------------------------
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"`) do set "STAMP=%%I"
set "ETL=%SCRIPT_DIR%MemoryUsageChecker-%STAMP%.etl"

echo.
echo ===========================================================================
echo MemoryUsageChecker - ETW trace collection
echo ---------------------------------------------------------------------------
echo Profile : %WPRP%!%PROFILE_TOKEN%
echo Output  : %ETL%
echo ===========================================================================
echo.
echo NOTE: For user-mode heap snapshots (Exercise 2 Part B), set the per-app
echo       opt-in flag BEFORE launching the target process, e.g.:
echo.
echo   reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /t REG_DWORD /d 1 /f
echo.
echo       and remove it after collection:
echo.
echo   reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /f
echo.
pause

REM -------------------------------------------------------------------------
REM 4. Start the trace.
REM -------------------------------------------------------------------------
echo.
echo [1/2] Starting trace...
wpr -start "%WPRP%!%PROFILE_TOKEN%" -filemode
if errorlevel 1 (
    echo.
    echo ERROR: wpr -start failed.
    echo If WPR says a recording is already in progress, run:  wpr -cancel
    pause
    exit /b 1
)

echo.
echo Trace is now recording.
echo.
echo   ^>^>^>  Reproduce the workload you want to analyze, THEN press a key here.  ^<^<^<
echo.
echo Tip: keep the run short. A few seconds to a couple of minutes is usually
echo      enough; longer captures grow very large (multiple GB).
echo.
pause

REM -------------------------------------------------------------------------
REM 5. Stop the trace and flush to disk.
REM -------------------------------------------------------------------------
echo.
echo [2/2] Stopping trace and writing %ETL%
echo       (Flushing buffers may take a moment...)
wpr -stop "%ETL%"
if errorlevel 1 (
    echo.
    echo ERROR: wpr -stop failed. The in-progress recording may still be active.
    echo        Discard it with:   wpr -cancel
    pause
    exit /b 1
)

echo.
echo ===========================================================================
echo Done.
echo.
echo Trace saved to:
echo   %ETL%
echo.
echo Next, analyze it from a terminal in the v2\MemoryUsageChecker folder:
echo   dotnet run -c Release -- "%ETL%"
echo.
echo Or with the built exe:
echo   MemoryUsageChecker.exe "%ETL%"
echo ===========================================================================
echo.
pause
endlocal
