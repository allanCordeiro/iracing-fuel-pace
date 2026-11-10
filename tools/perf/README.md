# PERF-01 (impact on the game)

Target (PRD §11): average fps with the app >= 99.5% of without; 1% low >= 99%; no increase in the number of frames longer than
2x the mean frame time. Method: 3 runs of 5 min with and 3 without the app (same car, track, weather; replay or test drive).

1. Get a PresentMon console exe yourself (https://github.com/GameTechDev/PresentMon/releases). The script downloads nothing.
2. In an elevated PowerShell, with iRacing running and on track:
   `.\presentmon.ps1 -PresentMon C:\Tools\PresentMon.exe -Label sem` (app closed), then again with `-Label com` (Agent running).
   CSVs go to `%APPDATA%\FuelPace\perf\<label>-<date>.csv`; the diagnostic bundle includes that folder.
3. Compare a pair: `FuelPace.Replay presentmon <csv-com> <csv-sem>`. Exit code 0 = APROVADO, 1 = REPROVADO.

The script passes `-process_name`, `-timed`, `-output_file`, `-terminate_after_timed` (single dash). If a PresentMon build rejects them,
run it by hand with the equivalent options; the Replay command only needs the CSV.

## How `presentmon` computes

Frame time column: `FrameTime` (PresentMon 2.x) or else `MsBetweenPresents` (1.x), in ms. Per file, over N frames:
- average fps = 1000 / mean frame time;
- 1% low = 1000 / mean of the slowest ceil(1% of N) frame times (at least one frame);
- spikes = number of frames longer than 2x the mean frame time.

Verdict: avg(com)/avg(sem) >= 99.5%, low(com)/low(sem) >= 99%, spikes(com) <= spikes(sem). Record the result in `docs/perf/`.
