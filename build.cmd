@echo off
setlocal enabledelayedexpansion
rem ---------------------------------------------------------------
rem  PaRuX - local debug build script
rem  Writes a full log to build.log
rem ---------------------------------------------------------------
set "ROOT=%~dp0"
set "LOG=%ROOT%build.log"
set "PROJ=%ROOT%src\PaRuX.App"
set "DOTNET=%LOCALAPPDATA%\PaRuX\dev\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

> "%LOG%" echo === PaRuX build ===
>> "%LOG%" echo Date: %DATE% %TIME%
>> "%LOG%" echo.

>> "%LOG%" echo --- dotnet --info ---
"%DOTNET%" --info >> "%LOG%" 2>&1
>> "%LOG%" echo.

>> "%LOG%" echo --- installed SDKs ---
"%DOTNET%" --list-sdks >> "%LOG%" 2>&1
>> "%LOG%" echo.

>> "%LOG%" echo --- dotnet build ---
pushd "%PROJ%"
"%DOTNET%" build -c Debug >> "%LOG%" 2>&1
set "BUILD_EXIT=!ERRORLEVEL!"
popd

>> "%LOG%" echo.
>> "%LOG%" echo BUILD_EXITCODE=!BUILD_EXIT!

type "%LOG%"
echo.
echo ------------------------------------------------
echo Full log: %LOG%
if "!BUILD_EXIT!"=="0" (
  echo BUILD SUCCEEDED. Run run.cmd to launch PaRuX.
) else (
  echo BUILD FAILED with exit code !BUILD_EXIT!.
)
echo ------------------------------------------------
pause
