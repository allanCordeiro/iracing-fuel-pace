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
# Options accepted by PresentMon 1.x and 2.x (single dash).
& $PresentMon -process_name $ProcessName -timed ($Minutes * 60) -output_file $csv -terminate_after_timed
if ($LASTEXITCODE -ne 0) { throw "PresentMon exited with code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $csv)) { throw "PresentMon wrote no CSV (iRacing running and on track?)" }
Write-Host "Pronto: $csv"
