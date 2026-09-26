@echo off
setlocal
cd /d "%~dp0"

rem ASCII only: the console code page differs between machines.
rem All output goes to build\run.log so Claude can read it without copy and paste.
rem
rem The app is started as  dotnet 2x2Explorer.dll  instead of 2x2Explorer.exe.
rem 2x2Explorer.exe is an unsigned launcher generated at build time, and some
rem Windows protection settings refuse to start it. dotnet.exe is signed by
rem Microsoft, so this path avoids that problem entirely.

set LOG=build\run.log
if not exist build mkdir build
set APPDLL=src\Explorer.App\bin\Debug\net10.0-windows\2x2Explorer.dll

where dotnet >nul 2>nul
if errorlevel 1 goto NOSDK

set HASSDK=
for /f "delims=" %%i in ('dotnet --list-sdks 2^>nul') do set HASSDK=1
if not defined HASSDK goto NOSDK

echo [1/2] Building... (the first run takes 1-2 minutes)
dotnet build 2x2Explorer.sln -v minimal > "%LOG%" 2>&1
if not errorlevel 1 goto BUILT

rem A locked output file (CS2012) is not a code error. A background build server or the
rem app from the last run still has the file mapped. Shut the servers down and try once more.
echo Build failed once. Closing background build servers and retrying...
dotnet build-server shutdown >nul 2>&1
dotnet build 2x2Explorer.sln -v minimal > "%LOG%" 2>&1
if errorlevel 1 goto BUILDFAILED

:BUILT

if not exist "%APPDLL%" goto NODLL

echo [2/2] Build OK. Starting 2x2 Explorer...
echo.
echo Everything is recorded in build\run.log - just ask Claude to read it.
echo.
echo ---- run ---- >> "%LOG%"
dotnet "%APPDLL%" >> "%LOG%" 2>&1
if errorlevel 1 goto RUNFAILED
echo 2x2 Explorer exited normally.
goto END

:BUILDFAILED
echo.
echo BUILD FAILED. Details are in build\run.log - ask Claude to read it.
echo.
findstr /c:"CS2012" "%LOG%" >nul 2>&1
if not errorlevel 1 (
    echo NOTE: CS2012 means an output file is locked, not a code error.
    echo       Close every running Explorer window, then run this again.
    echo       If it still fails, run cleanup.cmd once and try again.
    echo.
)
type "%LOG%"
goto END

:NODLL
echo.
echo Build reported success but the output file was not found:
echo     %APPDLL%
goto END

:RUNFAILED
echo.
echo The app failed to start. Details are in build\run.log - ask Claude to read it.
echo.
type "%LOG%"
goto END

:NOSDK
echo The .NET SDK is not installed. A runtime alone cannot build sources.
echo.
echo     winget install Microsoft.DotNet.SDK.10
echo.
echo Or download the SDK x64 installer from:
echo     https://dotnet.microsoft.com/download/dotnet/10.0
echo.
echo Open a new command window after installing.
goto END

:END
echo.
pause
endlocal
