# Core hot-path benchmark

`bench/FuelPace.Bench`, class `PipelineTickBench` (BenchmarkDotNet, `[MemoryDiagnoser]`). One invocation is 4 800 `TelemetryLoop.Step()` calls
(an 80 s lap at 60 Hz; the synthetic race's own laps are 20 s, 1 200 ticks) over the synthetic time race: 20 cars in CarIdx, leader pace, a baseline with a BoP
condition, a pit stop with refuel, a caution and an incident (`tests/FuelPace.Tests/Support/SyntheticRace.cs`). Each step reads
and decodes the record, then runs lap detection, stint tracking, leader pace, the strategy (every 6th tick) and the overlay publisher.
Diagnostics are off and no frame tap is attached. The file is reopened outside the measurement, between iterations.

Run (not part of `dotnet test`, not run in CI; CI only builds it):

    dotnet run -c Release --project bench/FuelPace.Bench -- --filter '*PipelineTickBench*'

Do not pass `--job`: the class defines its own job (3 invocations = 14 400 ticks per iteration) so that reopening the file stays out of
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

## Smoke com .ibt reais

`FuelPace.Replay <arquivo> --capture --profiles <dir>` (2026-11-09), arquivos de `telemeter/telemetry_data` (somente leitura):

| Arquivo | Resultado |
|---|---|
| Dallara P217 LMP2, Road Atlanta (2026-05-16 20-40-20) | sem exceção; 7 voltas válidas, 2.137-2.318 L (média 2.249), tempo médio 75.9 s; volta 11 (entrada no pit, 1.994 L) e 12 marcadas `pit` |
| BMW M8 GTE, Interlagos (2026-05-05 19-46-54) | sem exceção; só 3 voltas válidas (2.098-2.136 L, confiança Baixa): a maioria das voltas tem `incident`/`offtrack`; volta 12 `pit,refuel,incomplete` |
| Ligier JS P320, Zolder (fixture) | voltas 16-22 batem com a tabela §12 (ex.: lap 16 80.897 s 2.382 L); 6 voltas válidas, confiança Média |

Observações: (1) o nome da pista do Interlagos vem do arquivo como `Aut dromo Jos  Carlos Pace` (o acento já falta no YAML gravado pelo iRacing; não é erro do leitor). (2) Em voltas de pit o overlay fica INATIVO (`L_rest` indisponível), sem insight. (3) A volta 0 (início do arquivo) sai com `reset,incomplete`, como esperado.
