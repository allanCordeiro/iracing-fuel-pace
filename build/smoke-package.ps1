<#
.SYNOPSIS
  Runs what package.ps1 shipped, from the unpacked zips (CI, after Package).
.DESCRIPTION
  The tests run the exes from the test project's bin folder; the zips are a different layout (Agent, Settings and Replay
  published self-contained + ReadyToRun into one folder, FakeSim added to the debug zip). This script unpacks both zips into
  a temporary folder and, from there:
    1. Agent (debug zip) --exit-after 15: exit 0, Startup and Shutdown in its log.
    2. Settings: the main window appears; closing it ends the process with 0.
    3. Agent + FakeSim on private names (FUELPACE_IRSDK_MAP/EVENT, --map/--event): the Agent logs SourceOpened and no error.
    4. Replay diag on the Agent's folder of step 3: exit 0.
    5. PERF-05: the Agent of the NORMAL zip (debug off), overlay visible (autoHide off), FakeSim at speed 1: working set
       sampled at 30 s and near the end, plus the Agent's own PerfMinute/MemoryMinute lines. One run per -Variants entry
       (runtime knobs given as DOTNET_* environment variables; "baseline" = none).
  Every wait is bounded; every process started from the temporary folder is killed at the end. Results go to the console,
  to artifacts/package-smoke/ and to the job summary.
#>
param(
    [string]$OutDir = 'out',
    [int]$PerfSeconds = 75,
    [string[]]$Variants = @('baseline')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root $OutDir
$report = Join-Path $root 'artifacts/package-smoke'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('fp-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp, $report | Out-Null

$debugZip = Get-ChildItem $out -Filter '*-win-x64-debug.zip' | Select-Object -First 1
$normalZip = Get-ChildItem $out -Filter '*-win-x64.zip' | Select-Object -First 1
if (-not $debugZip -or -not $normalZip) { throw "zips not found in $out" }
$dbg = Join-Path $tmp 'debug'
$nrm = Join-Path $tmp 'normal'
Expand-Archive -LiteralPath $debugZip.FullName -DestinationPath $dbg
Expand-Archive -LiteralPath $normalZip.FullName -DestinationPath $nrm
Write-Host "unpacked $($debugZip.Name) and $($normalZip.Name) into $tmp"

$summary = [Collections.Generic.List[string]]::new()
function Say([string]$line) { Write-Host $line; $summary.Add($line) }

function Start-Exe([string]$exe, [string[]]$argv, [hashtable]$envVars = @{}) {
    if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe" }
    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path $exe
    foreach ($a in $argv) { $psi.ArgumentList.Add($a) }
    foreach ($k in $envVars.Keys) { $psi.Environment[$k] = [string]$envVars[$k] }
    return [Diagnostics.Process]::Start($psi)
}

function Wait-Exit($p, [int]$seconds, [string]$name) {
    if (-not $p.WaitForExit($seconds * 1000)) {
        try { $p.Kill($true) } catch { }
        throw "$name did not exit within $seconds s"
    }
    $p.WaitForExit() # flushes the exit code
    return $p.ExitCode
}

function Read-Log([string]$appData) {
    $dir = Join-Path $appData 'logs'
    if (-not (Test-Path $dir)) { return @() }
    $lines = foreach ($f in Get-ChildItem $dir -Filter '*.jsonl' | Sort-Object Name) {
        $fs = [IO.FileStream]::new($f.FullName, 'Open', 'Read', 'ReadWrite, Delete')
        try { $sr = [IO.StreamReader]::new($fs); $sr.ReadToEnd() -split "`n" } finally { $fs.Dispose() }
    }
    return @($lines | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } })
}

function Events($log, [string]$name) { return @($log | Where-Object { $_.ev -eq $name }) }

function Wait-Log([string]$appData, [string]$event, [int]$seconds, $proc) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) {
        if ((Events (Read-Log $appData) $event).Count -gt 0) { return }
        if ($proc -and $proc.HasExited) { throw "the Agent exited ($($proc.ExitCode)) before logging $event" }
        Start-Sleep -Milliseconds 250
    }
    throw "no $event in the Agent's log after $seconds s"
}

function Assert-NoErrors($log, [string]$what) {
    $errors = @($log | Where-Object { $_.lvl -eq 'error' })
    if ($errors.Count -gt 0) {
        $errors | ForEach-Object { Write-Host ($_ | ConvertTo-Json -Compress) }
        throw "$what logged $($errors.Count) error line(s)"
    }
}

function Stop-Leftovers {
    Get-Process -Name 'FuelPace.*' -ErrorAction SilentlyContinue | ForEach-Object {
        try { if ($_.Path -and $_.Path.StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase)) { $_.Kill($true) } } catch { }
    }
}

function New-Names {
    $id = [guid]::NewGuid().ToString('N')
    return @{ FUELPACE_IRSDK_MAP = "Local\FuelPace.Smoke.Map.$id"; FUELPACE_IRSDK_EVENT = "Local\FuelPace.Smoke.Event.$id" }
}

function Get-PrivateWs([int]$processId) {
    try {
        $c = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $processId" -ErrorAction Stop
        return [math]::Round($c.WorkingSetPrivate / 1MB, 1)
    } catch { return $null }
}

function Sample($p) {
    $p.Refresh()
    return [pscustomobject]@{
        WsMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateWsMB = Get-PrivateWs $p.Id
        PrivateBytesMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        PeakWsMB = [math]::Round($p.PeakWorkingSet64 / 1MB, 1)
    }
}

$failed = $false
try {
    # ---- 1. Agent from the debug zip
    $a1 = Join-Path $tmp 'appdata-start'
    $p = Start-Exe (Join-Path $dbg 'FuelPace.Agent.exe') @('--appdata', $a1, '--exit-after', '15')
    $code = Wait-Exit $p 90 'Agent'
    $log = Read-Log $a1
    if ($code -ne 0) { throw "Agent exited with $code" }
    if ((Events $log 'Startup').Count -ne 1 -or (Events $log 'Shutdown').Count -ne 1) { throw 'Agent log lacks Startup or Shutdown' }
    if (-not ($log | Where-Object { $_.msg -like '*debug on*' })) { throw 'the debug zip did not start in debug mode (FuelPace.debug marker)' }
    Assert-NoErrors $log 'Agent (start/exit)'
    Say "1. Agent (debug zip) --exit-after 15: exit 0, Startup + Shutdown, debug on"

    # ---- 2. Settings
    $p = Start-Exe (Join-Path $dbg 'FuelPace.Settings.exe') @()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $p.Refresh()
        if ($p.HasExited) { throw "Settings exited with $($p.ExitCode) before showing its window" }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
        if ($sw.Elapsed.TotalSeconds -gt 60) { throw 'the Settings window did not appear within 60 s' }
        Start-Sleep -Milliseconds 200
    }
    $title = $p.MainWindowTitle
    if (-not $p.CloseMainWindow()) { throw 'Settings: CloseMainWindow failed' }
    $code = Wait-Exit $p 30 'Settings'
    if ($code -ne 0) { throw "Settings exited with $code" }
    Say "2. Settings: window '$title' shown, closed, exit 0"

    # ---- 3. Agent + FakeSim on private names
    $a2 = Join-Path $tmp 'appdata-sim'
    $names = New-Names
    $agent = Start-Exe (Join-Path $dbg 'FuelPace.Agent.exe') @('--appdata', $a2, '--exit-after', '30') $names
    Wait-Log $a2 'Startup' 30 $agent
    # Speed 10 (2 laps = 4 s): at max the 2 laps are over before the Agent's next open attempt (once a second).
    $sim = Start-Exe (Join-Path $dbg 'FuelPace.FakeSim.exe') @('--synthetic-race', '2', '--speed', '10',
        '--map', $names.FUELPACE_IRSDK_MAP, '--event', $names.FUELPACE_IRSDK_EVENT)
    $code = Wait-Exit $sim 60 'FakeSim'
    if ($code -ne 0) { throw "FakeSim exited with $code" }
    $code = Wait-Exit $agent 90 'Agent (with FakeSim)'
    if ($code -ne 0) { throw "Agent (with FakeSim) exited with $code" }
    $log = Read-Log $a2
    if ((Events $log 'SourceOpened').Count -lt 1) { throw 'the Agent never opened the FakeSim source (no SourceOpened)' }
    Assert-NoErrors $log 'Agent (with FakeSim)'
    Say "3. Agent + FakeSim (private names): SourceOpened $((Events $log 'SourceOpened').Count), LapCompleted $((Events $log 'LapCompleted').Count), no errors"

    # ---- 4. Replay diag
    $diagOut = Join-Path $report 'diag.txt'
    & (Join-Path $dbg 'FuelPace.Replay.exe') diag $a2 | Out-File -FilePath $diagOut -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Replay diag exited with $LASTEXITCODE" }
    Say "4. Replay diag on the Agent folder: exit 0 (output in artifacts/package-smoke/diag.txt)"

    # ---- 5. PERF-05: normal zip, debug off, overlay visible
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($variant in $Variants) {
        $envVars = New-Names
        if ($variant -ne 'baseline') {
            foreach ($kv in $variant -split ';') { $k, $v = $kv -split '=', 2; $envVars[$k] = $v }
        }
        $a3 = Join-Path $tmp ('appdata-perf-' + $rows.Count)
        New-Item -ItemType Directory -Force -Path $a3 | Out-Null
        '{ "overlay": { "autoHide": false } }' | Out-File -FilePath (Join-Path $a3 'settings.json') -Encoding ascii
        $agent = Start-Exe (Join-Path $nrm 'FuelPace.Agent.exe') @('--appdata', $a3, '--exit-after', [string]($PerfSeconds + 5)) $envVars
        Wait-Log $a3 'Startup' 30 $agent
        $sim = Start-Exe (Join-Path $dbg 'FuelPace.FakeSim.exe') @('--synthetic-race', '20', '--speed', '1', '--cars', '20',
            '--disconnect-after', [string]$PerfSeconds, '--map', $envVars.FUELPACE_IRSDK_MAP, '--event', $envVars.FUELPACE_IRSDK_EVENT)
        $start = [Diagnostics.Stopwatch]::StartNew()
        while ($start.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 500 }
        $s30 = Sample $agent
        $cpu30 = $agent.TotalProcessorTime.TotalMilliseconds; $wall30 = $start.Elapsed.TotalMilliseconds
        while ($start.Elapsed.TotalSeconds -lt ($PerfSeconds - 3)) { Start-Sleep -Milliseconds 500 }
        $sEnd = Sample $agent
        $agent.Refresh()
        # PERF-02's unit: percent of ONE core, between the two samples (steady state, telemetry flowing, overlay drawn).
        $cpuPct = [math]::Round(100 * ($agent.TotalProcessorTime.TotalMilliseconds - $cpu30) / ($start.Elapsed.TotalMilliseconds - $wall30), 2)
        $null = Wait-Exit $sim 60 'FakeSim (PERF-05)'
        $code = Wait-Exit $agent 90 'Agent (PERF-05)'
        if ($code -ne 0) { throw "Agent (PERF-05, $variant) exited with $code" }
        $log = Read-Log $a3
        if ($log | Where-Object { $_.msg -like '*debug on*' }) { throw 'the normal zip ran in debug mode' }
        $vis = Events $log 'Visibility' | Select-Object -Last 1
        $perf = Events $log 'PerfMinute' | Select-Object -Last 1
        $mem = Events $log 'MemoryMinute' | Select-Object -Last 1
        $rows.Add([pscustomobject]@{
            Variant = $variant
            CpuPctOneCore = $cpuPct
            Ws30MB = $s30.WsMB; WsEndMB = $sEnd.WsMB; PrivateWsMB = $sEnd.PrivateWsMB; PrivateBytesMB = $sEnd.PrivateBytesMB
            PeakWsMB = $sEnd.PeakWsMB
            PerfMinuteWsMB = if ($perf) { [math]::Round($perf.workingSetMB, 1) } else { '-' }
            TickP99Us = if ($perf) { [math]::Round($perf.tickP99Us, 0) } else { '-' }
            GcHeapMB = if ($mem -and $null -ne $mem.gcHeapMB) { [math]::Round($mem.gcHeapMB, 1) } else { '-' }
            Assemblies = if ($mem) { $mem.assemblies } else { '-' }
            Visible = if ($vis) { $vis.visible } else { '-' }
            Backend = (($log | Where-Object { $_.ev -eq 'OverlayBackend' } | Select-Object -Last 1).backend)
        })
        Stop-Leftovers
    }
    $table = $rows | Format-Table Variant, CpuPctOneCore, WsEndMB, PrivateWsMB, PrivateBytesMB, PeakWsMB, PerfMinuteWsMB, TickP99Us,
        GcHeapMB, Visible -AutoSize | Out-String -Width 300
    $rows | ConvertTo-Json | Out-File -FilePath (Join-Path $report 'perf05.json') -Encoding utf8
    Say "5. PERF-05 (normal zip, debug off, overlay visible, FakeSim speed 1 with 20 cars, $PerfSeconds s):"
    foreach ($l in ($table.Trim() -split "`r?`n")) { Say ("   " + $l) }
}
catch {
    $failed = $true
    Say "FAILED: $($_.Exception.Message)"
}
finally {
    Stop-Leftovers
    # Keep the Agents' folders for a diagnosis.
    Get-ChildItem $tmp -Directory -Filter 'appdata-*' | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $report $_.Name) -Recurse -Force -ErrorAction SilentlyContinue
    }
    $summary | Out-File -FilePath (Join-Path $report 'summary.txt') -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) {
        @('### Package smoke', '```') + $summary + @('```') | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 }
