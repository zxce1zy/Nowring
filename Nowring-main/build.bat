@echo off
rem Builds a single Nowring.exe into the "out" folder. Needs .NET 8 SDK.
cd /d "%~dp0"
dotnet publish -c Release -o out
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Is .NET 8 SDK installed?
  pause
  exit /b 1
)
explorer out
