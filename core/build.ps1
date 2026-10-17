# Сборка ядра VPN лаунчера: sing-box указанной версии + правки из patches\.
# Нужны Git и Go (версию Go нужной сборки он скачает сам). Запуск из PowerShell:
#   .\core\build.ps1                      — положит launcher-core.exe в prototype\RouteCheck\bin
#   .\core\build.ps1 -Out C:\где-то\папка — в другую папку
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\prototype\RouteCheck\bin')
)

$ErrorActionPreference = 'Stop'
$Upstream = 'https://github.com/SagerNet/sing-box.git'
$Tag = 'v1.14.3'
$Version = '1.14.3-safelauncher.8'
$Tags = 'with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0'
$Work = Join-Path $env:TEMP 'safe-launcher-core-src'
# Модули Go, которые sing-box берёт со стороны и которые тоже правятся: папка в patches\ → модуль.
# Версия модуля — та, что записана в go.mod sing-box.
$Modules = [ordered]@{
    'wireguard-go' = 'github.com/sagernet/wireguard-go'
    'sing-tun'     = 'github.com/sagernet/sing-tun'
}
# Рядом с $Work: go.mod sing-box ссылается на эту папку относительным путём
$ModulesWork = Join-Path $env:TEMP 'safe-launcher-core-modules'

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') завершился с кодом $LASTEXITCODE" }
}

# Применяет правки *.patch из папки $Directory к исходникам в $Target (по порядку имён).
function Invoke-Patches([string]$Directory, [string]$Target) {
    foreach ($patch in Get-ChildItem $Directory -Filter *.patch | Sort-Object Name) {
        # На случай, если Git при выгрузке всё же поменял концы строк на CRLF
        $text = [IO.File]::ReadAllText($patch.FullName) -replace "`r`n", "`n"
        $lf = Join-Path $env:TEMP $patch.Name
        [IO.File]::WriteAllText($lf, $text, (New-Object Text.UTF8Encoding $false))
        Invoke-Checked git @('-C', $Target, 'apply', $lf)
        Write-Host "Применён $($patch.Name)"
    }
}

if (Test-Path (Join-Path $Work '.git')) {
    Invoke-Checked git @('-C', $Work, 'fetch', '--depth', '1', 'origin', 'tag', $Tag)
    Invoke-Checked git @('-C', $Work, 'checkout', '-f', $Tag)
    Invoke-Checked git @('-C', $Work, 'clean', '-fdx')
} else {
    Invoke-Checked git @('clone', '--depth', '1', '--branch', $Tag, $Upstream, $Work)
}

Invoke-Patches (Join-Path $PSScriptRoot 'patches') $Work

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$exe = Join-Path (Resolve-Path $Out) 'launcher-core.exe'
$env:GOTOOLCHAIN = 'go1.25.5'
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$ldflags = "-X github.com/sagernet/sing-box/constant.Version=$Version -X runtime.godebugDefault=multipathtcp=0,tlssha1=1 -checklinkname=0 -s -w -buildid="
Push-Location $Work
try {
    # Исходники модуля из кэша Go копируются в отдельную папку, к ним применяются правки,
    # и sing-box собирается с этой копией (replace в go.mod; go.mod вернётся к исходному при следующей сборке).
    if (Test-Path $ModulesWork) { Remove-Item -Recurse -Force $ModulesWork }
    New-Item -ItemType Directory -Force -Path $ModulesWork | Out-Null
    foreach ($name in $Modules.Keys) {
        $path = $Modules[$name]
        $json = & go mod download -json $path
        if ($LASTEXITCODE -ne 0) { throw "go mod download $path завершился с кодом $LASTEXITCODE" }
        $module = ($json -join "`n") | ConvertFrom-Json
        $copy = Join-Path $ModulesWork $name
        Copy-Item -LiteralPath $module.Dir -Destination $copy -Recurse
        # В кэше Go файлы только для чтения
        Get-ChildItem -LiteralPath $copy -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
        Invoke-Checked git @('init', '-q', $copy)
        Write-Host "$path $($module.Version):"
        Invoke-Patches (Join-Path $PSScriptRoot "patches\$name") $copy
        # Путь от папки sing-box: полный путь (в нём имя пользователя Windows) записался бы в launcher-core.exe
        Invoke-Checked go @('mod', 'edit', "-replace=$path=../$(Split-Path $ModulesWork -Leaf)/$name")
    }
    Invoke-Checked go @('build', '-trimpath', '-tags', $Tags, '-ldflags', $ldflags, '-o', $exe, './cmd/sing-box')
} finally {
    Pop-Location
}
Write-Host "Готово: $exe"
