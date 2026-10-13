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
$Version = '1.14.3-safelauncher.6'
$Tags = 'with_gvisor,with_quic,with_wireguard,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0'
$Work = Join-Path $env:TEMP 'safe-launcher-core-src'

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') завершился с кодом $LASTEXITCODE" }
}

if (Test-Path (Join-Path $Work '.git')) {
    Invoke-Checked git @('-C', $Work, 'fetch', '--depth', '1', 'origin', 'tag', $Tag)
    Invoke-Checked git @('-C', $Work, 'checkout', '-f', $Tag)
    Invoke-Checked git @('-C', $Work, 'clean', '-fdx')
} else {
    Invoke-Checked git @('clone', '--depth', '1', '--branch', $Tag, $Upstream, $Work)
}

foreach ($patch in Get-ChildItem (Join-Path $PSScriptRoot 'patches') -Filter *.patch | Sort-Object Name) {
    # На случай, если Git при выгрузке всё же поменял концы строк на CRLF
    $text = [IO.File]::ReadAllText($patch.FullName) -replace "`r`n", "`n"
    $lf = Join-Path $env:TEMP $patch.Name
    [IO.File]::WriteAllText($lf, $text, (New-Object Text.UTF8Encoding $false))
    Invoke-Checked git @('-C', $Work, 'apply', $lf)
    Write-Host "Применён $($patch.Name)"
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$exe = Join-Path (Resolve-Path $Out) 'launcher-core.exe'
$env:GOTOOLCHAIN = 'go1.25.5'
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$ldflags = "-X github.com/sagernet/sing-box/constant.Version=$Version -X runtime.godebugDefault=multipathtcp=0,tlssha1=1 -checklinkname=0 -s -w -buildid="
Push-Location $Work
try {
    Invoke-Checked go @('build', '-trimpath', '-tags', $Tags, '-ldflags', $ldflags, '-o', $exe, './cmd/sing-box')
} finally {
    Pop-Location
}
Write-Host "Готово: $exe"
