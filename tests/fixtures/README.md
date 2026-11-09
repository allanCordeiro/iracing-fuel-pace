# Test fixtures

Place `ligierjsp320_zolder_alt_2026-10-29_20-03-40.ibt` (iRacing telemetry, ~51 MB) in this directory.
The file is git-ignored (`*.ibt`) and must never be committed.

Alternatively, set the environment variable `FUELPACE_FIXTURE_IBT` to the absolute path of the file.
Tests that depend on it are skipped when it is missing.
