@echo off
rem Update the "Safe launcher - network" service and VPN core from sources (developer machine).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-service.ps1"
pause
