# Готовит для установщика папку installer\publish-core: служба «Безопасный лаунчер — сеть» (один exe, .NET внутри),
# ядро VPN launcher-core.exe нужной версии и файлы лицензии ядра. Вызывается из build-installer.cmd.
#
# Откуда берётся ядро (первое подходящее по версии из core\build.ps1):
#   1. installer\launcher-core.exe — можно положить сюда готовое;
#   2. уже установленное в C:\Program Files\SafeLauncher\core;
#   3. prototype\RouteCheck\bin\launcher-core.exe или архив prototype\RouteCheck\bin\launcher-core.zip;
#   4. иначе собирается скриптом core\build.ps1 (нужны Git и Go).
$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Out = Join-Path $PSScriptRoot 'publish-core'
$CoreExe = 'launcher-core.exe'

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') завершился с кодом $LASTEXITCODE" }
}

# Версия ядра, например 1.14.3-safelauncher.6; null — файла нет или он не запускается.
function Get-CoreVersion([string]$Exe) {
    if (-not $Exe -or -not (Test-Path $Exe)) { return $null }
    try {
        $line = (& $Exe version 2>$null | Select-Object -First 1)
        if ($line -match 'version\s+(\S+)') { return $Matches[1] }
    } catch { }
    return $null
}

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Write-Host '      служба...'
# Один exe со встроенным .NET: на компьютере пользователя .NET ставить не нужно.
Invoke-Checked dotnet @('publish', (Join-Path $Root 'SafeLauncher.Service\SafeLauncher.Service.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=none',
    '-o', $Out, '--nologo', '-v', 'q')
if (-not (Test-Path (Join-Path $Out 'SafeLauncher.Service.exe'))) { throw 'Служба не опубликовалась.' }

Write-Host '      ядро VPN...'
$buildScript = Join-Path $Root 'core\build.ps1'
if ((Get-Content -Raw $buildScript) -notmatch "\`$Version\s*=\s*'([^']+)'") { throw 'Не нашёл версию ядра в core\build.ps1' }
$required = $Matches[1]

$candidates = @(
    (Join-Path $PSScriptRoot $CoreExe),
    (Join-Path $env:ProgramFiles "SafeLauncher\core\$CoreExe"),
    (Join-Path $Root "prototype\RouteCheck\bin\$CoreExe")
)
$found = $candidates | Where-Object { (Get-CoreVersion $_) -eq $required } | Select-Object -First 1

$zip = Join-Path $Root "prototype\RouteCheck\bin\launcher-core.zip"
if (-not $found -and (Test-Path $zip)) {
    $unzipped = Join-Path $env:TEMP 'safe-launcher-core-zip'
    if (Test-Path $unzipped) { Remove-Item -Recurse -Force $unzipped }
    Expand-Archive -Path $zip -DestinationPath $unzipped -Force
    if ((Get-CoreVersion (Join-Path $unzipped $CoreExe)) -eq $required) { $found = Join-Path $unzipped $CoreExe }
}

if ($found) {
    Write-Host "      беру готовое ядро $required`: $found"
    Copy-Item $found (Join-Path $Out $CoreExe)
} else {
    Write-Host "      готового ядра $required нет — собираю (нужны Git и Go)..."
    & $buildScript -Out $Out
}

$built = Get-CoreVersion (Join-Path $Out $CoreExe)
if ($built -ne $required) { throw "Ядро не той версии: $built, нужно $required." }

Copy-Item (Join-Path $Root 'core\COPYING.txt'), (Join-Path $Root 'core\NOTICE.txt') $Out
Write-Host "      готово: служба и ядро $required в $Out"
