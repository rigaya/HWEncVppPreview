@echo off
setlocal

rem Build the single-file HWEncVppPreview.exe and HWEncVppPreviewCLI.exe (win-x64) the same way as CI.
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
    echo ERROR: App publish failed. 1>&2
    exit /b 1
)

dotnet publish "%ROOT%src\HWEncVppPreview.Cli\HWEncVppPreview.Cli.csproj" -c Release -p:PublishProfile=Distribution -r %RID% -o "%OUT%"
if errorlevel 1 (
    echo ERROR: Cli publish failed. 1>&2
    exit /b 1
)

rem Same check as CI: the App exe must start and print its version.
rem Pipe stdout through more so that a WinExe app's output is awaited.
set "VERSION_OUT="
for /f "usebackq delims=" %%V in (`"%OUT%\HWEncVppPreview.exe" --version ^| more`) do set "VERSION_OUT=%%V"
if not defined VERSION_OUT (
    echo ERROR: cannot get the version from the published App exe. 1>&2
    exit /b 1
)

rem Cli engine must print READY then exit on quit.
set "CLI_TMP=%TEMP%\HWEncVppPreviewCLI-publish-check"
if exist "%CLI_TMP%" rmdir /s /q "%CLI_TMP%"
mkdir "%CLI_TMP%" >nul 2>&1
> "%CLI_TMP%\quit.json" echo {"cmd":"quit"}
"%OUT%\HWEncVppPreviewCLI.exe" --amatsukaze-engine --tmp "%CLI_TMP%" --amatsukaze-dir "%CLI_TMP%" < "%CLI_TMP%\quit.json" > "%CLI_TMP%\out.txt"
if errorlevel 1 (
    echo ERROR: published CLI exited with an error. 1>&2
    rmdir /s /q "%CLI_TMP%" >nul 2>&1
    exit /b 1
)
set "READY_OUT="
set /p READY_OUT=<"%CLI_TMP%\out.txt"
rmdir /s /q "%CLI_TMP%" >nul 2>&1
if /I not "%READY_OUT%"=="AMTPREVIEW READY version=2" (
    echo ERROR: published CLI did not print AMTPREVIEW READY. got: [%READY_OUT%] 1>&2
    exit /b 1
)

echo.
echo Published: %OUT%\HWEncVppPreview.exe
echo Published: %OUT%\HWEncVppPreviewCLI.exe
echo %VERSION_OUT%
echo %READY_OUT%
exit /b 0
