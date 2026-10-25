@echo off
rem Launches the Debug build of PaRuX.
set "EXE=%~dp0src\PaRuX.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\PaRuX.exe"
if not exist "%EXE%" (
  echo PaRuX.exe not found. Run build.cmd first.
  echo Looked for: %EXE%
  pause
  exit /b 1
)
start "" "%EXE%"
