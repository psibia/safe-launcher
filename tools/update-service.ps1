# Обновление службы «Безопасный лаунчер — сеть» и ядра VPN на этом компьютере из исходников (для разработки;
# пользователям службу ставит установщик). Собирает службу, при необходимости ядро, и ставит их в
# C:\Program Files\SafeLauncher\core. Права администратора Windows спросит сама, только для копирования.
# Перед запуском закройте лаунчер. Запуск: двойной щелчок по tools\update-service.cmd.
param(
    [string]$InstallFrom
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'SafeLauncherNet'
$Dest = Join-Path $env:ProgramFiles 'SafeLauncher\core'
$CoreExe = 'launcher-core.exe'

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') завершился с кодом $LASTEXITCODE" }
}

# Версия ядра, например 1.14.3-safelauncher.6; null — не запускается.
function Get-CoreVersion([string]$Exe) {
    if (-not (Test-Path $Exe)) { return $null }
    try {
        $line = (& $Exe version 2>$null | Select-Object -First 1)
        if ($line -match 'version\s+(\S+)') { return $Matches[1] }
    } catch { }
    return $null
}

# ---- Вторая часть: с правами администратора копирует собранное и перезапускает службу.
if ($InstallFrom) {
    $log = Join-Path $InstallFrom 'install.log'
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        # Процесс службы может завершаться ещё пару секунд после остановки.
        for ($i = 0; $i -lt 20 -and (Get-Process -Name 'SafeLauncher.Service', 'launcher-core' -ErrorAction SilentlyContinue); $i++) {
            Start-Sleep -Milliseconds 500
        }

        New-Item -ItemType Directory -Force -Path $Dest | Out-Null
        # Служба теперь один exe: файлы прежней сборки из нескольких файлов не нужны.
        Get-ChildItem -Path (Join-Path $Dest '*') -File -Include '*.dll', '*.json', '*.pdb' | Remove-Item -Force
        Get-ChildItem -Path $InstallFrom -File | Where-Object { $_.Name -ne 'install.log' } |
            Copy-Item -Destination $Dest -Force
        [Console]::OutputEncoding = [Text.Encoding]::UTF8 # служба пишет по-русски в UTF-8
        $output = & (Join-Path $Dest 'SafeLauncher.Service.exe') --install 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Служба не установилась: $output" }
        Set-Content -Path $log -Value "OK`n$output" -Encoding UTF8
        exit 0
    } catch {
        Set-Content -Path $log -Value "ОШИБКА`n$($_.Exception.Message)" -Encoding UTF8
        exit 1
    }
}

# ---- Первая часть: сборка с обычными правами.
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Stage = Join-Path $env:TEMP 'safe-launcher-service-update'
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null

if (Get-Process -Name 'KillSwitchLauncher' -ErrorAction SilentlyContinue) {
    Write-Host 'Лаунчер запущен: закройте его (в трее «Закрыть лаунчер») и запустите обновление снова.' -ForegroundColor Yellow
    exit 1
}

Write-Host '[1/3] Сборка службы...'
Invoke-Checked dotnet @('publish', (Join-Path $Root 'SafeLauncher.Service\SafeLauncher.Service.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=none', '-o', $Stage, '--nologo', '-v', 'q')

Write-Host '[2/3] Ядро VPN...'
$buildScript = Join-Path $Root 'core\build.ps1'
if ((Get-Content -Raw $buildScript) -notmatch "\`$Version\s*=\s*'([^']+)'") { throw 'Не нашёл версию ядра в core\build.ps1' }
$required = $Matches[1]
$installed = Get-CoreVersion (Join-Path $Dest $CoreExe)
$prebuilt = Join-Path $Root "prototype\RouteCheck\bin\$CoreExe"
$prebuiltZip = Join-Path $Root 'prototype\RouteCheck\bin\launcher-core.zip'
if ($installed -ne $required -and (Get-CoreVersion $prebuilt) -ne $required -and (Test-Path $prebuiltZip)) {
    # Готовое ядро архивом (без Go): распаковываем рядом и проверяем версию.
    $unzipped = Join-Path $env:TEMP 'safe-launcher-core-zip'
    if (Test-Path $unzipped) { Remove-Item -Recurse -Force $unzipped }
    Expand-Archive -Path $prebuiltZip -DestinationPath $unzipped -Force
    $prebuilt = Join-Path $unzipped $CoreExe
}
if ($installed -eq $required) {
    Write-Host "      установлено нужное ядро $installed, не трогаю"
} elseif ((Get-CoreVersion $prebuilt) -eq $required) {
    Write-Host "      беру готовое ядро $required из prototype\RouteCheck\bin"
    Copy-Item $prebuilt $Stage
} else {
    Write-Host "      установлено ядро $(if ($installed) { $installed } else { 'нет' }), нужно $required — собираю (нужны Git и Go)..."
    & $buildScript -Out $Stage
    if ((Get-CoreVersion (Join-Path $Stage $CoreExe)) -ne $required) { throw 'Ядро не собралось, подробности выше.' }
}

Write-Host '[3/3] Установка в Program Files (Windows спросит права администратора)...'
try {
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-InstallFrom', "`"$Stage`"")
} catch {
    Write-Host 'Права администратора не получены — служба не обновлена.' -ForegroundColor Red
    exit 1
}

$result = Get-Content -Path (Join-Path $Stage 'install.log') -ErrorAction SilentlyContinue
if ($process.ExitCode -eq 0) {
    Write-Host "Готово: служба и ядро обновлены ($Dest). Ядро: $(Get-CoreVersion (Join-Path $Dest $CoreExe))." -ForegroundColor Green
} else {
    Write-Host "Не получилось: $($result -join ' ')" -ForegroundColor Red
    exit 1
}
