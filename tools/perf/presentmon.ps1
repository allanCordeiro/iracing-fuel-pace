<#
.SYNOPSIS
  Captures the frames of iRacing with PresentMon for the PERF-01 measurement (see tools/perf/README.md).
.DESCRIPTION
  Does not download anything: -PresentMon is the path of a PresentMon console exe you got yourself
  (https://github.com/GameTechDev/PresentMon/releases). Runs it on iRacingSim64DX11.exe for -Minutes minutes and writes
  <Label>-<yyyyMMdd-HHmmss>.csv to %APPDATA%\FuelPace\perf\ (the diagnostic bundle includes that folder).
  Start iRacing and be on track before running; PresentMon usually needs an elevated (administrator) PowerShell.
.EXAMPLE
  .\presentmon.ps1 -PresentMon C:\Tools\PresentMon.exe -Label com
  .\presentmon.ps1 -PresentMon C:\Tools\PresentMon.exe -Label sem -Minutes 5
#>
param(
    [Parameter(Mandatory)][string]$PresentMon,
    [Parameter(Mandatory)][ValidateSet('com', 'sem')][string]$Label,
    [int]$Minutes = 5,
    [string]$ProcessName = 'iRacingSim64DX11.exe'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PresentMon -PathType Leaf)) { throw "PresentMon not found: $PresentMon" }
if ($Minutes -lt 1) { throw '-Minutes must be at least 1' }

$dir = Join-Path $env:APPDATA 'FuelPace\perf'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$csv = Join-Path $dir ("{0}-{1}.csv" -f $Label, (Get-Date -Format 'yyyyMMdd-HHmmss'))

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
if (-not $ok) { throw "PresentMon produced no CSV (iRacing running and on track? running as administrator?)" }
Write-Host "Pronto: $csv"
