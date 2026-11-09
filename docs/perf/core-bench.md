# Core hot-path benchmark

`bench/FuelPace.Bench`, class `PipelineTickBench` (BenchmarkDotNet, `[MemoryDiagnoser]`). One invocation is one lap at 60 Hz
(4 800 `TelemetryLoop.Step()` calls) over the synthetic time race: 20 cars in CarIdx, leader pace, a baseline with a BoP
condition, a pit stop with refuel, a caution and an incident (`tests/FuelPace.Tests/Support/SyntheticRace.cs`). Each step reads
and decodes the record, then runs lap detection, stint tracking, leader pace, the strategy (every 6th tick) and the overlay publisher.
Diagnostics are off and no frame tap is attached. The file is reopened outside the measurement, between iterations.

Run (not part of `dotnet test`, not run in CI; CI only builds it):

    dotnet run -c Release --project bench/FuelPace.Bench -- --filter '*PipelineTickBench*'

Do not pass `--job`: the class defines its own job (3 invocations = 3 laps per iteration) so that reopening the file stays out of
the measured window.

## Result (2026-11-09)

Intel Core i7-10700F 2.90 GHz, Ubuntu 24.04, .NET SDK 10.0.403 (runtime 10.0.14), X64 RyuJIT x86-64-v3.

| Method | Mean     | Error    | StdDev   | Allocated |
|------- |---------:|---------:|---------:|----------:|
| OneLap | 12.67 ms | 0.331 ms | 0.219 ms |         - |

Per tick: 12.67 ms / 4 800 = **2.64 us**, about 380 times under the 1 ms budget (PERF-02). `Allocated` is `-`: 0 B per tick (PERF-03).
Most of the tick is the read and decode of the full variable catalog (6 CarIdx arrays of 64 entries) from the file.

## Zero-allocation tests

`tests/FuelPace.Tests/Perf/HotPathAllocationTests.cs` asserts exactly 0 bytes (`GC.GetAllocatedBytesForCurrentThread`) and 0 gen0
collections over 10 000 steps after 1 000 warm-up steps, on the synthetic race, on the real Zolder fixture, and with Trace
diagnostics drained by another thread plus a frame tap copying into a pre-allocated ring.
Every test that asserts an exact allocation or GC count belongs to the `zero-allocation` xUnit collection
(`Perf/PerfCollection.cs`), which runs alone and after the parallel tests.
