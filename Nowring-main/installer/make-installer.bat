@echo off
rem Local build of the installer. Needs .NET 8 SDK and Inno Setup 6 (https://jrsoftware.org/isdl.php)
setlocal
cd /d "%~dp0.."
if "%VER%"=="" set VER=1.0.0

dotnet publish -c Release -o out -p:Version=%VER%
if errorlevel 1 goto fail

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if exist "%ISCC%" goto build
echo Inno Setup 6.6 or newer not found. Install it from https://jrsoftware.org/isdl.php
pause
exit /b 1

:build
"%ISCC%" /DMyAppVersion=%VER% installer\Nowring.iss
if errorlevel 1 goto fail
explorer dist
exit /b 0

:fail
echo.
echo BUILD FAILED
pause
exit /b 1
