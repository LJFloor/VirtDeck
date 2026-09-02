@echo off
setlocal enabledelayedexpansion
rem ===========================================================================
rem  publish.bat - build VirtDeck and produce the Windows installer.
rem
rem  Steps:
rem    1) dotnet publish (self-contained x64 -> publish\win-x64)
rem    2) compile installer\VirtDeck.iss with Inno Setup (ISCC)
rem       -> installer\output\VirtDeckSetup-<ver>.exe
rem
rem  Run from anywhere; it cd's to its own folder (the repo root).
rem ===========================================================================

rem Always operate from the repo root (the directory this script lives in).
pushd "%~dp0"

set "CONFIG=Release"
set "RID=win-x64"
set "PUBLISH_DIR=publish\win-x64"
set "PROJECT=VirtDeck.Avalonia\VirtDeck.Avalonia.csproj"
set "ISS=installer\VirtDeck.iss"
set "SRC_ZIP=publish\SpiceClient-src.zip"

echo.
echo === [1/3] Publishing %PROJECT% (%CONFIG%, %RID%, self-contained) ===
dotnet publish "%PROJECT%" -c %CONFIG% -r %RID% --self-contained true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo.
    echo ERROR: dotnet publish failed.
    goto :fail
)

rem Drop the debug symbols: they are dead weight in a release artifact and one of
rem them (SkiaSharp's) is about 80 MB on its own.
del /s /q "%PUBLISH_DIR%\*.pdb" >nul 2>nul

echo.
echo === [2/3] Packaging SpiceClient source (LGPL corresponding source) ===
rem Stage a clean copy of the SpiceClient project (source only, no bin/obj/.vs), then zip it.
rem The installer ships this zip next to the app so the binary is "accompanied by source".
if not exist "publish" md "publish"
if exist "%SRC_ZIP%" del /q "%SRC_ZIP%"
set "SRC_STAGE=%TEMP%\SpiceClient-src-%RANDOM%%RANDOM%"
robocopy "SpiceClient" "%SRC_STAGE%\SpiceClient" /E /XD bin obj .vs >nul
if errorlevel 8 (
    echo.
    echo ERROR: failed to stage SpiceClient source.
    goto :fail
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%SRC_STAGE%\SpiceClient' -DestinationPath '%SRC_ZIP%' -Force"
set "ZIP_RC=%errorlevel%"
rmdir /s /q "%SRC_STAGE%" 2>nul
if not "%ZIP_RC%"=="0" (
    echo.
    echo ERROR: failed to create %SRC_ZIP%.
    goto :fail
)

rem Locate the Inno Setup compiler (ISCC). Prefer PATH, then the usual installs.
set "ISCC="
where iscc >nul 2>nul && set "ISCC=iscc"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC (
    echo.
    echo ERROR: Inno Setup compiler ^(ISCC.exe^) not found.
    echo        Install Inno Setup 6 from https://jrsoftware.org/isdl.php
    goto :fail
)

echo.
echo === [3/3] Building installer from %ISS% ===
"%ISCC%" "%ISS%"
if errorlevel 1 (
    echo.
    echo ERROR: Inno Setup build failed.
    goto :fail
)

echo.
echo === Done. Installer written to installer\output\ ===
popd
endlocal
exit /b 0

:fail
popd
endlocal
exit /b 1
