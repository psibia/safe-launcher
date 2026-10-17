@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

if not exist license-hashes.txt (
  echo Нет файла installer\license-hashes.txt с хешами ключей лицензии.
  echo Этого файла нет в GitHub: положите его рядом с build-installer.cmd и запустите сборку снова.
  goto :fail
)

rem Ключ подписи выпусков: по подписи служба ставит обновление без запроса прав (docs\DETAILS.md, «Обновления»).
rem Закрытый ключ update-signing-key.pem есть только здесь, в GitHub его нет. Открытый update-public-key.pem встраивается в лаунчер.
echo [1/5] Ключ подписи выпусков...
if not exist update-public-key.pem (
  if exist update-signing-key.pem (
    echo Есть закрытый ключ update-signing-key.pem, но нет открытого update-public-key.pem.
    echo Верните update-public-key.pem из Git или из резервной копии и запустите сборку снова.
    goto :fail
  )
  dotnet run --project "..\tools\UpdateSigner" -c Release -- new-key update-signing-key.pem update-public-key.pem
  if errorlevel 1 goto :fail
  echo.
  echo Создан ключ подписи выпусков. Сделайте резервную копию файла installer\update-signing-key.pem
  echo и храните её отдельно: без этого ключа установленные лаунчеры не примут новые выпуски.
  echo Файл installer\update-public-key.pem открытый: добавьте его в Git вместе с остальными изменениями.
  echo.
)
if not exist update-signing-key.pem (
  echo Нет закрытого ключа installer\update-signing-key.pem. Его нет в GitHub: верните его из резервной копии.
  echo Новый ключ создавать нельзя: установленные лаунчеры принимают выпуски, подписанные только прежним ключом.
  goto :fail
)

echo [2/5] Публикация лаунчера (Release, win-x64, self-contained, один exe)...
if exist publish rmdir /s /q publish
dotnet publish "..\KillSwitchLauncher\KillSwitchLauncher.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -o publish
if errorlevel 1 goto :fail

echo [3/5] Служба «Безопасный лаунчер — сеть» и ядро VPN...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-core.ps1"
if errorlevel 1 goto :fail

echo [4/5] Сборка установщика Inno Setup...
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

echo [5/5] Подпись установщика...
set "SETUP="
for /f "delims=" %%F in ('dir /b /o-d Output\SafeLauncher-Setup-*.exe') do if not defined SETUP set "SETUP=Output\%%F"
if not defined SETUP (
  echo Не найден собранный установщик в папке Output.
  goto :fail
)
dotnet run --project "..\tools\UpdateSigner" -c Release -- sign "%SETUP%" update-signing-key.pem update-public-key.pem
if errorlevel 1 goto :fail

echo.
echo Готово. В папке Output лежат установщик и его подпись:
echo   %SETUP%
echo   %SETUP%.sig
echo К выпуску на GitHub приложите оба файла: без подписи лаунчеры обновятся только с запросом прав Windows.
pause
exit /b 0

:fail
echo.
echo Сборка не удалась, подробности выше.
pause
exit /b 1
