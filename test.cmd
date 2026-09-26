@echo off
setlocal
cd /d "%~dp0"

rem Runs the Explorer.Core checks. No NuGet packages are needed.
rem Output goes to build\test.log as well as the screen.

set LOG=build\test.log
if not exist build mkdir build

where dotnet >nul 2>nul
if errorlevel 1 goto NOSDK

dotnet run --project tests\Explorer.Tests\Explorer.Tests.csproj -v minimal > "%LOG%" 2>&1
set RESULT=%ERRORLEVEL%

type "%LOG%"

if not "%RESULT%"=="0" echo.
if not "%RESULT%"=="0" echo Some checks failed. Details are in build\test.log.
goto END

:NOSDK
echo The .NET SDK is not installed.
goto END

:END
echo.
pause
endlocal
