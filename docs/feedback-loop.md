# Feedback loop (driver tests on Windows, fixes on Linux)

The driver sends a diagnostic bundle (`FuelPace-diag-yyyyMMdd-HHmmss.zip`, made from the tray: *Gerar pacote de diagnóstico*) plus
the numbers of the debug marks (`Ctrl+Shift+M`) and what he saw. The bundle holds `logs/`, `settings.json`, `profiles/`,
`system-info.txt`, `perf/` (PresentMon CSVs) and, unless he unchecked *Incluir gravações*, `recordings/*.ibt`.

**Privacy:** recordings carry the session YAML with the driver's own name and iRacing ID and the other drivers' names and IDs. Never post a bundle or a recording in a public issue or
PR; it travels privately (the driver copies it to `feedback/`, which is gitignored). A trimmed recording only goes into git
after checking it (see 4).

1. **Unpack** to `feedback/<date>/` (e.g. `unzip FuelPace-diag-*.zip -d feedback/2026-11-12`).
2. **Triage:** `FuelPace.Replay diag feedback/<date>` (also accepts the zip). It prints system info, errors and warnings, the
   timeline (state changes, captures, laps), a window of ±30 s around each `Marcador N` (with debug/trace lines), the PerfMinute
   table (with the MemoryMinute columns, PERF-05), the LiveStats table (how iRacing's data-valid event behaves: timed-out waits,
   wakeups without a new tick, back-offs; ruling A21) and a check of each recording.
3. **Reproduce:** for each mark and each error, find it on the timeline (the log lines carry `st`, the session time). Trim the
   recording around it: `FuelPace.Replay trim feedback/<date>/recordings/<file>.ibt --from <st-60> --to <st+30> --out cut.ibt`, then
   replay: `FuelPace.Replay cut.ibt --capture` (add `--speed 1` to watch it live-ish).
4. **Regression test:** turn the case into a test under `tests/`. The cut goes to `tests/fixtures/regression/` if < 5 MB and its YAML
   was checked/anonymized (the driver's and the other drivers' names and IDs); `.gitignore` lets `tests/fixtures/regression/*.ibt`
   in (every other `.ibt` stays ignored). Otherwise keep it out of git and gate the test with `SkipUnless`. The test asserts
   the expected behavior. Write it first and watch it fail.
5. **Fix,** push, let CI publish a new build (workflow artifact `fuelpace-windows`, zips `FuelPace-<ver>-win-x64[-debug].zip`), and ask
   the driver to repeat the scenario.
6. **Log the case** in `docs/feedback/<date>.md`: what happened, the cause, the test, the build with the fix.

PERF-01: the driver's PresentMon CSVs arrive in the bundle's `perf/`; `FuelPace.Replay presentmon <com.csv> <sem.csv>` gives the verdict.
