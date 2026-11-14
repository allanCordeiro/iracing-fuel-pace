# PRD v3: benchmark do tick (v2.1 × v3)

`bench/FuelPace.Bench`, `PipelineTickBench.OneLap` (o mesmo método de `docs/perf/core-bench.md`: 4 800 `TelemetryLoop.Step()`
por invocação sobre a corrida sintética; a estratégia roda a cada 6 ticks). Na v3, o tick de estratégia também roda o
`GlanceViewBuilder` e o publisher compara o `GlanceView` por valor.

Rodado em 2026-11-14, cada versão num git worktree temporário fora do repositório, uma depois da outra, na mesma máquina
(Intel Core i7-10700F, Ubuntu 24.04.5, .NET 10.0.14, X64 RyuJIT x86-64-v3):

    dotnet run -c Release --project bench/FuelPace.Bench -- --filter '*PipelineTickBench*'

| Versão | Commit | Média por volta | Erro | Por tick | Alocado |
|---|---|---:|---:|---:|---:|
| v2.1 | `fbba179` | 13.04 ms | 0.365 ms | 2.72 µs | 0 B |
| v3 | `3ae7920` | 13.61 ms | 0.865 ms | 2.84 µs | 0 B |

Diferença: **+4.4 %** (limite do PRD v3 §11.3: 10 %), dentro do erro somado das duas medidas. Continua cerca de 350 vezes abaixo
do orçamento de 1 ms por tick (PERF-02) e sem alocação (PERF-03).
