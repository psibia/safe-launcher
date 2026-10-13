@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo [1/3] Публикация лаунчера (Release, win-x64, self-contained, один exe)...
if exist publish rmdir /s /q publish
dotnet publish "..\KillSwitchLauncher\KillSwitchLauncher.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -o publish
if errorlevel 1 goto :fail

echo [2/3] Служба «Безопасный лаунчер — сеть» и ядро VPN...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-core.ps1"
if errorlevel 1 goto :fail

echo [3/3] Сборка установщика Inno Setup...
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Не найден Inno Setup 6. Установите его с https://jrsoftware.org/isdl.php
  echo или откройте KillSwitchLauncher.iss в Inno Setup и нажмите Compile.
  goto :fail
)
"%ISCC%" KillSwitchLauncher.iss
if errorlevel 1 goto :fail

echo.
echo Готово. Установщик лежит в папке Output:
dir /b Output\*.exe
pause
exit /b 0

:fail
echo.
echo Сборка не удалась, подробности выше.
pause
exit /b 1
