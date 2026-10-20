@echo off
REM TEST ONLY: kills the fan service process the hard way (no clean stop, no 0xFF),
REM to simulate a crash. The guardian must then hand the fans back to the EC.
setlocal
net session >nul 2>&1
if %errorlevel%==0 goto run

powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/k','\"\"%~f0\"\"'"
exit /b

:run
echo %time%  taskkill /F ExcaliburFanService.exe
taskkill /F /IM ExcaliburFanService.exe
echo.
echo ---- bitti; bekci penceresine ve fanstate'e bakin ----
