<#
.SYNOPSIS
  Captures the frames of iRacing with PresentMon for the PERF-01 measurement (see tools/perf/README.md).
.DESCRIPTION
  Does not download anything: PresentMon is a console exe you got yourself
  (https://github.com/GameTechDev/PresentMon/releases). Without -PresentMon the script looks for PresentMon*.exe in its own
  folder, then in your Downloads folder. Runs it on iRacingSim64DX11.exe for -Minutes minutes and writes
  <Label>-<yyyyMMdd-HHmmss>.csv to %APPDATA%\FuelPace\perf\ (the diagnostic bundle includes that folder).
  Start iRacing and be on track before running; PresentMon needs an elevated (administrator) PowerShell.
  Windows blocks scripts by default, so run it through -ExecutionPolicy Bypass (only for this run).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\presentmon.ps1 -Label sem
  powershell -ExecutionPolicy Bypass -File .\presentmon.ps1 -Label com -PresentMon C:\Tools\PresentMon-2.6.0-x64.exe
#>
param(
    [Parameter(Mandatory)][ValidateSet('com', 'sem')][string]$Label,
    [string]$PresentMon,
    [int]$Minutes = 5,
    [string]$ProcessName = 'iRacingSim64DX11.exe'
)
$ErrorActionPreference = 'Stop'

if (-not $PresentMon) {
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    $found = foreach ($dir in @($PSScriptRoot, $downloads)) {
        if (Test-Path -LiteralPath $dir) {
            Get-ChildItem -LiteralPath $dir -Filter 'PresentMon*.exe' -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending
        }
    }
    $first = @($found)[0]
    if (-not $first) {
        throw "PresentMon nao encontrado. Coloque o PresentMon-<versao>-x64.exe na pasta do FuelPace ($PSScriptRoot) ou passe -PresentMon <caminho do exe>."
    }
    $PresentMon = $first.FullName
}
if (-not (Test-Path -LiteralPath $PresentMon -PathType Leaf)) { throw "PresentMon nao encontrado: $PresentMon" }
if ($Minutes -lt 1) { throw '-Minutes deve ser pelo menos 1' }

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning 'Este PowerShell nao esta como administrador: o PresentMon costuma falhar assim.'
}
if (-not (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ProcessName)) -ErrorAction SilentlyContinue)) {
    Write-Warning "$ProcessName nao esta rodando: abra o iRacing e entre na pista antes."
}

$dir = Join-Path $env:APPDATA 'FuelPace\perf'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$csv = Join-Path $dir ("{0}-{1}.csv" -f $Label, (Get-Date -Format 'yyyyMMdd-HHmmss'))

Write-Host "PresentMon: $PresentMon"
Write-Host "Capturando $ProcessName por $Minutes min -> $csv"
# PresentMon 2.x uses --options, 1.x uses -options: pick by the exe's version, and retry once with the other style on failure.
$major = (Get-Item -LiteralPath $PresentMon).VersionInfo.FileMajorPart
$styles = if ($major -ge 2) { @('--', '-') } else { @('-', '--') }
$ok = $false
foreach ($d in $styles) {
    $pmArgs = @("${d}process_name", $ProcessName, "${d}timed", ($Minutes * 60), "${d}output_file", $csv, "${d}terminate_after_timed")
    & $PresentMon @pmArgs
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $csv)) { $ok = $true; break }
    Write-Warning "PresentMon failed with '${d}' options (exit $LASTEXITCODE)."
}
if (-not $ok) { throw "PresentMon nao gerou o CSV (iRacing aberto e na pista? PowerShell como administrador?)" }
Write-Host "Pronto: $csv"
