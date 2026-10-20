@echo off
REM Self-elevating launcher for the read-only EC probe.
REM Usage:  run-ec-probe.cmd <label>      e.g.  run-ec-probe.cmd idle
setlocal
set "LABEL=%~1"
if "%LABEL%"=="" set "LABEL=dump"
set "EXE=%~dp0..\..\src\Excalibur.EcProbe\bin\Debug\net9.0-windows\Excalibur.EcProbe.exe"

net session >nul 2>&1
if %errorlevel%==0 goto run

REM Not elevated yet: relaunch this script as administrator, keeping the window open.
powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/k','\"\"%~f0\" %LABEL%\"'"
exit /b

:run
"%EXE%" "%LABEL%"
echo.
echo ---- bitti, pencereyi kapatabilirsiniz ----
