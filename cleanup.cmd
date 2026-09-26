@echo off
setlocal
cd /d "%~dp0"

rem Two jobs:
rem   1. Move files and folders that are no longer used into "Recycle Bin\".
rem      Nothing is deleted here - look inside and delete the folder yourself.
rem   2. Delete everything that is generated: bin, obj and the build\ folder
rem      (publish, dist and the logs all live in there). start.cmd, test.cmd
rem      and pack.cmd write them again. This also clears the pile of
rem      Explorer.App_*_wpftmp files that a WPF build leaves in obj.
rem
rem Job 1 is the record of what is up for deletion right now. Keep it current:
rem when a file stops being used, add a line; once it is gone, drop the line.
rem A line for a file that no longer exists only prints "skipped" forever.
rem ASCII only: the console code page differs between machines.

set TRASH=Recycle Bin
if not exist "%TRASH%" mkdir "%TRASH%"

echo [1/2] Moving unused files into "%TRASH%" ...
echo.

rem Already moved earlier: Explorer.sln, assets\MISC, the delivered icon zip
rem and PdfRenderer.cs. Add a call :MOVEONE / :MOVEDIR line when something dies.

rem 2026-09-26: fallback reader for the MYBOX N: drive. The real cause was the
rem System attribute filter, so nothing calls this file any more.
call :MOVEONE "src\Explorer.Shell\Interop\Win32DirectoryReader.cs"

echo.
echo [2/2] Deleting generated files ...
echo.

call :DELBUILD "src\Explorer.Core"
call :DELBUILD "src\Explorer.Shell"
call :DELBUILD "src\Explorer.App"
call :DELBUILD "tests\Explorer.Tests"
call :DELDIR "build"

rem Old spots, used before everything generated moved into build\ (2026-09-26).
call :DELDIR "publish"
call :DELDIR "dist"
call :DELFILE "run.log"
call :DELFILE "test.log"
call :DELFILE "pack.log"

rem If nothing was ever moved there, the folder has no reason to stay.
rd "%TRASH%" 2>nul

echo.
if exist "%TRASH%" echo Look inside "%TRASH%" and delete it whenever you like.
if not exist "%TRASH%" echo Nothing needed moving.
echo The next start.cmd will take longer: bin and obj are built again.
echo Run pack.cmd again to rebuild the installer.
echo.
pause
exit /b


:MOVEONE
if not exist "%~1" goto NOFILE
rem No trailing backslash on the destination: cmd mis-parses a quoted path that ends with one.
move /y "%~1" "%TRASH%" >nul
if errorlevel 1 goto FILEFAILED
echo   moved   : %~1
exit /b
:NOFILE
echo   skipped : %~1  ^(not found^)
exit /b
:FILEFAILED
echo   FAILED  : %~1  ^(in use or read-only?^)
exit /b

:MOVEDIR
rem %~nx1 is the last path segment, so "assets\MISC" lands as "Recycle Bin\MISC".
if not exist "%~1" goto NODIR
if exist "%TRASH%\%~nx1" goto DIRCLASH
move /y "%~1" "%TRASH%" >nul
if errorlevel 1 goto DIRFAILED
echo   moved   : %~1  ^(folder^)
exit /b
:NODIR
echo   skipped : %~1  ^(not found^)
exit /b
:DIRCLASH
echo   skipped : %~1  ^("%TRASH%\%~nx1" is already there - empty the bin first^)
exit /b
:DIRFAILED
echo   FAILED  : %~1  ^(in use or read-only?^)
exit /b

:DELFILE
if not exist "%~1" exit /b
del /q "%~1" 2>nul
if exist "%~1" echo   FAILED  : %~1  ^(in use?^)
if not exist "%~1" echo   deleted : %~1
exit /b

:DELBUILD
if not exist "%~1" exit /b
if exist "%~1\bin" rd /s /q "%~1\bin"
if exist "%~1\obj" rd /s /q "%~1\obj"
echo   cleaned : %~1
exit /b

:DELDIR
if not exist "%~1" exit /b
rd /s /q "%~1" 2>nul
if exist "%~1" echo   FAILED  : %~1  ^(in use?^)
if not exist "%~1" echo   deleted : %~1  ^(folder^)
exit /b
