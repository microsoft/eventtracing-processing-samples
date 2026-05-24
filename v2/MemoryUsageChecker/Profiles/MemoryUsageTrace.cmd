@echo off
setlocal
set scriptDirectory=%~dp0
set wprpFileName=MemoryUsageChecker.wprp
rem Output files land next to this script. Strip the trailing backslash from
rem %~dp0 so that "%traceFilesOutputPath%" is a clean directory path — wpr.exe
rem -recordTempTo refuses paths whose closing quote is preceded by a backslash
rem (CRT argv parsing treats \" as an escaped literal quote, error 0xc5586004).
set "traceFilesOutputPath=%scriptDirectory:~0,-1%"
set etlFileName=MemoryUsage-Trace.etl
set traceInfoFileName=MemoryUsage-TraceInfo.txt
set systemEventLogsFileName=MemoryUsage-System.evtx
set memoryTraceRegKey=HKLM\Software\Microsoft\MemoryUsageTrace

if not exist "%scriptDirectory%%wprpFileName%" (
    echo.
    echo #########################################################################################################
    echo.
    echo ERROR: %wprpFileName% is NOT found next to this script.
    echo.
    echo.
    echo.Please open the following link in a browser and then save it as %wprpFileName% to this folder.
    echo.
    echo   https://raw.githubusercontent.com/microsoft/eventtracing-processing-samples/master/v2/MemoryUsageChecker/Profiles/%wprpFileName%
    echo.
    echo Alternatively, use this command in PowerShell to download the file.
    echo.
    echo   wget https://raw.githubusercontent.com/microsoft/eventtracing-processing-samples/master/v2/MemoryUsageChecker/Profiles/%wprpFileName% -outfile .\%wprpFileName%
    echo.
    echo For more information, see the project README at
    echo   https://github.com/microsoft/eventtracing-processing-samples/tree/master/v2/MemoryUsageChecker
    echo.
    echo #########################################################################################################
    goto End
)

if exist %SystemRoot%\system32\WHOAMI.EXE (
    %SystemRoot%\system32\WHOAMI.EXE /GROUPS | FIND.EXE /I "S-1-16-12288" >nul
    IF ERRORLEVEL 1 (
        echo.
        echo #########################################################################################################
        echo.
        ECHO ERROR: This script must be run from an elevated command prompt.
        echo.
        echo #########################################################################################################
        goto End
    )
)

cls

:MainMenu
set selection=
echo ###############################
echo     MEMORY USAGE TRACING
echo ###############################
echo.
echo 1) Start Tracing
echo 2) Stop Boot Session Trace
echo 3) Cleanup Previous Session
echo 4) Exit
echo.
set /p selection=Enter selection number:
if "%selection%"=="1" goto ProfilesMenu
if "%selection%"=="2" goto StopBootTrace
if "%selection%"=="3" goto Cleanup
if "%selection%"=="4" goto End
echo "%selection%" is not a valid option.  Please try again.
echo.
goto MainMenu

:ProfilesMenu
set selection=
set profileName=
echo.
echo ----------------------------
echo Which profile to use?
echo ----------------------------
echo 1) MemoryUsageChecker  (full profile - covers all 3 exercises)
echo 2) Back
echo.
set /p selection=Enter selection number:
if "%selection%"=="1" set profileName=MemoryUsageChecker
if "%selection%"=="2" goto MainMenu
if not "%profileName%"=="" goto StartOptionsMenu
echo.
echo "%selection%" is not a valid option.  Please try again.
echo.
goto ProfilesMenu

:StartOptionsMenu
set selection=
set startTime=%Time%
set startDate=%Date%
echo.
echo --------------
echo Start options:
echo --------------
echo 1) Start Now
echo 2) Start From Next Boot Session
echo 3) Back
echo.
echo NOTE: For user-mode heap snapshots (Exercise 2 Part B), set the per-app
echo       TracingFlags=1 opt-in BEFORE launching the target process:
echo.
echo       reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /t REG_DWORD /d 1 /f
echo.
echo       and remove it after collection:
echo.
echo       reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\YourApp.exe" /v TracingFlags /f
echo.
set /p selection=Enter selection number:
if "%selection%"=="1" goto StartNow
if "%selection%"=="2" goto ConfigureBootTrace
if "%selection%"=="3" goto ProfilesMenu
echo.
echo "%selection%" is not a valid option.  Please try again.
echo.
goto StartOptionsMenu

:StartNow
echo.
echo Starting Tracing Now... (%wprpFileName%!%profileName%)
wpr.exe -start "%scriptDirectory%%wprpFileName%!%profileName%" -filemode -recordTempTo "%traceFilesOutputPath%"
if not %ERRORLEVEL%==0 goto End
echo.
echo ----------------------------------------------------------------------
echo Tracing started. Reproduce the workload and hit any key to stop tracing.
echo ----------------------------------------------------------------------
pause
echo Saving WPR status to %traceInfoFileName%...
wpr.exe -status profiles collectors -details > "%traceFilesOutputPath%\%traceInfoFileName%"
echo Stopping tracing...
wpr.exe -stop "%traceFilesOutputPath%\%etlFileName%"
if not %ERRORLEVEL%==0 goto End
goto CollectMoreInfo

:ConfigureBootTrace
echo.
echo Configuring Boot Session Trace... (%wprpFileName%!%profileName%)
wpr.exe -addboot "%scriptDirectory%%wprpFileName%!%profileName%" -filemode -recordTempTo "%traceFilesOutputPath%"
if not %ERRORLEVEL%==0 goto End

rem Save the profile name to registry so we can retrieve it when stopping the boot trace.
REG.EXE ADD "%memoryTraceRegKey%" /v ProfileName /t REG_SZ /d %profileName% /f >nul

echo.
echo ###############################################################################
echo Please reboot your PC to start tracing. After reproducing the workload, run
echo this script again and select "Stop Boot Session Trace" to stop tracing.
echo ###############################################################################
echo.
goto End

:StopBootTrace
rem Restore the profile name saved when the boot trace was configured.
FOR /F "skip=2 tokens=3" %%v IN ('reg.exe query "%memoryTraceRegKey%" /v ProfileName 2^>nul') DO set profileName=%%v
REG.EXE DELETE "%memoryTraceRegKey%" /v ProfileName /f >nul 2>&1
echo Saving WPR status to %traceInfoFileName%...
wpr.exe -status profiles collectors -details > "%traceFilesOutputPath%\%traceInfoFileName%"
echo Stopping boot session tracing...
wpr.exe -stopboot "%traceFilesOutputPath%\%etlFileName%"
if not %ERRORLEVEL%==0 goto End
goto CollectMoreInfo

:CollectMoreInfo
echo.
echo Collecting more info...
rem OS Build Numbers and memory configuration go AFTER the WPR status block.
echo. >> "%traceFilesOutputPath%\%traceInfoFileName%"
echo Tracing Start Time: %startTime% %startDate% >> "%traceFilesOutputPath%\%traceInfoFileName%"
echo. >> "%traceFilesOutputPath%\%traceInfoFileName%"
echo - OS build numbers...
echo === OS Build Numbers === >> "%traceFilesOutputPath%\%traceInfoFileName%"
reg query "HKLM\Software\Microsoft\Windows NT\CurrentVersion" /v BuildLabEx >> "%traceFilesOutputPath%\%traceInfoFileName%" 2>&1
reg query "HKLM\Software\Microsoft\Windows NT\CurrentVersion" /v CurrentBuildNumber >> "%traceFilesOutputPath%\%traceInfoFileName%" 2>&1
reg query "HKLM\Software\Microsoft\Windows NT\CurrentVersion" /v DisplayVersion >> "%traceFilesOutputPath%\%traceInfoFileName%" 2>&1
reg query "HKLM\Software\Microsoft\Windows NT\CurrentVersion" /v UBR >> "%traceFilesOutputPath%\%traceInfoFileName%" 2>&1
echo. >> "%traceFilesOutputPath%\%traceInfoFileName%"
echo - Memory configuration...
echo === Memory Configuration === >> "%traceFilesOutputPath%\%traceInfoFileName%"
powershell -NoProfile -Command "Get-CimInstance Win32_ComputerSystem | Format-List Manufacturer,Model,TotalPhysicalMemory,NumberOfLogicalProcessors" >> "%traceFilesOutputPath%\%traceInfoFileName%" 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_OperatingSystem | Format-List Caption,Version,FreePhysicalMemory,TotalVisibleMemorySize,TotalVirtualMemorySize,FreeVirtualMemory" >> "%traceFilesOutputPath%\%traceInfoFileName%" 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_PageFileUsage | Format-List Name,AllocatedBaseSize,CurrentUsage,PeakUsage" >> "%traceFilesOutputPath%\%traceInfoFileName%" 2>&1
echo. >> "%traceFilesOutputPath%\%traceInfoFileName%"
echo - Event logs...
wevtutil.exe export-log "System" /ow:true "%traceFilesOutputPath%\%systemEventLogsFileName%"
echo. >> "%traceFilesOutputPath%\%traceInfoFileName%"
echo Tracing End Time: %Time% %Date% >> "%traceFilesOutputPath%\%traceInfoFileName%"

rem Summary
echo.
echo ######################################################################################
echo Please collect the following files under %traceFilesOutputPath% for further analysis.
echo.
echo   %etlFileName%
echo   %traceInfoFileName%
echo   %systemEventLogsFileName%
echo.
echo Analyze the trace:
echo.
echo   * Easiest: double-click MemoryUsageChecker.exe in this folder. It will auto-pick
echo     %etlFileName% (the trace just saved next to it) and start analyzing.
echo.
echo   * Or from a shell, with the path explicit:
echo.
echo       MemoryUsageChecker.exe "%traceFilesOutputPath%\%etlFileName%"
echo.
echo ######################################################################################
goto End

:Cleanup
echo.
echo Cleaning up previous session.  Use this if the trace script
echo was interrupted unexpectedly and a previous trace session is
echo still active. You may see errors for the trace session that
echo is not currently active.
echo.
wpr.exe -cancel
wpr.exe -cancelboot
REG.EXE DELETE "%memoryTraceRegKey%" /v ProfileName /f >nul 2>&1
echo.
echo #########
echo   Done.
echo #########
goto End

:End
endlocal
echo.
pause
