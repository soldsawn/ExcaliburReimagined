@echo off
REM Self-elevating launcher: runs the fan service in an elevated console (real EC).
REM Ctrl+C stops it and hands both fans back to the EC (0xFF).
REM Usage:  run-fan-service.cmd            real EC
REM         run-fan-service.cmd --simulate no EC access (no admin needed, but elevates anyway)
setlocal
set "EXE=%~dp0..\..\src\Excalibur.FanService\bin\Debug\net9.0-windows\ExcaliburFanService.exe"

net session >nul 2>&1
if %errorlevel%==0 goto run

powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/k','\"\"%~f0\" %*\"'"
exit /b

:run
"%EXE%" %*
echo.
echo ---- servis durdu (cikis kodu %errorlevel%), pencereyi kapatabilirsiniz ----
