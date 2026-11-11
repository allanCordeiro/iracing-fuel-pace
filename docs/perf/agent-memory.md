# Agent memory (PERF-05)

PERF-05 (PRD): "Memória do Agent (working set) ≤ 60 MB, estável (sem crescimento) em 2 h", measured with the Task Manager or the log.

## How it is measured

`build/smoke-package.ps1`, step 5, on every CI run (job summary and `artifacts/package-smoke/perf05.json`):

- the **normal** zip (no `FuelPace.debug`: debug off, Info logging, no recorder), unpacked, run from its own folder;
- `settings.json` with `"autoHide": false`, so the overlay is on screen the whole time (the log's `Visibility visible=1` is checked);
- FakeSim (from the debug zip, private names) at speed 1, 60 Hz, a synthetic race with 20 cars (all `CarIdx*` variables), 75 s;
- samples of the Agent process at 30 s and at ~72 s: `WorkingSet64` (total working set), the private working set
  (`Win32_PerfFormattedData_PerfProc_Process.WorkingSetPrivate`, the Task Manager's default "Memory" column), `PrivateMemorySize64`
  (private bytes) and the CPU time between the samples (percent of one core);
- the Agent's own `PerfMinute` (`workingSetMB`, `tickP99Us`) and `MemoryMinute` (working set, private bytes, GC heap in use,
  loaded assemblies) lines, which the field logs carry too.

The CI runner has no GPU: Direct3D runs on WARP (software), whose DLL and buffers count as process memory. With a real GPU the
user-mode driver DLL takes WARP's place (a large, mostly shared image) and the swap chain lives in video memory.

## Results

All numbers are from GitHub Actions `windows-latest` (no GPU: WARP), the normal zip, overlay visible (`Visibility visible=1`,
backend `composition`), FakeSim at 60 Hz with 20 cars, end-of-run sample at ~72 s. MB = 2^20 bytes. "WS" is the total working
set (`Environment.WorkingSet`, what `PerfMinute.workingSetMB` logs); "private WS" is the Task Manager's default "Memory" column.

**1. Before any change** (run 42822250120, commit 06a649e). Runtime knobs given as `DOTNET_*` environment variables, one Agent
run each:

| Variant | WS | private WS | private bytes | peak WS | PerfMinute WS | tick p99 |
|---|---:|---:|---:|---:|---:|---:|
| as shipped | 67.4 | 18.1 | 24.5 | 72.4 | 67.1 | 30 µs |
| `TieredPGO=0` | 66.2 | 17.6 | 24.3 | 69.2 | 66.4 | 20 µs |
| `gcConcurrent=0` | 67.6 | 18.3 | 25.3 | 72.2 | 67.5 | 20 µs |
| `GCConserveMemory=5` | 67.5 | 18.2 | 25.1 | 72.4 | 67.3 | 30 µs |
| `GCgen0size=0x400000` | 67.5 | 18.2 | 26.3 | 72.3 | 67.3 | 20 µs |
| `TieredCompilation=0` | 62.4 | 16.4 | 23.0 | 62.4 | 62.4 | 30 µs |
| `TieredPGO=0` + `gcConcurrent=0` | 66.2 | 17.6 | 23.9 | 69.4 | 66.0 | 20 µs |

So the shipped Agent was **7 MB over** PERF-05 on CI. The GC knobs do nothing: the hot path allocates nothing, no GC ran in the
75 s (`MemoryMinute`: 0 gen2, ~3 MB of managed heap in use). Most of the working set is **shareable** pages (67 − 18 = ~49 MB):
the mapped images of the runtime (coreclr, the ReadyToRun framework assemblies, the app's own R2R images) and of Direct3D 11,
DXGI, Direct2D, DirectWrite, DirectComposition and WARP. Those pages are shared with every other process that maps the same
files and are what the OS trims first under memory pressure. The Agent's own memory (private bytes) was ~25 MB.

**2. The embedded fonts read in place** (commit aeecb54, run 42912873768): `DWriteFonts` copied the 2.7 MB of TTFs into a
managed array and then DirectWrite copied them again (a null owner). It now hands DirectWrite a pointer into the mapped
assembly image (a non-null owner, so no copy), and only the glyph pages drawn are touched.

| Variant (same run) | CPU, % of one core | WS | private WS | private bytes | peak WS | PerfMinute WS | tick p99 |
|---|---:|---:|---:|---:|---:|---:|---:|
| fonts in place | 0.89 | 60.1 | 12.7 | 19.3 | 65.3 | 59.8 | 20 µs |
| fonts in place (2nd run) | 0.74 | 59.4 | 11.9 | 18.7 | 65.0 | 59.5 | 20 µs |
| + `TieredCompilation=0` | 0.81 | 55.5 | 11.4 | 17.7 | 55.5 | 55.4 | 20 µs |
| + `TieredCompilation=0` (2nd run) | 0.26 | 55.5 | 11.4 | 17.9 | 55.5 | 55.4 | 20 µs |
| + `TieredPGO=0` | 0.92 | 58.8 | 12.3 | 19.1 | 62.2 | 58.7 | 20 µs |

The fonts saved ~7.5 MB of WS and ~6 MB of private WS. Turning tiered compilation off saves another ~4.5 MB and the startup
peak (the code is ReadyToRun, so it is never re-jitted: no tier-1 copies, call-counting stubs or PGO data) with the same tick
p99 and no more CPU (the CPU samples are noisy: 0.26 to 0.92 % across identical configurations on a shared runner).

## Decision

- Kept: fonts read in place (aeecb54) and `<TieredCompilation>false</TieredCompilation>` in `FuelPace.Agent.csproj`
  (`TieredPGO` removed with it, it has no meaning without tiering). Dropped: the GC knobs (no effect) and `TieredPGO=0` alone
  (~1 MB, within noise).
- Result on CI with both: **WS ≈ 55.5 MB** (≤ 60 MB), private WS ≈ 11.4 MB, private bytes ≈ 18 MB, peak = steady state.
  Shipped configuration (run 42915099650, `TieredCompilation` off in the csproj): WS 55.4 MB, private WS 11.2 MB, private
  bytes 18.1 MB, peak 55.4 MB, `PerfMinute` 55.3 MB, tick p99 20 µs, CPU 0.65 % of one core. The smoke step prints this row on
  every CI run and warns above 60 MB.
- Not measured here: the 2 h stability half of PERF-05 (CI runs 75 s; the field logs carry `PerfMinute` and `MemoryMinute` every
  minute, and `FuelPace.Replay diag` tabulates them) and a real GPU. With a GPU the user-mode driver DLL replaces WARP: its image
  is larger, mostly shared and mostly not touched, so the total WS on the driver's PC can differ by several MB either way. The
  first in-game bundle answers that; if the total is over 60 MB there while the private WS stays ~12 MB, the excess is shared
  driver pages, not the Agent's own memory.
- Observed, not part of PERF-05: CPU on CI is 0.3 to 0.9 % of one core, above PERF-02's 0.5 % average. CI draws with WARP
  (software rasterizer) at up to 10 redraws/s; on a GPU that cost moves off the CPU. PERF-02 is to be read from the field logs.
