@echo off
REM Self-elevating launcher: runs the fan guardian in an elevated console (real EC).
REM It only ever writes 0xFF (EC automatic). Ctrl+C stops it.
setlocal
set "EXE=%~dp0..\..\src\Excalibur.FanGuardian\bin\Debug\net9.0-windows\ExcaliburFanGuardian.exe"

net session >nul 2>&1
if %errorlevel%==0 goto run

powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/k','\"\"%~f0\"\"'"
exit /b

:run
"%EXE%"
echo.
echo ---- bekci durdu (cikis kodu %errorlevel%), pencereyi kapatabilirsiniz ----
