@echo off
setlocal
cd /d "%~dp0"

rem Builds everything needed for a release. Nothing is uploaded.
rem   1. reads the version from Directory.Build.props
rem   2. cuts that version's section out of CHANGELOG.md into
rem      build\dist\2x2Explorer-<version>-notes.md (stops if the section is missing)
rem   3. runs the Explorer.Core checks (stops if any fail)
rem   4. picks up the Korean wizard language if Inno Setup carries it
rem   5. publishes a self-contained x64 build into build\publish\win-x64
rem   6. runs Inno Setup to produce build\dist\2x2Explorer-<version>-setup.exe
rem   7. writes the installer's SHA-256 to build\dist\2x2Explorer-<version>-setup.exe.sha256
rem
rem Then create the GitHub Release by hand: tag v<version>, paste the notes file
rem as the description, attach the setup exe. See docs\releasing.md section 6.
rem
rem   pack.cmd            normal run, waits for a key at the end
rem   pack.cmd /nopause   for running from another script
rem
rem The installer is NOT code signed, so Windows shows "Windows protected
rem your PC" the first time it runs. See docs\releasing.md.
rem
rem ASCII only: the console code page differs between machines.

set LOG=build\pack.log
set PUBDIR=build\publish\win-x64
if not exist build mkdir build
set APPPROJ=src\Explorer.App\Explorer.App.csproj
set TESTPROJ=tests\Explorer.Tests\Explorer.Tests.csproj
set ISS=installer\2x2Explorer.iss
set RELPS=installer\release-files.ps1
set DIST=build\dist
if not exist "%DIST%" mkdir "%DIST%"

where dotnet >nul 2>nul
if errorlevel 1 goto NOSDK

rem ---- 1. version -------------------------------------------------------
rem Read <Version> straight out of the props file. The earlier attempt piped a
rem PowerShell one-liner through FOR /F and the quoting did not survive, so we
rem parse the file itself. Splitting on < > and space makes the tag the first
rem token and its value the second; <LangVersion> does not collide.
set VER=
for /f "tokens=1,2 delims=<> " %%a in (Directory.Build.props) do if /i "%%a"=="Version" set "VER=%%b"
if not defined VER goto NOVER
echo Version from Directory.Build.props: %VER%
echo.

rem ---- 1b. release notes --------------------------------------------------
rem CHANGELOG.md is UTF-8 Korean, which cmd cannot read reliably, so the cutting
rem is done by a small PowerShell script. It stops here when "## [%VER%]" is
rem missing, so a version bump without a patch note never reaches an installer.
set NOTES=%DIST%\2x2Explorer-%VER%-notes.md
if exist "%NOTES%" del /q "%NOTES%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%RELPS%" -Step notes -Version %VER% -Changelog CHANGELOG.md -Out "%NOTES%"
if errorlevel 1 goto NONOTES
echo Release notes: %NOTES%
echo.

rem ---- 1c. checks -------------------------------------------------------
echo Running the Explorer.Core checks...
dotnet run --project "%TESTPROJ%" -c Release -v minimal > build\test.log 2>&1
if errorlevel 1 goto TESTFAILED
echo Checks passed. Details are in build\test.log.
echo.

rem ---- 2. find Inno Setup ----------------------------------------------
rem winget installs Inno Setup per user under %LOCALAPPDATA%\Programs, not into
rem Program Files, so look there first. The registry lookup after that covers
rem machines where someone installed it somewhere else.
set ISCC=
call :TRYDIR "%LOCALAPPDATA%\Programs\Inno Setup 6"
call :TRYDIR "%ProgramFiles(x86)%\Inno Setup 6"
call :TRYDIR "%ProgramFiles%\Inno Setup 6"
call :TRYREG "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
call :TRYREG "HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
call :TRYREG "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
if not defined ISCC for /f "delims=" %%i in ('where ISCC 2^>nul') do set "ISCC=%%i"
if not defined ISCC goto NOISCC
echo Inno Setup: "%ISCC%"

rem ---- 3. Korean wizard language ---------------------------------------
rem Inno Setup ships its bundled .isl files next to ISCC.exe. When Korean is
rem among them we copy it beside the script, and the .iss picks it up on its
rem own. Nothing to download and nothing to edit by hand.
set KORISL=installer\Korean.isl
if exist "%KORISL%" goto HAVEKOR
for %%d in ("%ISCC%") do set "ISDIR=%%~dpd"
if exist "%ISDIR%Languages\Korean.isl" (
    copy /y "%ISDIR%Languages\Korean.isl" "%KORISL%" >nul
    echo Korean wizard: copied from "%ISDIR%Languages".
    goto KORDONE
)
echo Korean wizard: Korean.isl was not found, building an English-only wizard.
echo   Your Inno Setup does not carry it. Pick Korean from the list at
echo   https://jrsoftware.org/files/istrans/ , save it as
echo   installer\Korean.isl , and run this again.
goto KORDONE
:HAVEKOR
echo Korean wizard: using installer\Korean.isl
:KORDONE
echo.

rem ---- 4. publish -------------------------------------------------------
echo [1/2] Publishing a self-contained build... (a few minutes the first time)
if exist "%PUBDIR%" rd /s /q "%PUBDIR%"
dotnet publish "%APPPROJ%" -c Release -r win-x64 --self-contained true -o "%PUBDIR%" > "%LOG%" 2>&1
if errorlevel 1 goto PUBFAILED
if not exist "%PUBDIR%\2x2Explorer.exe" goto NOEXE

rem ---- 5. installer -----------------------------------------------------
echo [2/2] Building the installer...
"%ISCC%" /Q "/DAppVersion=%VER%" "%ISS%" >> "%LOG%" 2>&1
if errorlevel 1 goto ISSFAILED

set SETUPEXE=build\dist\2x2Explorer-%VER%-setup.exe
if not exist "%SETUPEXE%" goto NOSETUP

rem ---- 7. checksum ------------------------------------------------------
set HASHFILE=%SETUPEXE%.sha256
powershell -NoProfile -ExecutionPolicy Bypass -File "%RELPS%" -Step hash -Setup "%SETUPEXE%" -Out "%HASHFILE%"
if errorlevel 1 echo Could not write the SHA-256 file. The installer itself is fine.

echo.
echo Done. Files for the GitHub Release:
for %%f in ("%SETUPEXE%") do echo   %%~ff  (%%~zf bytes^)
for %%f in ("%NOTES%") do echo   %%~ff  (paste as the release description^)
if exist "%HASHFILE%" for %%f in ("%HASHFILE%") do echo   %%~ff
echo.
echo Next: tag v%VER%, create the Release on GitHub, paste the notes, attach the exe.
echo.
echo Reminder: the installer is not signed. The first run shows a SmartScreen
echo warning; click More info then Run anyway.
goto END

:NOSDK
echo The .NET SDK is not installed.
echo     winget install Microsoft.DotNet.SDK.10
goto END

:NONOTES
echo.
echo CHANGELOG.md has no section for %VER%.
echo Rename "## [Unreleased]" to "## [%VER%] - YYYY-MM-DD", add a new empty
echo "## [Unreleased]" above it, and run this again. See docs\releasing.md.
goto END

:TESTFAILED
echo.
echo CHECKS FAILED. The installer was not built. Details are in build\test.log.
echo.
type build\test.log
goto END

:NOVER
echo Could not read the version from Directory.Build.props.
echo Expected a line like:  ^<Version^>1.0.0^</Version^>
goto END

:NOISCC
echo Inno Setup was not found. Looked in:
echo     %%LOCALAPPDATA%%\Programs\Inno Setup 6
echo     %%ProgramFiles(x86)%%\Inno Setup 6
echo     %%ProgramFiles%%\Inno Setup 6
echo     the Inno Setup 6 uninstall entry in the registry
echo     PATH
echo.
echo Install it with:  winget install JRSoftware.InnoSetup
echo Or find it with:  where /r C:\ ISCC.exe
goto END

:PUBFAILED
echo.
echo PUBLISH FAILED. Details are in %LOG%.
echo.
type "%LOG%"
goto END

:NOEXE
echo.
echo Publish reported success but 2x2Explorer.exe was not found in %PUBDIR%.
goto END

:ISSFAILED
echo.
echo INNO SETUP FAILED. Details are in %LOG%.
echo.
type "%LOG%"
goto END

:NOSETUP
echo.
echo Inno Setup reported success but the installer was not found:
echo     %SETUPEXE%
goto END

:TRYDIR
rem Sets ISCC when the folder given holds ISCC.exe. Tolerates a trailing slash.
if defined ISCC exit /b
set "D=%~1"
if "%D:~-1%"=="\" set "D=%D:~0,-1%"
if exist "%D%\ISCC.exe" set "ISCC=%D%\ISCC.exe"
exit /b

:TRYREG
rem Reads InstallLocation from an uninstall key and tries that folder.
if defined ISCC exit /b
for /f "tokens=2,*" %%a in ('reg query %1 /v InstallLocation 2^>nul ^| findstr /i "REG_SZ"') do call :TRYDIR "%%b"
exit /b

:END
echo.
if /i not "%~1"=="/nopause" pause
endlocal
