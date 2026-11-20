@echo off
setlocal

rem Build the single-file HWEncVppPreview.exe (win-x64) the same way as CI.
rem Usage: publish.bat [output-dir]   (default: publish\win-x64)
rem NOTE: keep this file ASCII-only. cmd.exe misparses non-ASCII text depending on the code page.

set "RID=win-x64"
set "ROOT=%~dp0"
if "%~1"=="" (
    set "OUT=%ROOT%publish\%RID%"
) else (
    set "OUT=%~f1"
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet not found. Install .NET SDK 10. 1>&2
    exit /b 1
)

rem Recreate the output dir so that stale files never remain.
if exist "%OUT%" rmdir /s /q "%OUT%"
if errorlevel 1 (
    echo ERROR: cannot remove output dir: %OUT% 1>&2
    exit /b 1
)

dotnet publish "%ROOT%src\HWEncVppPreview.App\HWEncVppPreview.App.csproj" -c Release -p:PublishProfile=Distribution -r %RID% -o "%OUT%"
if errorlevel 1 (
    echo ERROR: dotnet publish failed. 1>&2
    exit /b 1
)

rem Same check as CI: the exe must start and print its version.
rem Pipe stdout through more so that a WinExe app's output is awaited.
set "VERSION_OUT="
for /f "usebackq delims=" %%V in (`"%OUT%\HWEncVppPreview.exe" --version ^| more`) do set "VERSION_OUT=%%V"
if not defined VERSION_OUT (
    echo ERROR: cannot get the version from the published exe. 1>&2
    exit /b 1
)

echo.
echo Published: %OUT%\HWEncVppPreview.exe
echo %VERSION_OUT%
exit /b 0
