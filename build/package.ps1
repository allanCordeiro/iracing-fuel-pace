param(
    [string]$Version = '0.1.0+local',
    [string]$OutDir = 'out'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

# File-name version: drop the "+build" metadata.
$fileVersion = $Version.Split('+')[0]
$out = Join-Path $root $OutDir
$stage = Join-Path $out 'stage'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Publish-Project([string]$proj, [string]$dir) {
    dotnet publish $proj -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:PublishSingleFile=false `
        "-p:InformationalVersion=$Version" -o $dir
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

$projects = @(
    'src/FuelPace.Agent/FuelPace.Agent.csproj',
    'src/FuelPace.Settings/FuelPace.Settings.csproj',
    'tools/FuelPace.Replay/FuelPace.Replay.csproj'
)
foreach ($p in $projects) { Publish-Project $p $stage }

# FakeSim goes only in the debug zip.
$fake = Join-Path $out 'fakesim'
Publish-Project 'tools/FuelPace.FakeSim/FuelPace.FakeSim.csproj' $fake

if (Test-Path 'assets/fonts/OFL.txt') { Copy-Item 'assets/fonts/OFL.txt' $stage }
if (Test-Path 'docs/windows-testing.md') { Copy-Item 'docs/windows-testing.md' (Join-Path $stage 'LEIA-ME.md') }

if (Test-Path 'tools/perf/presentmon.ps1') { Copy-Item 'tools/perf/presentmon.ps1' $stage }

$zip = Join-Path $out "FuelPace-$fileVersion-win-x64.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

# Debug zip: same content + FuelPace.debug marker + FakeSim.
Get-ChildItem $fake -File | Where-Object { -not (Test-Path (Join-Path $stage $_.Name)) } |
    ForEach-Object { Copy-Item $_.FullName $stage }
New-Item -ItemType File -Force -Path (Join-Path $stage 'FuelPace.debug') | Out-Null
$zipDebug = Join-Path $out "FuelPace-$fileVersion-win-x64-debug.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipDebug

Remove-Item $stage, $fake -Recurse -Force
Get-ChildItem $out -Filter *.zip | ForEach-Object { Write-Host "$($_.Name) $($_.Length)" }
