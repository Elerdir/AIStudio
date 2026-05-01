@echo off
setlocal enabledelayedexpansion

echo.
echo  =====================================================
echo   AI Studio - Build Installer
echo  =====================================================
echo.

cd /d "%~dp0"

:: ---- 0. Prerequisites -------------------------------------------------------

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [CHYBA] dotnet neni v PATH.
    echo         Nainstaluj .NET 10 SDK: https://dotnet.microsoft.com/download
    goto :fail
)
for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo [OK] .NET SDK %DOTNET_VER%

:: Inno Setup - hledame ISCC.exe (IS 7, 6, 5)
set ISCC=

:: Nejdrive zkus PATH
where ISCC.exe >nul 2>&1
if not errorlevel 1 (
    for /f "tokens=*" %%p in ('where ISCC.exe') do if not defined ISCC set "ISCC=%%p"
)

:: Pak hledej na pevnych cestach
if not defined ISCC if exist "C:\Program Files\Inno Setup 7\ISCC.exe"       set "ISCC=C:\Program Files\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "C:\Program Files (x86)\Inno Setup 7\ISCC.exe" set "ISCC=C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "C:\Program Files\Inno Setup 6\ISCC.exe"       set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "C:\Program Files\Inno Setup 5\ISCC.exe"       set "ISCC=C:\Program Files\Inno Setup 5\ISCC.exe"
if not defined ISCC if exist "C:\Program Files (x86)\Inno Setup 5\ISCC.exe" set "ISCC=C:\Program Files (x86)\Inno Setup 5\ISCC.exe"

if not defined ISCC (
    echo.
    echo [CHYBA] Inno Setup nenalezen. Zkus spustit:
    echo   where ISCC.exe
    echo a nastav cestu rucne v promenne ISCC v tomto skriptu.
    goto :fail
)
echo [OK] Inno Setup: %ISCC%

:: ---- 1. dotnet publish -------------------------------------------------------
echo.
echo [1/2] dotnet publish (win-x64, self-contained, Release)...
echo       Muze trvat 1-2 minuty...
echo.

set "PUBLISH_DIR=%~dp0publish\win-x64"

dotnet publish "AIStudio.App\AIStudio.App.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false --output "%PUBLISH_DIR%" --nologo

if errorlevel 1 (
    echo.
    echo [CHYBA] dotnet publish selhal.
    goto :fail
)
echo [OK] Publish hotov: %PUBLISH_DIR%

:: ---- 2. Inno Setup -----------------------------------------------------------
echo.
echo [2/2] Inno Setup kompilace...
echo.

"%ISCC%" "installer\AIStudio.iss"
if errorlevel 1 (
    echo.
    echo [CHYBA] Inno Setup selhal.
    goto :fail
)

:: ---- Done --------------------------------------------------------------------
echo.
echo  =====================================================
echo   Hotovo! Installer je ve slozce dist\
echo  =====================================================
echo.
if exist "%~dp0dist" explorer "%~dp0dist"
goto :done

:fail
echo.
pause
exit /b 1

:done
pause
exit /b 0
