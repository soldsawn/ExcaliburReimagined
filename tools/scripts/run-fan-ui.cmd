@echo off
REM Starts the fan test window (no admin needed). Add --sim to connect to the --simulate service.
start "" "%~dp0..\..\src\Excalibur.FanTestUi\bin\Debug\net9.0-windows\ExcaliburFanTestUi.exe" %*
