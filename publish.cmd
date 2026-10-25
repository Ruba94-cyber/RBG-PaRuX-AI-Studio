@echo off
setlocal
rem ------------------------------------------------------------------
rem  PaRuX official Windows publish entry point.
rem  MSI is the release channel; the old Inno Setup/portable release
rem  pipeline is intentionally retired.
rem ------------------------------------------------------------------
set "ROOT=%~dp0"

if not "%PARUX_ACCEPT_WIX7_EULA%"=="1" (
  echo WiX v7 terms have not been explicitly accepted for this shell.
  echo Read https://docs.firegiant.com/wix/osmf/
  echo Then set PARUX_ACCEPT_WIX7_EULA=1 if you accept them.
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\build-msi.ps1" %*
exit /b %ERRORLEVEL%
