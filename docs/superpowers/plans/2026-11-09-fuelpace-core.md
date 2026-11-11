# FuelPace Core (Fases A–D3) — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar o `FuelPace.Core` (.NET 10, sem dependência de Windows): leitura de `.ibt`, detecção/validação de voltas, captura, baseline, persistência, ritmo do líder, motor de estratégia e pipeline de replay com zero alocação por tick, além de testes, benchmark e uma CLI de replay.

**Architecture:** Biblioteca `FuelPace.Core` com lógica determinística: `TelemetryFrame` (struct com inline arrays) → `TelemetryPipeline` (60 Hz: voltas, stint, líder; 10 Hz: `FuelStrategyEngine`) → `OverlayState` (struct) publicado com contador de versão visível. O replay `.ibt` usa o mesmo caminho que a fonte ao vivo usará na Fase E. I/O de disco e parse de YAML ficam fora do hot path.

**Tech Stack:** C# / .NET 10 (`net10.0`), xUnit v3, YamlDotNet, System.Text.Json (source generator), BenchmarkDotNet.

**Spec:** `docs/PRD.md` (v2.1). Este plano cobre as fases A–D3 da §14. As fases E–H (Agent Win32/DirectComposition, Settings WPF, IPC) ganham um plano próprio, a ser executado no Windows.

## Global Constraints

- **Alvo de execução: Windows 10/11 x64. Ambiente de desenvolvimento: Linux.** Todo teste precisa passar nos dois (CI com matriz `ubuntu-latest` + `windows-latest`). Nada pode depender de comportamento que só vale no Linux: rename sobre arquivo aberto, caracteres permitidos em nome de arquivo, diferença de maiúsculas/minúsculas em caminhos, `\n` puro, granularidade fina do `Thread.Sleep`.
- Target `net10.0` em todos os projetos deste plano; o `FuelPace.Core` não referencia nada específico de Windows.
- **Diagnóstico desde o primeiro task:** todo componente com estado recebe `DiagLog? log = null` como último parâmetro do construtor (`null` → `DiagLog.Null`) e emite os eventos do catálogo do Task 1b nos pontos indicados ali. `DiagLog.Emit` é zero alocação mesmo em nível `Trace`. Funções estáticas puras (`FuelStrategyEngine`, `LapsRemainingCalculator`, `FuelProjection`) não registram nada: quem as chama compara as saídas e emite os eventos.
- Hot path (tudo o que roda por tick: leitura do frame, `LapDetector`, `StintTracker`, `LeaderPaceTracker`, `FuelStrategyEngine`, publicação do `OverlayState`): **0 bytes alocados**. Sem LINQ, `string`, boxing, closures, `async`, `params` nem iteradores.
- Detecção de volta a 60 Hz (todo frame); estratégia a 10 Hz (a cada 6º frame).
- Formatação numérica sempre em `CultureInfo.InvariantCulture` (ponto decimal); L com 1 casa, L/v com 2 casas. `InvariantGlobalization=true`.
- Comparação do sinalizador nos valores arredondados a 0,01 L (`Math.Round(x, 2, MidpointRounding.AwayFromZero)`); igual = AZUL.
- Padrões: reserva 0,5 L; N da média móvel 5 (aceito 2–16); `economia_max` 6%; voltas extras 0; histerese 1,5 s; offtrack > 0,5 s acumulado; refuel > 0,05 L; timeout de `LapLastLapTime` 5 s; outlier > 107% da mediana; `VOLTA` oculta enquanto `LapDistPct < 0,15`.
- Persistência: `%APPDATA%\FuelPace\profiles\{carPath}__{trackName}.json` (espaços → `_`), escrita atômica (temp + rename), sempre fora da thread de telemetria.
- Nenhuma chamada de rede.
- A fixture `.ibt` nunca entra no git; testes que dependem dela usam `Assert.SkipUnless`.
- Identificadores de código em inglês; textos para o piloto em português, exatamente como no PRD.
- Toda mensagem de commit termina com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Ambiente: antes de qualquer comando `dotnet`, rodar `export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"`.

## Decisões tomadas onde o PRD é ambíguo

1. **Volta cujo início ou fim ficou fora da janela observada** (início do `.ibt`, captura ligada ou desligada no meio da volta) recebe `incomplete` além dos motivos observados. Na fixture, a volta 15 começa no meio do arquivo e sai com `{pit, incident, incomplete}`, enquanto a tabela §12 lista só `pit, incident`. O teste do critério 1 confere `⊇ {pit, incident}` nessa volta e igualdade exata nas demais.
2. **`fuelProfile` é guardado por volta** (101 pontos, fração acumulada), não só por captura. Isso permite recalcular o perfil médio quando o piloto exclui ou reinclui uma volta (CAP-06). A baseline guarda o perfil médio.
3. **Insight sem baseline:** segue o mock aprovado nº 4: `Sem baseline para {carro} · {pista}`. `{carro}` = `CarScreenNameShort` (fallback: `CarScreenName`); `{pista}` = `TrackDisplayShortName` + (` ` + `TrackConfigName`, se houver) (fallback: `TrackDisplayName`).
4. **Modo stint:** `L_rest = StintLaps − voltas completadas desde o início do stint − LapDistPct`. O stint começa na saída do pit (`OnPitRoad` true→false) ou num reabastecimento.
5. **Consumo de referência sem `VOLTA` nem `ÚLT`:** usa `MÉD`. Folga e margem usam `consumo_ref` arredondado: `(FuelLevel − reserva) − L_rest × round2(consumo_ref)`, com mínimo 0.
6. **Percentual do insight de economia:** `round(100 × (round2(ref) − round2(alvo)) / round2(ref))`; custo em segundos: `−b × economia_L` da regressão.
7. **Carro elétrico** (`FuelLevel` com unidade `kWh`): estado INATIVO com insight `Carro elétrico: sem suporte`.
8. **Sessão sem limite (treino sem tempo, ou com `PracticeLapsFromTime = false`) não é INATIVO.** Segue o mock nº 4: novo estado `NoTarget` (linha neutra, `ALVO —`, texto à direita `treino · sem limite` em treino, `sem limite` nos outros casos, sem selo). Os demais valores (COMB, VOLTA, ÚLT, MÉD, autonomia) continuam aparecendo. Sem baseline, `NoBaseline` tem prioridade, com o mesmo texto à direita.
9. **Destaque em negrito no insight** (mocks 1–3): o trecho até o primeiro ` · ` ou ` (` fica em negrito (`Folga 1.8 L`, `Economize 0.07 L/v`, `Faltam 3.3 L`, `No alvo`). Os insights sem baseline, de BoP e de carro elétrico não têm negrito.
10. **Sessão por tempo × por voltas:** vem do YAML da sessão atual (`SessionNum`). `SessionLaps` numérico com `SessionLapsRemainEx < 32767` → por voltas; `SessionTime` numérico → por tempo; senão, ilimitada. O ritmo do líder só é usado quando `SessionType == "Race"`; em treino e quali, usa-se o próprio ritmo.

## Review Focus

1. **Troca de carro, pista ou sessão com a captura ligada:** as voltas não podem cair no perfil errado. Esperado: a captura para sozinha e é salva no perfil antigo. Teste no Task 14.
2. **JSON de perfil corrompido ou truncado** (crash numa versão antiga, edição manual): o app não pode quebrar nem sobrescrever o arquivo em silêncio. Esperado: o arquivo é renomeado para `.corrupt` e o perfil é tratado como vazio. Teste no Task 8.
3. **Corrida por tempo depois que o relógio zera** (`T_rem ≤ 0`, líder na última volta, bandeirada já dada): `L_rest` precisa ser a fração certa da volta, nunca 0, negativo ou NaN. Teste no Task 11.
4. **Descontinuidade de telemetria** (`SessionTime` com salto ou voltando, comum em reconexão ou ao sair do carro): a volta é marcada `reset`, sem consumo ou tempo absurdos e sem exceção. Teste no Task 5.
5. **Combustível abaixo da reserva, `L_rest` ≤ 0 ou referências NaN:** nada de alvo infinito ou negativo exibido como AZUL. Esperado: abaixo da reserva → NÃO FACTÍVEL com litros faltantes positivos; `L_rest ≤ 0` → INATIVO. Teste no Task 12.

---

## Estrutura de arquivos

```
global.json                         SDK 10.0.x (rollForward latestFeature)
Directory.Build.props               Nullable, ImplicitUsings, LangVersion latest, TreatWarningsAsErrors, InvariantGlobalization
FuelPace.sln
.gitignore                          bin/ obj/ *.ibt BenchmarkDotNet.Artifacts/
.github/workflows/ci.yml            ubuntu-latest, dotnet test
src/FuelPace.Core/
  Telemetry/VarType.cs, VarSpec.cs, VarHeader.cs, IrsdkHeader.cs   ← cabeçalhos (reusados pelo leitor ao vivo)
  Telemetry/IbtReader.cs                                             ← arquivo .ibt
  Telemetry/TelemetryVars.cs                                         ← catálogo das variáveis usadas
  Telemetry/TelemetryFrame.cs, CarArray.cs                           ← struct do frame
  Telemetry/VarLayout.cs, TelemetryFrameReader.cs                    ← offsets por nome + cópia sem alocação
  Telemetry/ITelemetrySource.cs, IbtTelemetrySource.cs, ReplaySpeed.cs
  Telemetry/SessionInfo.cs, SessionInfoParser.cs
  Laps/LapReasons.cs, CompletedLap.cs, FuelProfile101.cs, LapStats.cs, ILapSink.cs
  Laps/LapDetector.cs, LapValidator.cs
  Capture/CaptureConditions.cs, CapturedLap.cs, CaptureRecord.cs, CaptureSession.cs, OutlierRule.cs
  Baselines/LapSample.cs, Baseline.cs, BaselineCalculator.cs
  Storage/ProfileDocument.cs, ProfileJsonContext.cs, ProfileStore.cs, ProfileOperations.cs, BackgroundWorkQueue.cs
  Strategy/FuelProjection.cs, LapsRemainingCalculator.cs
  Strategy/StrategyConfig.cs, StrategyInput.cs, OverlayState.cs, Insight.cs, SignalHysteresis.cs
  Strategy/FuelStrategyEngine.cs, OverlayText.cs
  Race/LeaderPaceTracker.cs, LeaderPace.cs
  Pipeline/StintTracker.cs, OverlayStatePublisher.cs, TelemetryPipeline.cs, TelemetryLoop.cs
tests/FuelPace.Tests/
  Support/Fixtures.cs, IbtFileBuilder.cs, SampleYaml.cs, LapSim.cs, ReplayHarness.cs, FixedTimeProvider.cs
  <Área>/<Classe>Tests.cs
tests/fixtures/README.md            onde colocar a fixture (o arquivo em si é ignorado)
bench/FuelPace.Bench/               BenchmarkDotNet
tools/FuelPace.Replay/              CLI de replay (console)
```

---

### Task 1: Scaffolding + cabeçalhos iRacing + `IbtReader`

**Files:**
- Create: `global.json`, `Directory.Build.props`, `FuelPace.sln`, `.gitignore`, `.gitattributes`, `.github/workflows/ci.yml`, `tests/fixtures/README.md`
- Create: `src/FuelPace.Core/FuelPace.Core.csproj`, `src/FuelPace.Core/Telemetry/{VarType,VarSpec,VarHeader,IrsdkHeader,IbtReader}.cs`
- Create: `tests/FuelPace.Tests/FuelPace.Tests.csproj`, `tests/FuelPace.Tests/Support/{Fixtures,IbtFileBuilder}.cs`
- Test: `tests/FuelPace.Tests/Telemetry/IbtReaderTests.cs`

**Interfaces:**
- Produces:
  - `enum VarType { Char = 0, Bool = 1, Int = 2, BitField = 3, Float = 4, Double = 5 }` e `static int VarTypeSize(VarType)` (1,1,4,4,4,8)
  - `readonly record struct VarSpec(string Name, VarType Type, int Count = 1, string Unit = "")`
  - `sealed record VarHeader(VarType Type, int Offset, int Count, bool CountAsTime, string Name, string Desc, string Unit)` + `static VarHeader[] ParseAll(ReadOnlySpan<byte> src, int count)` (144 bytes cada)
  - `readonly record struct VarBufInfo(int TickCount, int BufOffset)`
  - `readonly record struct IrsdkHeader(int Ver, int Status, int TickRate, int SessionInfoUpdate, int SessionInfoLen, int SessionInfoOffset, int NumVars, int VarHeaderOffset, int NumBuf, int BufLen, VarBufInfo Buf0, VarBufInfo Buf1, VarBufInfo Buf2, VarBufInfo Buf3)` + `static IrsdkHeader Parse(ReadOnlySpan<byte> first112Bytes)`
  - `readonly record struct IbtDiskHeader(long SessionStartDate, double SessionStartTime, double SessionEndTime, int SessionLapCount, int RecordCount)`
  - `sealed class IbtReader : IDisposable` com `static IbtReader Open(string path)`, `IrsdkHeader Header`, `IbtDiskHeader DiskHeader`, `IReadOnlyList<VarHeader> Variables`, `string SessionInfoYaml` (latin-1, cortado no primeiro `\0`), `int RecordCount`, `void ReadRecord(int index, Span<byte> destination)`
  - Teste: `Fixtures.RequireZolder() → string` (pula se ausente), `Fixtures.RepoRoot`, `Fixtures.TempPath(string ext) → string`
  - Teste: `IbtFileBuilder.Write(string path, string yaml, IReadOnlyList<VarSpec> vars, IEnumerable<double[]> rows, int tickRate = 60)` (cada `double[]` tem um valor por elemento, na ordem das vars; offsets sequenciais)

- [ ] **Step 1: Instalar o SDK .NET 10 (sem sudo)**

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o "$TMPDIR/dotnet-install.sh"
bash "$TMPDIR/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
dotnet --version
```
Expected: `10.0.x`. (`$TMPDIR` = diretório de scratch da sessão.)

- [ ] **Step 2: Criar solução e projetos**

```bash
dotnet new sln -n FuelPace
dotnet new classlib -n FuelPace.Core -o src/FuelPace.Core -f net10.0
dotnet new classlib -n FuelPace.Tests -o tests/FuelPace.Tests -f net10.0
dotnet sln add src/FuelPace.Core tests/FuelPace.Tests
dotnet add tests/FuelPace.Tests reference src/FuelPace.Core
dotnet add tests/FuelPace.Tests package xunit.v3
dotnet add tests/FuelPace.Tests package xunit.runner.visualstudio
dotnet add tests/FuelPace.Tests package Microsoft.NET.Test.Sdk
```
Apagar os `Class1.cs`. No `.csproj` de testes: `<IsPackable>false</IsPackable>`, `<OutputType>Exe</OutputType>` (exigido pelo xUnit v3). `global.json`: `{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`. `Directory.Build.props` com os itens listados na estrutura. `.gitignore`: `bin/`, `obj/`, `*.ibt`, `BenchmarkDotNet.Artifacts/`, `.vs/`, `*.user`. `.gitattributes`: `* text=auto eol=lf`, `*.ibt binary`, `*.sln text eol=crlf`. O `ci.yml` usa `strategy.matrix.os: [ubuntu-latest, windows-latest]` com `actions/checkout@v4` + `actions/setup-dotnet@v4` (`dotnet-version: 10.0.x`) e roda `dotnet test -c Release` nos dois sistemas. O job do Windows também roda `dotnet publish tools/FuelPace.Replay -c Release -r win-x64 --self-contained false` assim que esse projeto existir (Task 16). O `tests/fixtures/README.md` diz onde colocar `ligierjsp320_zolder_alt_2026-10-29_20-03-40.ibt` e cita a variável `FUELPACE_FIXTURE_IBT`.

- [ ] **Step 3: Escrever os testes que falham**

```csharp
public class IbtReaderTests
{
    [Fact]
    public void Reads_header_vars_yaml_and_records_from_synthetic_file()
    {
        var path = Fixtures.TempPath(".ibt");
        var vars = new VarSpec[] { new("SessionTime", VarType.Double, 1, "s"), new("Lap", VarType.Int), new("LapDistPct", VarType.Float, 1, "%") };
        var yaml = "WeekendInfo:\n TrackName: test track\n";
        IbtFileBuilder.Write(path, yaml, vars, [[10.0, 3, 0.25], [10.5, 3, 0.5], [11.0, 4, 0.01]]);

        using var r = IbtReader.Open(path);
        Assert.Equal(60, r.Header.TickRate);
        Assert.Equal(3, r.RecordCount);
        Assert.Equal(["SessionTime", "Lap", "LapDistPct"], r.Variables.Select(v => v.Name));
        Assert.Equal("s", r.Variables[0].Unit);
        Assert.Equal(yaml, r.SessionInfoYaml);

        var row = new byte[r.Header.BufLen];
        r.ReadRecord(2, row);
        Assert.Equal(11.0, BinaryPrimitives.ReadDoubleLittleEndian(row.AsSpan(r.Variables[0].Offset)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(r.Variables[1].Offset)));
    }

    [Fact]
    public void ReadRecord_out_of_range_throws()
    { /* índice == RecordCount → ArgumentOutOfRangeException */ }

    [Fact]
    public void Zero_record_count_is_derived_from_file_length()
    { // gravação interrompida por crash: zera RecordCount no sub-cabeçalho e acrescenta meia linha no fim
      // → RecordCount == (length − Buf0.BufOffset) / BufLen (linha parcial ignorada)
    }

    [Fact]
    public void Fixture_has_expected_shape()
    {
        using var r = IbtReader.Open(Fixtures.RequireZolder());
        Assert.Equal(60, r.Header.TickRate);
        Assert.Equal(47106, r.RecordCount);
        Assert.Contains(r.Variables, v => v.Name == "FuelLevel" && v.Type == VarType.Float);
        Assert.Contains(r.Variables, v => v.Name == "SessionTime" && v.Type == VarType.Double);
        Assert.DoesNotContain(r.Variables, v => v.Name.StartsWith("CarIdx"));
        Assert.Contains("CarPath: ligierjsp320", r.SessionInfoYaml);
    }
}
```

`Fixtures.RequireZolder()`: usa `FUELPACE_FIXTURE_IBT` se estiver definida; senão `RepoRoot/tests/fixtures/ligierjsp320_zolder_alt_2026-10-29_20-03-40.ibt`. Chama `Assert.SkipUnless(File.Exists(p), "fixture Zolder ausente")`. `RepoRoot` sobe a partir de `AppContext.BaseDirectory` até achar `FuelPace.sln`.

- [ ] **Step 4: Rodar e ver falhar**

Run: `dotnet test tests/FuelPace.Tests --filter "FullyQualifiedName~IbtReaderTests"`
Expected: erro de compilação (`IbtReader` não existe).

- [ ] **Step 5: Implementar os tipos de `Telemetry/` e o `IbtFileBuilder`**

`IbtReader` abre um `SafeFileHandle` (`File.OpenHandle`), lê o cabeçalho de 112 bytes, o sub-cabeçalho de disco no offset 112, os var headers e o YAML de uma vez. `ReadRecord` usa `RandomAccess.Read(handle, dest[..BufLen], Buf0.BufOffset + (long)index * BufLen)`, sem alocar. `IbtFileBuilder` escreve: cabeçalho (com `Ver=2`, `NumBuf=1`), sub-cabeçalho, var headers a partir do offset 144, YAML logo depois (com `\0`) e as linhas a partir de `Buf0.BufOffset`, com `RecordCount` preenchido.

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test tests/FuelPace.Tests --filter "FullyQualifiedName~IbtReaderTests"`
Expected: 2 passam, `Fixture_has_expected_shape` é ignorado (sem fixture) ou passa (com fixture).

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(core): scaffold solution and add .ibt reader"
```
(Inclui `docs/PRD.md` e este plano.)

---

### Task 1b: `DiagLog`: eventos de diagnóstico sem alocação

**Files:**
- Create: `src/FuelPace.Core/Diagnostics/{DiagLevel,DiagEvent,DiagRecord,DiagLog,DiagEventCatalog,DiagJsonFormatter}.cs`
- Test: `tests/FuelPace.Tests/Diagnostics/DiagLogTests.cs`

**Interfaces:**
- Produces:
  - `enum DiagLevel : byte { Off = 0, Error = 1, Warn = 2, Info = 3, Debug = 4, Trace = 5 }`
  - `enum DiagEvent : ushort` (valores 1–999 reservados ao Core; o App registra os seus a partir de 1000)
  - `struct DiagRecord { long Seq; long WallTicksUtc; double SessionTime; DiagEvent Event; DiagLevel Level; int I0, I1; double A, B, C, D; }` (sem strings)
  - `readonly record struct DiagText(long Seq, long WallTicksUtc, DiagLevel Level, string Category, string Message)`
  - `sealed class DiagLog` com `static readonly DiagLog Null`, `(DiagLevel level, int capacity = 16384, TimeProvider? clock = null)`, `DiagLevel Level { get; set; }` (volátil, ajustável em tempo real), `bool IsEnabled(DiagLevel l)`, `void Emit(DiagLevel l, DiagEvent e, double sessionTime, int i0 = 0, int i1 = 0, double a = double.NaN, double b = double.NaN, double c = double.NaN, double d = double.NaN)`, `void Text(DiagLevel l, string category, string message)` (caminho frio, pode alocar), `void Exception(string category, Exception ex)`, `int DrainRecords(Span<DiagRecord> dest)`, `bool TryDequeueText(out DiagText t)`, `long Dropped`
  - `sealed record DiagEventInfo(string Name, string Category, string? I0, string? I1, string? A, string? B, string? C, string? D, bool I1IsLapReasons = false)` (nomes dos campos no JSON; `null` = não sai)
  - `static class DiagEventCatalog { static DiagEventInfo Get(DiagEvent e); static void Register(DiagEvent e, DiagEventInfo info); }`
  - `static class DiagJsonFormatter { static void Write(Utf8JsonWriter w, in DiagRecord r); static void Write(Utf8JsonWriter w, in DiagText t); }`. Uma linha JSON por evento: `{"t":"<ISO-8601 UTC ms>","seq":n,"st":<SessionTime|null>,"lvl":"info","cat":"laps","ev":"LapCompleted",<campos>}`; NaN → `null`; com `I1IsLapReasons`, sai como array de códigos

**Catálogo do Core** (nome · nível · campos · quem emite):

| Evento | Nível | Campos (I0, I1, A, B, C, D) | Emissor |
|---|---|---|---|
| `SourceOpened` | Info | numVars, bufLen, tickRate | `TelemetryLoop` |
| `SourceEnded` | Info | reason (0 EOF, 1 disconnected) | `TelemetryLoop` |
| `LayoutResolved` | Info | missingVars, hasCarIdx, isElectric | `TelemetryLoop` |
| `SessionInfoChanged` | Info | version | `TelemetryLoop` (+ `Text` com carro/pista/sessões) |
| `TornRead` | Debug | count | leitor ao vivo |
| `FrameGap` | Debug | —, —, dtS, sessionTime | `LapDetector` (gap > 0,1 s ou volta no tempo) |
| `LapBoundary` | Debug | lap, kind (0 wrap, 1 lapInc), prevPct, pct, interpFrac | `LapDetector` |
| `LapTimeResolved` | Debug | lap, source (0 oficial, 1 medido por timeout, 2 sem tempo), timeS, waitS | `LapDetector` |
| `LapReasonTriggered` | Debug | lap, reasonBit, value (s offtrack / Δincid / flags / ΔL refuel / Δm salto), pct | `LapDetector` (1ª vez por motivo e volta) |
| `LapCompleted` | Info | lap, reasons*, timeS, fuelL, fullThrottlePct, measuredTimeS | `LapDetector` |
| `StintStarted` | Info | cause (0 saída do pit, 1 refuel), —, fuelL | `StintTracker` |
| `LeaderChanged` | Info | oldIdx, newIdx, paceS | `LeaderPaceTracker` |
| `LeaderLapAccepted` | Trace | carIdx, —, timeS | `LeaderPaceTracker` |
| `LeaderLapRejected` | Debug | carIdx, cause (0 pit, 1 fora do mundo, 2 caution, 3 ≤0, 4 outlier, 5 timeout), timeS | `LeaderPaceTracker` |
| `LeaderPaceSource` | Debug | source, —, paceS | `LeaderPaceTracker` (quando muda) |
| `StateChanged` | Info | raw, displayed, targetL, refL, lapsRemaining, feasibleL | `TelemetryPipeline` |
| `InsightChanged` | Debug | kind, —, A, B, C | `TelemetryPipeline` |
| `OverlayVersion` | Debug | state, insightKind, fuelL, targetL, lapsRemaining, refL | `TelemetryPipeline` (a cada versão visível nova) |
| `StrategyTrace` | Trace | raceKind, flags (bit0 inactive, 1 caution, 2 iAmLeader, 3 bop), projectionL, lastL, meanL, myPaceS | `TelemetryPipeline` (1 Hz) |
| `CaptureStarted` | Info | lapAtStart | `TelemetryPipeline` |
| `CaptureLap` | Info | lap, reasons*, fuelL, timeS | `CaptureSession` |
| `CaptureStopped` | Info | valid, total, auto (0/1) | `TelemetryPipeline` |
| `ProfileSaved` / `ProfileSaveFailed` / `ProfileQuarantined` | Info / Error / Warn | captures, validLaps (+ `Text` com caminho e erro) | `ProfileStore` |
| `PerfMinute` | Info | gen0Delta, publishes, tickP99Us, tickMaxUs, workingSetMB, droppedDiag | `TelemetryLoop` |
| `UserMark` | Info | markNumber | App (hotkey de marcador) |

(`*` = `I1IsLapReasons` ou campo de motivos.)

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Emit_below_level_is_ignored() { /* Level Info; Emit(Debug, ...) → DrainRecords == 0 */ }
[Fact] public void Emit_and_drain_preserve_order_and_fields() { }
[Fact] public void Full_ring_drops_and_counts() { /* capacity 4, 6 Emit → 4 drenados, Dropped 2 */ }
[Fact] public void Emit_allocates_nothing_at_trace() { /* 100.000 Emit em Trace, drenando a cada 1000 num Span pré-alocado → 0 bytes */ }
[Fact] public void Concurrent_emitters_do_not_lose_or_corrupt() { /* 4 threads × 10.000, capacity 65536 → 40.000 registros, Seq únicos */ }
[Fact] public void Json_line_has_named_fields_and_reason_codes()
{ // LapCompleted(lap 19, reasons Incident|OffTrack, 93.175, 2.388, 55.3, 93.18) →
  // {"t":"…","seq":…,"st":4300.5,"lvl":"info","cat":"laps","ev":"LapCompleted","lap":19,"reasons":["incident","offtrack"],"timeS":93.175,"fuelL":2.388,"fullThrottlePct":55.3,"measuredTimeS":93.18}
}
[Fact] public void Nan_fields_are_null() { }
[Fact] public void Text_and_exception_are_queued() { /* Exception inclui tipo, mensagem e stack trace */ }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar** (ring de `DiagRecord` protegido por `System.Threading.Lock`, sem alocar; textos numa `ConcurrentQueue<DiagText>`). **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): zero-allocation diagnostic event log"`

---

### Task 2: `TelemetryFrame`, `VarLayout` e `IbtTelemetrySource`

**Files:**
- Create: `src/FuelPace.Core/Telemetry/{TelemetryVars,CarArray,TelemetryFrame,VarLayout,TelemetryFrameReader,ITelemetrySource,ReplaySpeed,IbtTelemetrySource}.cs`
- Modify: `tests/FuelPace.Tests/Support/IbtFileBuilder.cs` (adicionar `WriteFrames`)
- Test: `tests/FuelPace.Tests/Telemetry/IbtTelemetrySourceTests.cs`

**Interfaces:**
- Consumes: `IbtReader`, `VarHeader`, `VarSpec`, `VarType` (Task 1)
- Produces:
  - `[InlineArray(64)] struct CarArray<T> where T : unmanaged`
  - `struct TelemetryFrame` com campos públicos: `double SessionTime, SessionTimeRemain`; `int SessionNum, SessionState, SessionLapsRemainEx, Lap, LapCompleted, PlayerTrackSurface, PlayerCarMyIncidentCount, PlayerCarIdx, TrackWetness`; `uint SessionFlags`; `float LapDistPct, LapLastLapTime, FuelLevel, FuelLevelPct, FuelUsePerHour, PlayerCarTowTime, Speed, Throttle, TrackTempCrew, AirTemp, PlayerCarPowerAdjust, PlayerCarWeightPenalty`; `bool OnPitRoad, PlayerCarInPitStall, IsOnTrack, IsReplayPlaying, HasCarIdx`; `CarArray<int> CarIdxPosition, CarIdxLapCompleted, CarIdxTrackSurface, CarIdxClass`; `CarArray<float> CarIdxLapDistPct, CarIdxLastLapTime`; `CarArray<bool> CarIdxOnPitRoad`
  - `enum TelemetryVarId` (um membro por var acima, mesmo nome) e `static class TelemetryVars { static IReadOnlyList<VarSpec> All }` (nome iRacing, tipo nativo, count 64 para `CarIdx*`)
  - `readonly record struct VarSlot(int Offset, VarType Type, int Count)` (`Offset = -1` = ausente)
  - `sealed class VarLayout { static VarLayout Resolve(IReadOnlyList<VarHeader> headers); VarSlot this[TelemetryVarId id]; bool Has(TelemetryVarId id); bool HasCarIdx; string FuelUnit; bool IsElectric }` (`IsElectric` = `FuelUnit` igual a `kWh`, ignorando caixa)
  - `static class TelemetryFrameReader { static void Read(ReadOnlySpan<byte> row, VarLayout layout, ref TelemetryFrame frame); }`: converte do tipo declarado (ex.: int → double) e zera o que está ausente
  - `enum ReadResult { Frame, NoData, EndOfStream, Disconnected }`, `enum ReplaySpeed { Max, X1, X10 }`
  - `interface ITelemetrySource : IDisposable { ReadResult TryRead(ref TelemetryFrame frame); int SessionInfoVersion { get; } string ReadSessionInfoYaml(); VarLayout Layout { get; } }`
  - `sealed class IbtTelemetrySource : ITelemetrySource` com `(string path, ReplaySpeed speed = ReplaySpeed.Max)`; `SessionInfoVersion` = 1; em `X1`/`X10`, o ritmo segue um prazo absoluto (`t0_wall + (SessionTime − t0_sim)/speed`, medido com `Stopwatch`), com `Thread.Sleep` só quando faltam ≥ 2 ms. Assim o `Sleep` de ~15,6 ms do Windows não acumula atraso, e **nunca** se chama `timeBeginPeriod`
  - Teste: `IbtFileBuilder.WriteFrames(string path, string yaml, IEnumerable<TelemetryFrame> frames, bool includeCarIdx = true, string fuelUnit = "l")`

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Fact] public void Round_trips_scalar_and_caridx_fields()
{ // 3 frames montados à mão (SessionTime 1.0/1.0167/1.0333, Lap 5, LapDistPct 0.1/0.2/0.3, FuelLevel 50/49.99/49.98,
  // SessionFlags 0x4000, OnPitRoad true no 2º, CarIdxPosition[7]=1, CarIdxLastLapTime[7]=99.5f)
  // WriteFrames → IbtTelemetrySource: TryRead devolve Frame 3× e depois EndOfStream; todos os campos batem; HasCarIdx == true
}
[Fact] public void Missing_caridx_vars_yield_HasCarIdx_false_and_zeroed_arrays() { /* includeCarIdx:false */ }
[Fact] public void Electric_unit_is_detected() { /* fuelUnit:"kWh" → Layout.IsElectric == true */ }
[Fact] public void Reading_frames_allocates_nothing_after_first_read()
{ // 2000 frames sintéticos; lê 1 frame (warm-up); mede GC.GetAllocatedBytesForCurrentThread() ao redor dos outros 1999 TryRead; Assert.Equal(0, delta)
}
[Fact] public void Paced_replay_does_not_drift() { /* 120 frames a 60 Hz em X10 → tempo de parede entre 0.19 e 0.30 s (passa com o Sleep de 15,6 ms do Windows) */ }
[Fact] public void Fixture_first_frame_is_mid_session_mid_lap()
{ // RequireZolder; 1º frame: SessionTime ≈ 4264 (±1), LapDistPct ≈ 0.067 (±0.005), FuelLevel em (90,100]; Layout.HasCarIdx == false; IsElectric == false
}
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test tests/FuelPace.Tests --filter "FullyQualifiedName~IbtTelemetrySourceTests"` → falha de compilação.

- [ ] **Step 3: Implementar**

`TelemetryFrameReader.Read` faz uma atribuição explícita por campo (nada de reflection), com helpers `ReadDouble/ReadFloat/ReadInt/ReadUInt/ReadBool(ReadOnlySpan<byte> row, VarSlot s, int index = 0)` via `BinaryPrimitives`. A fonte guarda um `byte[] row` de `BufLen` alocado no construtor e um índice de registro.

- [ ] **Step 4: Rodar e ver passar** — mesmo comando → todos passam (o da fixture pode ser ignorado).

- [ ] **Step 5: Commit** — `git commit -m "feat(core): telemetry frame, var layout and .ibt replay source"`

---

### Task 3: `SessionInfoParser`

**Files:**
- Create: `src/FuelPace.Core/Telemetry/{SessionInfo,SessionInfoParser}.cs`
- Create: `tests/FuelPace.Tests/Support/SampleYaml.cs`
- Test: `tests/FuelPace.Tests/Telemetry/SessionInfoParserTests.cs`

**Interfaces:**
- Produces:
  - `sealed record SessionDef(int Num, string Type, int? Laps, double? TimeS)`
  - `sealed record SessionInfo(int DriverCarIdx, string CarPath, string CarScreenName, string CarScreenNameShort, string TrackName, string TrackDisplayShortName, string TrackDisplayName, string TrackConfigName, double TrackLengthKm, double TankCapacityL, double FuelKgPerL, double EstLapTimeS, IReadOnlyList<SessionDef> Sessions)` com `SessionDef? FindSession(int num)` e `string ProfileKey => $"{CarPath}__{TrackName}"`
  - `static class SessionInfoParser { static SessionInfo Parse(string yaml); static string Sanitize(string yaml); }`
  - Teste: `SampleYaml.Zolder` (YAML mínimo com os campos da §5.2, valores da fixture, 2 drivers: CarIdx 0 = outro carro com `UserName: Joe: The "Rocket"` e `TeamName: Team #1`; CarIdx 3 = jogador, `DriverCarIdx: 3`; sessões: 0 Practice unlimited/`7200.0000 sec`, 1 Race laps `25`/`unlimited`); `SampleYaml.WithSessions(params string[] sessionBlocks)`
- Dependência: `dotnet add src/FuelPace.Core package YamlDotNet`

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Parses_fixture_like_yaml()
{
    var s = SessionInfoParser.Parse(SampleYaml.Zolder);
    Assert.Equal(3, s.DriverCarIdx);
    Assert.Equal("ligierjsp320", s.CarPath);
    Assert.Equal("Ligier JS P320", s.CarScreenName);
    Assert.Equal("Ligier", s.CarScreenNameShort);          // SampleYaml: CarScreenNameShort: Ligier
    Assert.Equal("Zolder", s.TrackDisplayShortName);       // SampleYaml: TrackDisplayShortName: Zolder
    Assert.Equal("zolder alt", s.TrackName);
    Assert.Equal("Zolder Circuit", s.TrackDisplayName);
    Assert.Equal("Alternate", s.TrackConfigName);
    Assert.Equal(3.9342, s.TrackLengthKm, 4);
    Assert.Equal(100.0, s.TankCapacityL, 3);   // 100.0 × 1.000
    Assert.Equal(0.75, s.FuelKgPerL, 3);
    Assert.Equal(78.9567, s.EstLapTimeS, 4);
    Assert.Equal(new SessionDef(0, "Practice", null, 7200), s.FindSession(0));
    Assert.Equal(new SessionDef(1, "Race", 25, null), s.FindSession(1));
    Assert.Equal("ligierjsp320__zolder alt", s.ProfileKey);
}
[Fact] public void Tank_capacity_applies_max_fuel_pct() { /* DriverCarMaxFuelPct: 0.600 → 60.0 */ }
[Fact] public void Sanitize_quotes_values_with_colons_hashes_and_quotes() { /* Parse não lança; o driver 0 não afeta o resultado */ }
[Fact] public void Crlf_yaml_parses_identically() { /* SampleYaml.Zolder.Replace("\n", "\r\n") → mesmo SessionInfo (nenhum valor termina em '\r') */ }
[Fact] public void Fixture_yaml_parses() { /* RequireZolder → mesmos CarPath/TrackName/TankCapacityL/EstLapTimeS; Session 0 = Practice, Laps null, TimeS 7200 */ }
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar**

`Sanitize`: primeiro normalizar `\r\n` e `\r` para `\n`. Depois, para cada linha que casa com `^(\s*(?:- )?[A-Za-z0-9_]+: )(.+?)\s*$` e cujo valor não começa com `'` nem `"`, trocar o valor por `'` + valor com `'` duplicado + `'`. Desserializar (YamlDotNet, `IgnoreUnmatchedProperties`) em DTOs privados com todos os escalares como `string`. Converter com um helper `LeadingNumber("3.9342 km") → 3.9342` (InvariantCulture); `"unlimited"` → `null`.

- [ ] **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): session info YAML parser with iRacing sanitization"`

---

### Task 4: `LapDetector` — viradas, interpolação, tempo oficial, perfil de consumo

**Files:**
- Create: `src/FuelPace.Core/Laps/{LapReasons,FuelProfile101,LapStats,CompletedLap,ILapSink,LapValidator,LapDetector}.cs`
- Create: `tests/FuelPace.Tests/Support/LapSim.cs`
- Test: `tests/FuelPace.Tests/Laps/LapDetectorTests.cs`

**Interfaces:**
- Consumes: `TelemetryFrame` (Task 2)
- Produces:
  - `[Flags] enum LapReasons : ushort { None = 0, Pit = 1, Incident = 2, OffTrack = 4, Caution = 8, Reset = 16, Refuel = 32, Incomplete = 64, Outlier = 128, Manual = 256 }`
  - `static class LapReasonCodes { string[] ToCodes(LapReasons r); LapReasons FromCodes(IEnumerable<string> codes); }` com códigos `pit, incident, offtrack, caution, reset, refuel, incomplete, outlier, manual`, sempre nessa ordem
  - `[InlineArray(101)] struct FuelProfile101 { float _e; }`
  - `struct LapStats { bool PitSeen; int IncidentsAtStart, IncidentsAtEnd; double OffTrackSeconds; bool CautionSeen, ResetSeen, RefuelSeen, StartObserved, EndObserved; }`
  - `static class LapValidator { const uint CautionMask = 0x0008 | 0x4000 | 0x8000; const double OffTrackLimitS = 0.5, RefuelThresholdL = 0.05; static LapReasons Evaluate(in LapStats s); }`: neste task, só a regra `Incomplete` (`!StartObserved || !EndObserved`)
  - `struct CompletedLap { int LapNumber; double StartTime, EndTime, TimeS /*NaN = sem tempo*/, MeasuredTimeS, FuelUsedL, FullThrottlePct; LapReasons Reasons; LapStats Stats; bool HasProfile; FuelProfile101 FuelProfile /*fração acumulada 0..1*/; }`
  - `interface ILapSink { void OnLapCompleted(in CompletedLap lap); }`
  - `sealed class LapDetector` com `(ILapSink sink)`, `void SetTrackLength(double meters)`, `void Update(in TelemetryFrame f)`, `void Flush()` (resolve a volta pendente pelo timeout e emite a volta em andamento com `EndObserved=false`), `bool TrySnapshotInProgress(out CompletedLap lap)`, `double CurrentLapFuelUsedL`, `double CurrentLapDistPct`, `bool CurrentLapHasPit`, `double CurrentLapStartTime`
  - Teste: `LapSim` com `(double lapTimeS = 80, double fuelPerLapL = 2.4, double startFuelL = 100, double startPct = 0.0, int startLap = 1, double officialTimeDelayS = 1.5, bool publishOfficialTimes = true, double fullThrottleFraction = 0.65, double hz = 60)`; mutadores encadeáveis `LapTimes(params double[])`, `FuelPerLap(params double[])`, `OffTrack(int lap, double fromPct, double seconds)`, `Incident(int lap, double atPct, int count = 1)`, `PitRoad(int lap, double fromPct, double toPct)`, `Caution(int lap, double fromPct, double toPct, uint flags = 0x4000)`, `Refuel(int lap, double atPct, double liters)`, `Tow(int lap, double atPct)`, `TimeGap(int lap, double atPct, double seconds)`, `TimeBackwards(int lap, double atPct, double seconds)`, `IncrementLapEarlyFrames(int frames)`, `SessionLaps(int total)`, `TimeRace(double totalS)`, `Leader(...)` (opcional, para Task 14); `IEnumerable<TelemetryFrame> Run(int laps, double endPct = 0.0)`. Consumo acumulado na volta: `f(p) = p − 0.04·sin(πp)` × consumo da volta. `LapLastLapTime` passa ao tempo da volta `officialTimeDelayS` depois da virada (ou fica `-1` se `!publishOfficialTimes`). `Lap` incrementa no frame da virada (ou N frames antes, com `IncrementLapEarlyFrames`). `IsOnTrack = true`, `SessionState = 4`, `Speed = trackLen/lapTime`, track length 4000 m.

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Emits_complete_laps_with_interpolated_fuel_and_time()
{ // LapSim(startPct:0.5, startLap:1).Run(laps:3)  → 1ª emitida: LapNumber 1, StartObserved false, Reasons == Incomplete
  // voltas 2 e 3: FuelUsedL 2.4 ±0.001; MeasuredTimeS 80 ±0.02; TimeS 80 ±0.001; Reasons None; HasProfile
}
[Fact] public void Waits_for_delayed_LapLastLapTime() { /* LapTimes(80, 81.234): TimeS da volta 2 = 81.234 ±0.001; emitida só com SessionTime ≥ EndTime + 1.5 */ }
[Fact] public void Falls_back_to_measured_time_after_5s() { /* officialTimeDelayS: 99 → TimeS == MeasuredTimeS ±0.001, emitida a EndTime + 5 */ }
[Fact] public void Non_positive_official_time_means_no_time() { /* publishOfficialTimes:false → double.IsNaN(TimeS) */ }
[Fact] public void Fuel_profile_is_cumulative_fraction() { /* volta completa: [0]=0, [100]=1, [50] = 0.46 ±0.002, monotônico */ }
[Fact] public void Flush_emits_in_progress_lap_as_incomplete() { /* Run(2, endPct:0.4) + Flush: última EndObserved false, Incomplete, FuelUsedL ≈ 2.4·f(0.4) ±0.005, HasProfile false */ }
[Fact] public void Lap_number_is_Lap_value_at_mid_lap() { /* IncrementLapEarlyFrames(3): números 1,2,3 sem duplicar nem pular */ }
[Fact] public void Full_throttle_pct_is_time_weighted() { /* fullThrottleFraction 0.6 → 60 ±1 */ }
[Fact] public void Lap_increment_without_wrap_ends_lap_with_reset() { /* frame com Lap+1 e pct contínuo (0.4→0.41); depois de 1 s: volta emitida com Stats.ResetSeen */ }
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar `LapDetector`**

Regras que os testes não determinam sozinhos:
- **Virada primária:** `prevPct > 0.9 && pct < 0.1`. Fração `a = (1 − prevPct) / ((1 − prevPct) + pct)`; `SessionTime` e `FuelLevel` interpolados linearmente com `a` entre o frame anterior e o atual. É ao mesmo tempo o fim da volta que termina e o início da nova.
- **Virada secundária:** `Lap > prevLap` sem virada primária num raio de 1,0 s. Fecha a volta no frame em que se detecta (1 s depois do incremento), com os valores desse frame, e marca `ResetSeen`.
- **Número da volta:** `Lap` no primeiro frame com `pct ≥ 0.5`; se a volta nunca chega lá, o `Lap` do último frame.
- **Tempo oficial:** na virada, guardar `prevLLT = f.LapLastLapTime` e deixar a volta pendente. Em cada frame seguinte: se `LapLastLapTime != prevLLT`, `TimeS = valor > 0 ? valor : NaN`. Se passarem 5 s, `TimeS = LapLastLapTime ≤ 0 ? NaN : MeasuredTimeS`. Uma nova virada com volta ainda pendente resolve a pendente pelo timeout. Só existe um slot pendente.
- **Perfil:** ao cruzar cada `k/100` (k = 1..99) dentro da volta, gravar o consumo acumulado interpolado. No fim, dividir pelo total (`HasProfile` só com início e fim observados e total > 0).
- `dt` usado nos acumuladores = `clamp(SessionTime − prevTime, 0, 0.1)`.
- Sem alocação: `CompletedLap` é montado num campo e passado por `in`.

- [ ] **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): lap detector with interpolation, delayed lap time and fuel profile"`

---

### Task 5: Regras de validação de volta (§6.3)

**Files:**
- Modify: `src/FuelPace.Core/Laps/LapValidator.cs`, `src/FuelPace.Core/Laps/LapDetector.cs` (coleta dos `LapStats`)
- Test: `tests/FuelPace.Tests/Laps/LapValidatorTests.cs`

**Interfaces:**
- Consumes: `LapDetector`, `LapStats`, `LapSim` (Task 4)
- Produces: `LapValidator.Evaluate` com todas as regras. `LapDetector` preenche `LapStats` por volta.

- [ ] **Step 1: Testes que falham** (todos via `LapSim` → `LapDetector`, olhando a volta indicada)

```csharp
[Fact] public void Pit_road_marks_pit() { /* PitRoad(2, 0.9, 1.0) → volta 2 contém Pit; volta 1 None */ }
[Fact] public void Incident_increase_marks_incident() { /* Incident(2, 0.3) → volta 2 == Incident; 1 e 3 None */ }
[Fact] public void Offtrack_over_half_second_marks_offtrack() { /* OffTrack(2, 0.4, 0.6) → OffTrack */ }
[Fact] public void Offtrack_below_limit_is_ignored() { /* OffTrack(2, 0.4, 0.12) → None (caso da volta 20 da fixture) */ }
[Fact] public void Caution_flags_mark_caution() { /* flags 0x4000, 0x8000 e 0x0008 → Caution */ }
[Fact] public void Other_flags_do_not_mark_caution() { /* flags 0x0004 (green) e 0x0001 → None */ }
[Fact] public void Refuel_above_threshold_marks_refuel() { /* Refuel(2, 0.5, 1.0) → Refuel; Refuel(2, 0.5, 0.04) → None */ }
[Fact] public void Tow_marks_reset() { /* Tow(2, 0.5) → Reset */ }
[Fact] public void Not_on_track_marks_reset() { /* IsOnTrack false num frame → Reset */ }
[Fact] public void Impossible_pct_jump_marks_reset() { /* pct 0.30→0.70 em 1 frame, Speed 50 m/s, pista 4000 m → Reset */ }
[Fact] public void Session_time_gap_marks_reset() { /* TimeGap(2, 0.5, 3.0) → Reset; FuelUsedL ainda ≈ 2.4 */ }
[Fact] public void Session_time_going_backwards_marks_reset_without_throwing() { /* TimeBackwards(2, 0.5, 10) → Reset; nenhuma volta com FuelUsedL < 0 ou MeasuredTimeS < 0 */ }
[Fact] public void Multiple_reasons_accumulate_in_code_order() { /* OffTrack + Incident na mesma volta → ToCodes == ["incident","offtrack"] */ }
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar**

`Evaluate`: `Pit` se `PitSeen`; `Incident` se `IncidentsAtEnd > IncidentsAtStart`; `OffTrack` se `OffTrackSeconds > 0.5`; `Caution`, `Reset`, `Refuel` pelas flags; `Incomplete` como antes. Coleta no detector:
- `PitSeen |= OnPitRoad || PlayerCarInPitStall`
- `OffTrackSeconds += dt` quando `PlayerTrackSurface == 0`
- `CautionSeen |= (SessionFlags & CautionMask) != 0`
- `RefuelSeen` quando `FuelLevel > minFuelNaVolta + 0.05`
- `ResetSeen` quando: `!IsOnTrack`; `PlayerCarTowTime > 0`; `SessionTime − prev > 1.0` ou `< 0`; ou, com a extensão da pista conhecida, `|Δpct (ajustado para a virada)| × trackLenM > max(Speed, prevSpeed) × dtBruto × 2 + 30`
- Com `SessionTime` voltando: `dt = 0` e nenhum consumo negativo é acumulado.

- [ ] **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): lap validation rules (pit, incident, offtrack, caution, reset, refuel)"`

---

### Task 6: `CaptureSession`, regra de outlier e critério de aceite 1

**Files:**
- Create: `src/FuelPace.Core/Capture/{CaptureConditions,CapturedLap,CaptureRecord,OutlierRule,CaptureSession}.cs`
- Create: `tests/FuelPace.Tests/Support/{ReplayHarness,FixedTimeProvider}.cs`
- Test: `tests/FuelPace.Tests/Capture/CaptureSessionTests.cs`, `tests/FuelPace.Tests/Capture/FixtureCaptureTests.cs`

**Interfaces:**
- Consumes: `CompletedLap`, `ILapSink`, `LapReasons`, `LapReasonCodes` (Task 4), `IbtTelemetrySource` (Task 2), `SessionInfoParser` (Task 3)
- Produces:
  - `sealed record CaptureConditions(double TrackTempC, double AirTempC, int Wetness, double PowerAdjustPct, double WeightPenaltyKg, double TankCapacityL)`
  - `sealed class CapturedLap { int Lap; double? TimeS; double FuelL; double FullThrottlePct; LapReasons Reasons; float[]? FuelProfile; bool Valid => Reasons == LapReasons.None; static CapturedLap From(in CompletedLap l) }` (valores arredondados: tempo a 3 casas, L a 4, perfil a 4)
  - `sealed class CaptureRecord { string Id; bool Active = true; string SessionType; CaptureConditions Conditions; List<CapturedLap> Laps; }`
  - `static class OutlierRule { const double Factor = 1.07; static void Apply(IList<CapturedLap> laps); }`
  - `sealed class CaptureSession : ILapSink` com `(TimeProvider clock)`, `bool IsActive`, `int ValidCount`, `int TotalCount`, `string ProfileKey`, `void Start(double sessionTime, string profileKey, string sessionType, CaptureConditions conditions)`, `void OnLapCompleted(in CompletedLap lap)`, `CaptureRecord Stop(bool hasInProgress, in CompletedLap inProgress)`
  - Teste: `ReplayHarness.CaptureWholeFile(string ibtPath) → (CaptureRecord record, SessionInfo info)`: captura ligada antes do 1º frame, `detector.Flush()` no fim e depois `Stop(false, default)`

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Lap_in_progress_at_start_is_incomplete() { /* Start com SessionTime no meio da volta 2 → volta 2 gravada com Incomplete; volta 3 None */ }
[Fact] public void Lap_in_progress_at_stop_is_recorded_incomplete() { /* Stop(true, snapshot) → última CapturedLap com Incomplete */ }
[Fact] public void Laps_outside_capture_window_are_ignored() { /* voltas completadas antes do Start e depois do Stop não aparecem */ }
[Fact] public void Counts_track_valid_and_total() { /* 3 válidas + 1 incident → ValidCount 3, TotalCount 4 */ }
[Fact] public void Outlier_rule_flags_slow_otherwise_valid_laps()
{ // tempos 80, 80.2, 79.9, 80.1, 90 (todas válidas) → só a de 90 recebe Outlier
  // volta inválida (incident) com 95 → não recebe Outlier e não entra na mediana
  // Apply 2× → idempotente; com menos de 2 outras válidas → nada é marcado
}
[Fact] public void Id_is_utc_iso_of_start() { /* FixedTimeProvider(2026-10-29T20:03:40Z) → "2026-10-29T20:03:40Z" */ }
[Fact] public void Fixture_capture_matches_reference_table()   // critério de aceite 1
{
    var (rec, _) = ReplayHarness.CaptureWholeFile(Fixtures.RequireZolder());
    var expected = new (int lap, double? time, double fuel, string[] reasons)[] {
        (16, 80.897, 2.382, []), (17, 80.272, 2.404, []), (18, 79.964, 2.373, []),
        (19, 93.175, 2.388, ["incident", "offtrack"]), (20, 79.133, 2.386, ["incident"]),
        (21, 79.514, 2.403, []), (22, 79.134, 2.389, []), (23, 81.062, 2.289, []),
        (24, null, 0.986, ["incomplete"]) };
    // para cada esperado: lap existe; reasons iguais como conjunto; |fuel| ≤ 0.005; time ±0.01 (ou null)
    // volta 15: !Valid, reasons ⊇ {pit, incident}, TimeS null
    // nenhuma outra volta; Laps.Count(l => l.Valid) == 6
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar**

`OnLapCompleted` ignora tudo quando inativa. Uma volta com `StartTime < captureStartTime` recebe `Incomplete`. `Stop` grava o snapshot (se houver) como `Incomplete`, roda `OutlierRule.Apply` e devolve o registro. `OutlierRule.Apply`: primeiro limpa `Outlier` de todas; a base são as voltas com `Reasons == None` e `TimeS` não nulo; para cada uma, mediana das **outras** da base (precisa de ≥ 2); se `TimeS > 1.07 × mediana`, adiciona `Outlier`. Tudo numa única passada, sobre a base original.

- [ ] **Step 4: Rodar e ver passar.** Se o teste da fixture falhar em `caution` em alguma volta válida, investigar quais bits de `SessionFlags` aparecem na fixture antes de mexer no `CautionMask`.

- [ ] **Step 5: Commit** — `git commit -m "feat(core): capture session with outlier rule; fixture acceptance test"`

---

### Task 7: `BaselineCalculator` (§6.4) e critério 2

**Files:**
- Create: `src/FuelPace.Core/Baselines/{LapSample,Baseline,BaselineCalculator}.cs`
- Test: `tests/FuelPace.Tests/Baselines/BaselineCalculatorTests.cs`

**Interfaces:**
- Consumes: `CapturedLap`, `CaptureConditions` (Task 6), `ReplayHarness` (Task 6)
- Produces:
  - `readonly record struct LapSample(double FuelL, double TimeS, double FullThrottlePct, float[]? FuelProfile)`
  - `enum Confidence { Baixa, Media, Alta }`
  - `sealed record FuelVsTime(double A, double B)` (`tempo = A + B·consumo`)
  - `sealed record BopSignature(double PowerAdjustPct, double WeightPenaltyKg, double TankCapacityL)` com `bool Matches(BopSignature other)` (tolerâncias 0,01 %, 0,1 kg, 0,1 L)
  - `sealed record Baseline(DateTimeOffset ComputedAt, int ValidLaps, double FuelMeanL, double FuelMedianL, double FuelStdL, double FuelMinL, double FuelP10L, double FuelP90L, double LapMeanS, double LapMedianS, double LapStdS, double LapBestS, double FullThrottleMeanPct, double AutonomyLaps, double AutonomyMin, FuelVsTime? FuelVsTime, Confidence Confidence, float[] FuelProfile, IReadOnlyList<BopSignature> Conditions)`
  - `static class BaselineCalculator { static Baseline? Compute(IReadOnlyList<LapSample> validLaps, double tankCapacityL, IReadOnlyList<BopSignature> conditions, DateTimeOffset computedAt); static float[] MeanProfile(IReadOnlyList<float[]> profiles); }`

- [ ] **Step 1: Testes que falham**

```csharp
static readonly LapSample[] Fixture6 = [ new(2.382,80.897,64,null), new(2.404,80.272,66,null), new(2.373,79.964,65,null),
                                         new(2.403,79.514,67,null), new(2.389,79.134,64,null), new(2.289,81.062,58,null) ];
[Fact] public void Matches_reference_baseline()
{
    var b = BaselineCalculator.Compute(Fixture6, 100, [], T0)!;
    Assert.Equal(6, b.ValidLaps);
    Assert.Equal(2.3733, b.FuelMeanL, 3);  Assert.Equal(2.3855, b.FuelMedianL, 3);
    Assert.Equal(2.289, b.FuelMinL, 3);    Assert.Equal(0.0430, b.FuelStdL, 3);   // desvio amostral (n−1)
    Assert.Equal(2.331, b.FuelP10L, 3);    Assert.Equal(2.4035, b.FuelP90L, 3);   // percentil com interpolação linear
    Assert.Equal(80.1405, b.LapMeanS, 3);  Assert.Equal(80.118, b.LapMedianS, 3); Assert.Equal(79.134, b.LapBestS, 3);
    Assert.Equal(42.14, b.AutonomyLaps, 1); Assert.Equal(56.3, b.AutonomyMin, 1);
    Assert.Equal(Confidence.Media, b.Confidence);
    Assert.Null(b.FuelVsTime);
}
[Fact] public void Fewer_than_3_laps_has_no_baseline() { /* 2 → null */ }
[Theory] [InlineData(3, Confidence.Baixa)] [InlineData(4, Confidence.Baixa)] [InlineData(5, Confidence.Media)] [InlineData(9, Confidence.Media)] [InlineData(10, Confidence.Alta)]
public void Confidence_by_count(int n, Confidence c) { }
[Fact] public void Regression_when_enough_economy_laps()
{ // 7× 2.40 + 2.30, 2.29, 2.31; tempo = 200 − 50·consumo → FuelVsTime.A ≈ 200 (±0.01), B ≈ −50 (±0.01)
}
[Fact] public void No_regression_when_range_below_2pct() { /* 10 voltas entre 2.38 e 2.40 → null */ }
[Fact] public void No_regression_with_single_economy_lap() { /* 9× 2.40 + 1× 2.30 → null */ }
[Fact] public void Mean_profile_averages_pointwise() { /* 2 perfis → média em cada índice; voltas sem perfil ignoradas; nenhum perfil → array vazio */ }
[Fact] public void Fixture_baseline_matches_reference()   // critério 2
{ // ReplayHarness → voltas válidas → LapSample → Compute(tank 100): FuelMean 2.373, Median 2.386, Min 2.289, Std 0.043,
  // LapMean 80.140, LapMedian 80.118, todos ±0.005; Confidence Media; FuelVsTime null; FuelProfile.Length == 101
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar** com mínimos quadrados ordinários para a regressão e percentil `rank = p × (n − 1)` com interpolação linear entre as posições vizinhas. Pode usar LINQ (não é hot path).

- [ ] **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): baseline aggregates, regression and confidence"`

---

### Task 8: Modelo do perfil, `ProfileStore`, operações e fila de I/O

**Files:**
- Create: `src/FuelPace.Core/Storage/{ProfileDocument,ProfileJsonContext,ProfileStore,ProfileOperations,BackgroundWorkQueue}.cs`
- Test: `tests/FuelPace.Tests/Storage/ProfileStoreTests.cs`, `ProfileOperationsTests.cs`, `BackgroundWorkQueueTests.cs`

**Interfaces:**
- Consumes: `CaptureRecord`, `CapturedLap`, `CaptureConditions`, `OutlierRule` (Task 6); `Baseline`, `BaselineCalculator`, `BopSignature` (Task 7); `SessionInfo` (Task 3)
- Produces:
  - `sealed record CarInfo(string Path, string Name, double TankCapacityL, double FuelKgPerL)`, `sealed record TrackInfo(string Name, string Display, string Config, double LengthKm)`
  - `sealed class ProfileDocument { int SchemaVersion = 1; CarInfo Car; TrackInfo Track; List<CaptureRecord> Captures; Baseline? Baseline; static ProfileDocument CreateFor(SessionInfo info); }`
  - `ProfileJsonContext : JsonSerializerContext` (camelCase, `WriteIndented`, `LapReasons` serializado como array de códigos via conversor próprio, `Confidence` como `"baixa"/"media"/"alta"`, `CapturedLap.Valid` escrito e ignorado na leitura)
  - `static class AtomicFile { static void WriteAllText(string path, string contents); }` (temp + rename + novas tentativas, regras abaixo; o `settings.json` do App também usa)
  - `sealed class ProfileStore` com `(string rootDir)`, `static string DefaultRoot` (`ApplicationData/FuelPace`), `static string FileNameFor(string carPath, string trackName)`, `ProfileDocument? Load(string carPath, string trackName)`, `void Save(ProfileDocument doc)`
  - `static class ProfileOperations { AddCapture(doc, rec); SetLapExcluded(doc, captureId, lap, excluded); SetCaptureActive(doc, captureId, active); DeleteCapture(doc, captureId); RecomputeBaseline(doc, DateTimeOffset now); }`: as quatro primeiras recebem também `DateTimeOffset now` e terminam chamando `RecomputeBaseline`
  - `sealed class BackgroundWorkQueue : IDisposable` com `(string threadName)`, `void Enqueue(Action work)`, `bool WaitIdle(TimeSpan timeout)`. `Dispose` esvazia a fila; uma exceção numa tarefa vai para `Action<Exception>? OnError` e a fila segue.

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void File_name_replaces_spaces_and_invalid_chars() { Assert.Equal("ligierjsp320__zolder_alt.json", ProfileStore.FileNameFor("ligierjsp320", "zolder alt")); }
[Theory] // regras do Windows aplicadas também no Linux (Path.GetInvalidFileNameChars() do Linux só tem '/' e '\0')
[InlineData("car", "a<b>c:d\"e/f\\g|h?i*j", "car__a_b_c_d_e_f_g_h_i_j.json")]
[InlineData("CON", "Track.", "_con__track_.json")]          // nome reservado + ponto final
[InlineData("Car", "Zolder Alt", "car__zolder_alt.json")]    // minúsculas: NTFS não diferencia maiúsculas
public void File_name_is_windows_safe(string car, string track, string expected) { Assert.Equal(expected, ProfileStore.FileNameFor(car, track)); }
[Fact] public void Save_retries_when_target_is_locked()
{ // só no Windows: Assert.SkipUnless(OperatingSystem.IsWindows(), ...); abre o .json com FileShare.None, solta depois de 150 ms numa Task; Save conclui sem exceção
}
[Fact] public void Save_then_load_round_trips() { /* doc com 1 captura (2 voltas, uma com ["offtrack","incident"]) + baseline → igual campo a campo */ }
[Fact] public void Json_uses_prd_schema_names()
{ // texto contém "schemaVersion": 1, "tankCapacityL", "fuelKgPerL", "lengthKm", "sessionType", "fullThrottlePct",
  // "reasons": [ "incident", "offtrack" ] (ordem dos códigos), "confidence": "media", "valid": false
}
[Fact] public void Save_is_atomic_and_leaves_no_temp() { /* salvar 2× → só um .json no diretório, conteúdo da 2ª versão */ }
[Fact] public void Corrupt_file_is_quarantined_and_load_returns_null() { /* "{ trunc" → Load null; existe *.json.corrupt; .json original sumiu */ }
[Fact] public void Missing_file_returns_null() { }
// ProfileOperationsTests
[Fact] public void Excluding_a_lap_marks_manual_and_recomputes_baseline() { /* 6 válidas → exclui a 23 → ValidLaps 5, FuelMinL 2.373; reinclui → 6, Manual removido */ }
[Fact] public void Inactive_capture_is_left_out_of_baseline() { }
[Fact] public void Deleting_last_capture_clears_baseline() { /* Baseline == null */ }
[Fact] public void Baseline_conditions_list_distinct_bop_of_active_captures() { }
// BackgroundWorkQueueTests
[Fact] public void Runs_in_order_on_a_background_thread() { /* 3 itens registram ordem e thread id ≠ chamador */ }
[Fact] public void Exception_does_not_stop_queue() { }
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar**

`FileNameFor`: `ToLowerInvariant`; espaço e qualquer um de `< > : " / \ | ? *` ou caractere de controle viram `_`; um `.` ou espaço no fim de cada parte vira `_`; partes que, sem extensão, sejam `con, prn, aux, nul, com1–com9, lpt1–lpt9` ganham `_` na frente. `Save` escreve em `{file}.tmp` e faz `File.Move(tmp, file, overwrite: true)`. No Windows, o rename falha se outro processo (o Settings, o antivírus) estiver com o arquivo aberto, então `IOException`/`UnauthorizedAccessException` geram até 5 novas tentativas, com 50, 100, 200, 400 e 800 ms de espera. Se todas falharem, a exceção sobe e o `.tmp` fica no lugar. `Load` com `JsonException` renomeia para `{file}.corrupt` (sobrescrevendo um `.corrupt` anterior) e devolve `null`. `RecomputeBaseline`: roda `OutlierRule.Apply` em cada captura (o que respeita `Manual`), junta as voltas `Valid` das capturas ativas e chama `BaselineCalculator.Compute(…, doc.Car.TankCapacityL, condições distintas das ativas, now)`. A fila usa uma `Thread` dedicada (`IsBackground = true`, `Priority = BelowNormal`) consumindo uma `BlockingCollection<Action>`.

- [ ] **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): profile persistence with atomic writes and profile operations"`

---

### Task 9: `FuelProjection` e critério 8

**Files:**
- Create: `src/FuelPace.Core/Strategy/FuelProjection.cs`
- Test: `tests/FuelPace.Tests/Strategy/FuelProjectionTests.cs`

**Interfaces:**
- Consumes: `CapturedLap.FuelProfile` (Task 6), `BaselineCalculator.MeanProfile` (Task 7), `ReplayHarness`
- Produces: `static class FuelProjection { const double MinPct = 0.15; static double Interpolate(ReadOnlySpan<float> profile, double pct); static double Project(double usedSoFarL, double lapDistPct, ReadOnlySpan<float> refProfile); }`: `Project` devolve NaN se `pct < 0.15`; com perfil vazio usa `usedSoFar / pct`; se o valor interpolado for ≤ 0, NaN

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Hidden_below_15pct() { Assert.True(double.IsNaN(FuelProjection.Project(0.3, 0.14, Profile))); }
[Fact] public void Linear_without_profile() { Assert.Equal(2.4, FuelProjection.Project(1.2, 0.5, []), 6); }
[Fact] public void Uses_profile_interpolation() { /* perfil f(p)=p−0.04·sin(πp) em 101 pontos; usado = 2.4·f(0.505) a pct 0.505 → 2.4 ±0.002 */ }
[Fact] public void Leave_one_out_on_fixture_beats_linear()   // critério 8
{ // voltas válidas da fixture; para cada i: ref = MeanProfile(outras); para pct em {0.5, 0.6}:
  //   used = Interpolate(perfil_i, pct) × fuel_i;  |Project(used,pct,ref) − fuel_i| ≤ 0.07
  //   |used/pct − fuel_i| > 0.13
}
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar.** **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): fuel projection from consumption profile"`

---

### Task 10: `LeaderPaceTracker` (§7.3) e critério 10

**Files:**
- Create: `src/FuelPace.Core/Race/{LeaderPace,LeaderPaceTracker}.cs`
- Test: `tests/FuelPace.Tests/Race/LeaderPaceTrackerTests.cs`

**Interfaces:**
- Consumes: `TelemetryFrame`, `CarArray<T>` (Task 2), `LapValidator.CautionMask` (Task 4)
- Produces:
  - `enum LeaderPaceSource : byte { None, Mean, Best102, Held }`
  - `readonly record struct LeaderPace(int LeaderCarIdx, double PaceS, double LeaderLapDistPct, LeaderPaceSource Source, bool Frozen)` (`PaceS` NaN = sem dado; `LeaderCarIdx` −1 = sem líder)
  - `sealed class LeaderPaceTracker` com `const int MaxCars = 64, MaxHistory = 16`, `(int meanWindow = 5)`, `void Update(in TelemetryFrame f)`, `LeaderPace Current`, `int ValidLapCount(int carIdx)`, `void Reset()`

- [ ] **Step 1: Testes que falham** (frames montados por um helper local `RaceFrames`, que avança `SessionTime` a 60 Hz e, por carro, controla `LapCompleted`, `LastLapTime` com 1 s de atraso, `OnPitRoad`, `TrackSurface`, `Position` e as flags da sessão)

```csharp
[Fact] public void Mean_of_last_laps_of_leader() { /* car 5 Position 1, voltas 100, 101, 99 → PaceS 100, Source Mean */ }
[Fact] public void Uses_window_of_N() { /* meanWindow 2, voltas 100, 110, 90 → 100 (110, 90) */ }
[Fact] public void Single_lap_uses_best_times_102() { /* 1 volta de 100 → 102, Best102 */ }
[Fact] public void Pit_lap_is_not_buffered() { /* OnPitRoad durante a 2ª volta → ValidLapCount 2 de 3 */ }
[Fact] public void Not_in_world_lap_is_not_buffered() { /* TrackSurface −1 durante a volta → descartada */ }
[Fact] public void Slow_lap_over_107pct_of_median_is_rejected() { /* 100,100,100,120 → ValidLapCount 3 */ }
[Fact] public void Caution_freezes_pace_and_skips_laps() { /* com 0x4000: Frozen, PaceS igual ao anterior, Held; volta completada sob caution não entra */ }
[Fact] public void Leader_change_uses_new_leader_buffer_same_tick() // critério 10
{ // car 5 (voltas de 100) é líder; car 7 tem 3 voltas de 105; no frame em que car 7 vira Position 1: Current.LeaderCarIdx 7, PaceS 105, Source Mean
}
[Fact] public void Leader_in_pit_holds_last_pace() { /* líder entra no pit → Held, mesmo PaceS */ }
[Fact] public void No_leader_or_no_data_is_nan() { /* sem Position == 1 → PaceS NaN, Source None, LeaderCarIdx −1 */ }
[Fact] public void Missing_LastLapTime_update_drops_lap_after_5s() { }
[Fact] public void Update_allocates_nothing() { /* 10.000 Update com 20 carros ativos → 0 bytes */ }
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar**

Arrays fixos `float[64 × 16]` (ring buffer), `int[64]` com contadores e posição, `int[64]` com último `LapCompleted` (−1 = nunca visto; a 1ª observação só inicializa), flags por volta (`pit`, `notInWorld`, `caution`), pendente por carro (`pendingSince`, `prevLastLapTime`). No incremento de `LapCompleted`, a volta fica pendente com as flags da volta, que são zeradas. Quando `CarIdxLastLapTime` muda, a volta é aceita se não tiver flags, tiver tempo > 0 e, havendo ≥ 3 aceitas, tiver tempo ≤ 1,07 × mediana do buffer (calculada em `stackalloc float[16]`). Depois de 5 s sem mudança, a volta é descartada. `Current` é recalculado em todo `Update`, seguindo a ordem de fallback da §7.3.

- [ ] **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): leader pace tracker with per-car ring buffers"`

---

### Task 11: `LapsRemainingCalculator` e critério 9

**Files:**
- Create: `src/FuelPace.Core/Strategy/LapsRemainingCalculator.cs`
- Test: `tests/FuelPace.Tests/Strategy/LapsRemainingCalculatorTests.cs`

**Interfaces:**
- Produces: `static class LapsRemainingCalculator { const double CeilEpsilon = 1e-9; static double ByLaps(int sessionLapsRemainEx, double myPct); static double ByTime(double timeRemainS, double leaderPct, double leaderPaceS, double myPct, double myPaceS); static double AfterCheckered(double myPct); static double Stint(int stintLaps, int lapsDone, double myPct); }`
  - `ByTime`: `T = max(0, timeRemainS)`; com `leaderPaceS` NaN, usa `leaderPct = myPct` e `leaderPaceS = myPaceS`; `N_L = ceil(p_L + T/t_L + ε) − p_L`; `T_fin = N_L × t_L`; resultado `ceil(p_me + T_fin/t_me + ε) − p_me`; `myPaceS` NaN ou ≤ 0 → NaN
  - `ByLaps = SessionLapsRemainEx − myPct + 1`; `AfterCheckered = 1 − myPct`; `Stint = max(0, stintLaps − lapsDone − myPct)`

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Time_race_uses_leader_pace() { Assert.Equal(7.2, LapsRemainingCalculator.ByTime(600, 0.30, 100, 0.80, 102), 6); }   // critério 9
[Fact] public void Own_pace_gives_one_lap_less() { Assert.Equal(6.2, LapsRemainingCalculator.ByTime(600, 0.80, 102, 0.80, 102), 6); }
[Fact] public void Being_the_leader_equals_own_pace() { Assert.Equal(LapsRemainingCalculator.ByTime(600, 0.8, double.NaN, 0.8, 102), LapsRemainingCalculator.ByTime(600, 0.8, 102, 0.8, 102)); }
[Fact] public void Clock_at_zero_leader_finishes_current_lap() { Assert.Equal(1.2, LapsRemainingCalculator.ByTime(0, 0.30, 100, 0.80, 102), 6); }
[Fact] public void Negative_time_remaining_is_treated_as_zero() { /* −30 == mesmo resultado de 0 */ }
[Fact] public void Leader_crossing_exactly_at_zero_gets_one_more_lap() { /* T=0, p_L=0 → N_L = 1 */ }
[Fact] public void Laps_race_formula() { Assert.Equal(10.75, LapsRemainingCalculator.ByLaps(10, 0.25), 6); }
[Fact] public void After_checkered_is_rest_of_lap() { Assert.Equal(0.4, LapsRemainingCalculator.AfterCheckered(0.6), 6); }
[Fact] public void Stint_counts_down_and_never_negative() { Assert.Equal(19.5, LapsRemainingCalculator.Stint(25, 5, 0.5), 6); Assert.Equal(0, LapsRemainingCalculator.Stint(25, 26, 0.5)); }
[Fact] public void Nan_pace_gives_nan() { }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar.** **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): laps-remaining formulas for laps, time (leader) and stint"`

---

### Task 12: `FuelStrategyEngine` (§7.2–7.5), critérios 3, 4 e 9 (alvo)

**Files:**
- Create: `src/FuelPace.Core/Strategy/{StrategyConfig,StrategyInput,Insight,OverlayState,SignalHysteresis,FuelStrategyEngine}.cs`
- Test: `tests/FuelPace.Tests/Strategy/FuelStrategyEngineTests.cs`

**Interfaces:**
- Consumes: `Baseline`, `FuelVsTime` (Task 7), `LapsRemainingCalculator` (Task 11)
- Produces:
  - `enum LapsMode { Auto, Stint }`; `enum RaceKind : byte { Unlimited, Laps, Time }`
  - `sealed record StrategyConfig { double ReserveL = 0.5; int MeanWindow = 5; double MaxSavingFraction = 0.06; double ExtraLaps = 0; LapsMode Mode = Auto; int StintLaps = 25; bool PracticeLapsFromTime = true; }` (`init`; `MeanWindow` fora de 2–16 → `ArgumentOutOfRangeException` no `init`)
  - `struct StrategyInput { double SessionTime, FuelL, LapDistPct, LapProjectionL, LastLapL, StintMeanL, StintMeanLapS, LastLapTimeS, SessionTimeRemain, LeaderPaceS, LeaderLapDistPct, EstLapTimeS; bool LastLapInvalid, Inactive, IsElectric, Caution, IsRaceSession, LeaderFinished, IAmLeader, BopMismatch, Recording; int StintLapsDone, SessionLapsRemainEx, RecValid, RecTotal; RaceKind Race; }` (NaN = indisponível)
  - `enum SignalState : byte { Inactive, NoBaseline, NoTarget, Blue, Red, NotFeasible }`
  - `enum InsightKind : byte { None, ElectricUnsupported, StopRequired, BopMismatch, Save, Slack, OnTarget, NoBaseline }`; `readonly record struct Insight(InsightKind Kind, double A = double.NaN, double B = double.NaN, double C = double.NaN)` (StopRequired A = litros; Save A = L/v, B = % inteiro, C = s/v ou NaN; Slack e OnTarget A = litros)
  - `struct OverlayState { SignalState State; double FuelL, AutonomyLaps, AutonomyMin, LapProjectionL, LastLapL, MeanL, TargetL, LapsRemaining, FeasibleMinL, LastLapTimeS, MeanLapTimeS, BaselineLapTimeS; bool LastLapInvalid, MeanFromBaseline, TargetIsEstimate, IsStintMode, IsPractice, Recording, BopMismatch; int RecValid, RecTotal; Insight Insight; }`
  - `struct SignalHysteresis { const double HoldSeconds = 1.5; SignalState Apply(SignalState raw, double now); }` (implementado no Task 13; aqui pode ser um stub que devolve `raw`)
  - `static class FuelStrategyEngine { static OverlayState Compute(in StrategyInput input, Baseline? baseline, StrategyConfig cfg, ref SignalHysteresis hysteresis); }`

**Algoritmo (as decisões que a assinatura não fixa):**
1. `IsElectric` → `Inactive` + `ElectricUnsupported`. `Inactive` → só `FuelL` preenchido, `Insight.None`.
2. `myPace = StintMeanLapS ?? baseline.LapMeanS ?? EstLapTimeS`. `MeanL = StintMeanL`, ou, se NaN, `baseline.FuelMeanL` com `MeanFromBaseline = true`.
3. `L_rest`: em `Stint` → `Stint(StintLaps, StintLapsDone, pct)`; em `Auto` → `Laps`: `ByLaps + ExtraLaps`; `Time`: `LeaderFinished ? AfterCheckered : ByTime(T, usa líder só se IsRaceSession && !IAmLeader, …) + ExtraLaps`, com `Time` em sessão que não é corrida só se `PracticeLapsFromTime`; `Unlimited` → NaN. `L_rest` NaN → `NoTarget` (ou `NoBaseline`, se não houver baseline), com `TargetL` NaN e insight `None` (ou `NoBaseline`/`BopMismatch`); `L_rest ≤ 0` → `Inactive`. `IsPractice = !IsRaceSession`.
4. `TargetL = (FuelL − ReserveL) / L_rest`. `TargetIsEstimate = Caution && Race == Time`.
5. Sem baseline → `NoBaseline` + insight `NoBaseline` (autonomia continua a partir de `MeanL` se houver).
6. `ref = LapProjectionL`, senão `LastLapL`, senão `MeanL`. `feasible = min(FuelMeanL × (1 − MaxSaving), FuelMinL)`. `round2(Target) < round2(feasible)` → `NotFeasible`; senão `round2(ref) ≤ round2(Target)` → `Blue`; senão `Red`.
7. `displayed = hysteresis.Apply(raw, SessionTime)`. Insight pela prioridade da §7.5 sobre `displayed`: NF → `StopRequired(L_rest × feasible − (FuelL − ReserveL))`; `BopMismatch` → `BopMismatch`; Red → `Save(max(0, r(ref) − r(T)), pct, FuelVsTime is null ? NaN : −B × save)`; Blue com `Target ≥ FuelMeanL + FuelStdL` → `Slack(max(0, usable − L_rest × r(ref)))`, senão `OnTarget(…)`.
8. `AutonomyLaps = (FuelL − ReserveL) / MeanL`; `AutonomyMin = AutonomyLaps × myPace / 60`.

- [ ] **Step 1: Testes que falham** (helper `Input.LapsRace(double fuelL, double lRest, double refL)` monta `Race = Laps`, `LapDistPct = 0`, `SessionLapsRemainEx = (int)lRest − 1`, `LastLapL = refL`, `LapProjectionL = NaN`; `FixtureBaseline` vem de `BaselineCalculator.Compute(Fixture6, 100, …)`)

```csharp
[Fact] public void L19_is_blue_with_slack()          // critério 3
{ var s = Run(Input.LapsRace(50, 19, 2.38));
  Assert.Equal(2.61, Math.Round(s.TargetL, 2)); Assert.Equal(SignalState.Blue, s.State);
  Assert.Equal(InsightKind.Slack, s.Insight.Kind); Assert.Equal(4.28, s.Insight.A, 2); }
[Fact] public void L22_is_red_save_013_5pct()        // critério 3
{ var s = Run(Input.LapsRace(50, 22, 2.38));
  Assert.Equal(2.25, Math.Round(s.TargetL, 2)); Assert.Equal(SignalState.Red, s.State);
  Assert.Equal(new Insight(InsightKind.Save, 0.13, 5, double.NaN), Rounded(s.Insight)); }
[Fact] public void L23_is_not_feasible_missing_18() // critério 3
{ var s = Run(Input.LapsRace(50, 23, 2.38));
  Assert.Equal(SignalState.NotFeasible, s.State); Assert.Equal(2.23, Math.Round(s.FeasibleMinL, 2));
  Assert.Equal(InsightKind.StopRequired, s.Insight.Kind); Assert.Equal(1.8, Math.Round(s.Insight.A, 1)); }
[Theory] [InlineData(2.38)] [InlineData(2.384)]
public void Equal_after_rounding_is_blue(double refL)  // critério 4
{ var s = Run(Input.LapsRace(48.1, 20, refL)); Assert.Equal(2.38, Math.Round(s.TargetL, 2)); Assert.Equal(SignalState.Blue, s.State);
  Assert.Equal(InsightKind.OnTarget, s.Insight.Kind); Assert.Equal(0.0, s.Insight.A, 2); }
[Fact] public void Time_race_target_uses_leader()      // critério 9
{ // Race Time, IsRaceSession, SessionTimeRemain 600, LeaderLapDistPct 0.30, LeaderPaceS 100, LapDistPct 0.80, StintMeanLapS 102, FuelL 18
  // → LapsRemaining 7.2 ±1e-6, round2(TargetL) == 2.43
}
[Fact] public void Projection_takes_precedence_over_last_lap() { }
[Fact] public void Falls_back_to_mean_when_no_projection_nor_last_lap() { }
[Fact] public void Regression_adds_seconds_cost() { /* baseline with FuelVsTime(200, −50), L22 → Insight.C == 6.5 ±1e-9 */ }
[Fact] public void Bop_mismatch_outranks_save_but_not_stop() { /* L22 + BopMismatch → BopMismatch; L23 + BopMismatch → StopRequired */ }
[Fact] public void No_baseline_state_and_insight() { }
[Fact] public void Inactive_shows_only_fuel() { }
[Fact] public void Electric_is_inactive_with_unsupported_insight() { }
[Fact] public void Fuel_below_reserve_is_not_feasible_with_positive_missing() { /* FuelL 0.3, L_rest 2 → NF, A > 0 */ }
[Fact] public void Zero_or_negative_laps_remaining_is_inactive() { /* Stint mode, lapsDone ≥ StintLaps */ }
[Fact] public void Unlimited_auto_shows_no_target() { /* com baseline → NoTarget, TargetL NaN, FuelL/MeanL preenchidos, IsPractice; sem baseline → NoBaseline */ }
[Fact] public void Stint_mode_sets_flag_and_laps() { /* StintLaps 25, done 5, pct 0.5 → 19.5, IsStintMode */ }
[Fact] public void Extra_laps_are_added_in_auto() { }
[Fact] public void Caution_in_time_race_marks_estimate() { }
[Fact] public void Mean_falls_back_to_baseline_italic() { /* StintMeanL NaN → MeanL 2.373, MeanFromBaseline */ }
[Fact] public void Compute_allocates_nothing() { /* 10.000 Compute → 0 bytes */ }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar.** **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): fuel strategy engine with states and prioritized insights"`

---

### Task 13: Histerese, igualdade visível e `OverlayText`

**Files:**
- Modify: `src/FuelPace.Core/Strategy/SignalHysteresis.cs`, `src/FuelPace.Core/Strategy/OverlayState.cs` (adicionar `VisibleEquals`)
- Create: `src/FuelPace.Core/Strategy/OverlayText.cs`
- Test: `tests/FuelPace.Tests/Strategy/{SignalHysteresisTests,OverlayTextTests,OverlayStateTests}.cs`

**Interfaces:**
- Consumes: `SignalState`, `OverlayState`, `Insight` (Task 12)
- Produces:
  - `SignalHysteresis.Apply`: só segura a transição entre `Blue/Red/NotFeasible`; de ou para `Inactive`/`NoBaseline`/`NoTarget`, na primeira chamada, ou com `now` menor que o último → aplica `raw` na hora
  - `bool OverlayState.VisibleEquals(in OverlayState other)`: compara os valores como aparecem na tela (L a 1 casa, L/v a 2, voltas a 1, minutos inteiros, tempos a 2, NaN == NaN), mais estado, flags, contadores e insight (tipo + argumentos arredondados como no texto)
  - `static class OverlayText` (todos devolvem o nº de chars escritos e usam `TryFormat` com InvariantCulture; NaN → `—`): `Liters(double, Span<char>)` (`"77.6"`), `PerLap(double, Span<char>)` (`"2.38"`), `Laps(double, Span<char>)` (`"34.0"`), `Minutes(double, Span<char>)` (`"43"`), `Seconds(double, Span<char>)` (`"79.51"`), `Insight(in Insight, ReadOnlySpan<char> carShort, ReadOnlySpan<char> trackShort, Span<char> dest, out int boldLength)` (`boldLength` segue a decisão 9)

- [ ] **Step 1: Testes que falham**

```csharp
[Theory] // carShort "Ligier", trackShort "Zolder Alt"; último argumento = boldLength
[InlineData(InsightKind.StopRequired, 3.4, double.NaN, double.NaN, "Faltam 3.4 L · parada necessária", 12)]
[InlineData(InsightKind.BopMismatch, double.NaN, double.NaN, double.NaN, "Baseline com BoP diferente", 0)]
[InlineData(InsightKind.Save, 0.08, 3, double.NaN, "Economize 0.08 L/v (−3%)", 18)]
[InlineData(InsightKind.Save, 0.08, 3, 0.9, "Economize 0.08 L/v (−3%) · ≈ +0.9 s/v", 18)]
[InlineData(InsightKind.Slack, 1.2, double.NaN, double.NaN, "Folga 1.2 L · pode atacar", 11)]
[InlineData(InsightKind.OnTarget, 0.3, double.NaN, double.NaN, "No alvo · margem 0.3 L", 7)]
[InlineData(InsightKind.NoBaseline, double.NaN, double.NaN, double.NaN, "Sem baseline para Ligier · Zolder Alt", 0)]
[InlineData(InsightKind.ElectricUnsupported, double.NaN, double.NaN, double.NaN, "Carro elétrico: sem suporte", 0)]
[InlineData(InsightKind.None, double.NaN, double.NaN, double.NaN, "", 0)]
public void Insight_copy(InsightKind k, double a, double b, double c, string expected, int bold) { /* Span<char> buf = stackalloc char[128] */ }
[Fact] public void Numbers_format_invariant() { /* Liters(77.64)="77.6"; PerLap(2.384)="2.38"; PerLap(NaN)="—"; Laps(34)="34.0"; Minutes(43.4)="43"; Seconds(79.514)="79.51" */ }
[Fact] public void Formatting_allocates_nothing() { }
// Hysteresis
[Fact] public void Holds_previous_color_for_1_5s() { /* Blue@0; Red@1.0 → Blue; Red@2.4 → Blue; Red@2.5 → Red */ }
[Fact] public void Flicker_never_switches() { /* Red/Blue alternando a cada 0.5 s por 10 s → sempre Blue */ }
[Fact] public void Inactive_and_no_baseline_switch_immediately() { }
[Fact] public void Time_going_backwards_resets() { }
// VisibleEquals
[Fact] public void Equal_when_display_is_equal() { /* FuelL 77.61 vs 77.64; TargetL 2.384 vs 2.381 → true */ }
[Fact] public void Different_when_display_changes() { /* 77.6 vs 77.7; State Blue vs Red; RecValid 3 vs 4 → false */ }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar** (o sinal de menos do insight é `−`, U+2212; o separador é ` · `, com espaços). **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): signal hysteresis, visible-equality and overlay text formatting"`

---

### Task 14: `StintTracker`, `TelemetryPipeline`, `OverlayStatePublisher`, `TelemetryLoop`

**Files:**
- Create: `src/FuelPace.Core/Pipeline/{StintTracker,OverlayStatePublisher,TelemetryPipeline,TelemetryLoop}.cs`
- Test: `tests/FuelPace.Tests/Pipeline/{StintTrackerTests,TelemetryPipelineTests}.cs`

**Interfaces:**
- Consumes: tudo o que veio antes: `ITelemetrySource`, `VarLayout`, `SessionInfoParser`, `LapDetector`, `CaptureSession`, `LeaderPaceTracker`, `FuelProjection`, `FuelStrategyEngine`, `SignalHysteresis`, `ProfileStore`, `ProfileOperations`, `BackgroundWorkQueue`, `BopSignature`
- Produces:
  - `sealed class StintTracker` com `(int window)`, `void Update(in TelemetryFrame f)` (novo stint na saída do pit ou quando `FuelLevel` sobe > 0,05 L entre frames), `void OnLapCompleted(in CompletedLap lap)` (entra na média se `Reasons == None` e `EndTime > stintStart`), `double MeanFuelL`, `double MeanLapS` (NaN com < 2), `int LapsDone`
  - `sealed class OverlayStatePublisher` com `void Publish(in OverlayState s)` (incrementa `Version` só se `!VisibleEquals(último)`), `long Version`, `long PublishCount`, `OverlayState Read()` (cópia protegida por `System.Threading.Lock`)
  - `sealed class TelemetryPipeline : ILapSink` com `(StrategyConfig cfg, TimeProvider clock, ProfileStore? store, BackgroundWorkQueue? io)`, `void OnLayout(VarLayout layout)`, `void OnSessionContext(SessionInfo info, ProfileDocument? profile)` (thread-safe: troca referências `volatile` lidas no próximo tick), `void Tick(in TelemetryFrame f)`, `void Flush()`, `void RequestCaptureStart()`, `void RequestCaptureStop()` (thread-safe, aplicados no início do próximo tick), `event Action<CaptureRecord, string /*profileKey*/>? CaptureCompleted`, `OverlayStatePublisher Overlay`, `bool CaptureActive`
  - `void TelemetryPipeline.SetConfig(StrategyConfig cfg)` (thread-safe, troca `volatile`; um `MeanWindow` diferente recria o `StintTracker` e o `LeaderPaceTracker` no próximo tick)
  - `string CarShort`, `string TrackShort` (calculados em `OnSessionContext`, decisão 3) e o `OverlayStatePublisher` também guarda as duas strings, para o renderer formatar o insight
  - `interface IFrameTap { void OnFrame(in TelemetryFrame f); void OnSessionInfo(string yaml, int version, SessionInfo info); void OnSourceEnded(); }`: usado pelo gravador de telemetria do App; `OnFrame` precisa ser zero alocação
  - `sealed class TelemetryLoop` com `(ITelemetrySource source, TelemetryPipeline pipeline, ProfileStore? store, bool inlineSessionInfo, DiagLog? log = null, IFrameTap? tap = null)`. Mede cada `Step` com `Stopwatch.GetTimestamp()` num histograma fixo (buckets de 10 µs até 10 ms) e emite `PerfMinute` a cada 60 s de relógio de parede, `bool Step()` (um `TryRead`; quando `SessionInfoVersion` muda, faz parse + `store.Load`, inline ou numa `Thread` de fundo, e chama `OnSessionContext`; devolve `false` em `EndOfStream`, depois de `pipeline.Flush()`), `void RunToEnd()`

**Comportamento por tick (`Tick`):**
1. Aplicar pedidos de captura. No start: `CaptureSession.Start(f.SessionTime, info.ProfileKey, sessionType, condições do frame + tanque)`. No stop: `Stop(detector.TrySnapshotInProgress(out var lap), lap)`, depois `CaptureCompleted`; com `store`/`io`, enfileira `Load ?? CreateFor` → `AddCapture` → `Save` → `OnSessionContext` com o documento novo.
2. Se o `ProfileKey` do contexto mudou com a captura ligada → stop automático, salvando no key antigo.
3. `detector.Update(f)`; `stint.Update(f)`; `leader.Update(f)` (só se `layout.HasCarIdx`).
4. A cada 6º tick: montar `StrategyInput` (`Inactive = OnPitRoad || PlayerCarInPitStall || !IsOnTrack || IsReplayPlaying || SessionState ∉ {4,5}`; `LapProjectionL = CurrentLapHasPit ? NaN : FuelProjection.Project(...)` com o perfil da baseline; `Race` a partir de `info.FindSession(f.SessionNum)`; `LeaderFinished = SessionState == 5`; `IAmLeader = leader.Current.LeaderCarIdx == f.PlayerCarIdx` (com `PlayerCarIdx` ausente, usar `info.DriverCarIdx`); `BopMismatch` = a assinatura da sessão não bate com nenhuma das `baseline.Conditions`) → `Compute` → `Publish`.
5. `OnLapCompleted` distribui a volta para stint e captura e guarda `LastLapL`/`LastLapInvalid`/`LastLapTimeS`.

- [ ] **Step 1: Testes que falham** (`LapSim(...).SessionLaps(10)` → `IbtFileBuilder.WriteFrames` com `SampleYaml` de corrida por voltas → `TelemetryLoop(inline: true)`)

```csharp
[Fact] public void Strategy_runs_at_10hz() { /* 600 frames → Overlay.PublishCount == 100 */ }
[Fact] public void End_to_end_laps_race_shows_last_mean_and_state()
{ // baseline montada em memória (Fixture6) via OnSessionContext; 4 voltas de 2.4 L → LastLapL 2.4, MeanL 2.4 (stint), !MeanFromBaseline, Race Laps,
  // LapsRemaining == 10 − LapCompleted − pct + 1 (±0.01), State Blue (100 L sobra)
}
[Fact] public void Version_does_not_change_when_nothing_visible_changes()   // PERF-06 (parte do Core)
{ // repetir o mesmo frame 600×: Version estável depois do 1º publish; PublishCount 100
}
[Fact] public void Capture_start_stop_persists_profile_with_baseline()
{ // store em diretório temporário + BackgroundWorkQueue; RequestCaptureStart antes do 1º frame; 6 voltas; RequestCaptureStop; io.WaitIdle
  // → arquivo existe, 1 captura, ≥ 5 voltas válidas, baseline != null; Overlay.Read().Recording só enquanto ativa; RecValid/RecTotal batem
}
[Fact] public void Session_change_while_capturing_auto_stops_into_old_profile()   // Review Focus 1
{ // captura ligada; OnSessionContext com outro CarPath → CaptureActive false; CaptureCompleted com o key antigo; nenhuma volta nova no perfil antigo
}
[Fact] public void Pit_road_makes_overlay_inactive() { }
[Fact] public void Stint_mean_resets_after_pit_exit() { /* StintTrackerTests: 3 voltas, pit, 1 volta → MeanFuelL NaN, LapsDone 1 */ }
[Fact] public void Time_race_without_caridx_uses_own_pace() { /* LapSim.TimeRace(1800), includeCarIdx:false → LapsRemaining == ByTime(T, pct, NaN, pct, myPace) */ }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar.** **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): telemetry pipeline, stint tracking, overlay publisher and loop"`

---

### Task 15: Zero alocação por tick (PERF-03) e benchmark

**Files:**
- Test: `tests/FuelPace.Tests/Perf/HotPathAllocationTests.cs`
- Create: `bench/FuelPace.Bench/FuelPace.Bench.csproj`, `bench/FuelPace.Bench/Program.cs`, `bench/FuelPace.Bench/PipelineTickBench.cs`
- Create: `tests/FuelPace.Tests/Support/SyntheticRace.cs` (monta uma `.ibt` sintética com LapSim + CarIdx de 20 carros; o projeto Bench linka este arquivo via `<Compile Include>` junto com `LapSim.cs`, `IbtFileBuilder.cs` e `SampleYaml.cs`)

**Interfaces:**
- Consumes: `TelemetryLoop`, `TelemetryPipeline` (Task 14)
- Produces: `SyntheticRace.WriteIbt(string path, int laps, bool withCarIdx) → string path`

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Pipeline_allocates_zero_bytes_per_tick_synthetic()   // PERF-03, roda no CI
{ // SyntheticRace (12 voltas, CarIdx, corrida por tempo); TelemetryLoop inline, captura desligada, com baseline;
  // 1000 Step() de aquecimento; mede GC.GetAllocatedBytesForCurrentThread() em 10.000 Step() (passando por ≥ 2 viradas de volta) → 0
}
[Fact] public void Pipeline_allocates_zero_bytes_per_tick_fixture() { /* RequireZolder; mesmo método */ }
[Fact] public void No_gen0_collections_during_replay() { /* GC.CollectionCount(0) igual antes e depois dos 10.000 passos */ }
[Fact] public void Zero_bytes_with_debug_logging_and_frame_tap() { /* DiagLog em Trace (drenado por outra thread) + IFrameTap que copia o frame para um ring pré-alocado → 0 bytes na thread medida */ }
```

- [ ] **Step 2: Rodar e ver falhar (ou passar).** Se passar logo de cara, conferir que o loop medido realmente cruza viradas de volta e roda a estratégia (`PublishCount` > 0 no fim). Se falhar, achar a alocação com `dotnet-counters`/`dotnet-trace` ou bisseção (medir cada componente isolado) e corrigir no componente.

- [ ] **Step 3: Criar o projeto Bench**

```bash
dotnet new console -n FuelPace.Bench -o bench/FuelPace.Bench -f net10.0
dotnet sln add bench/FuelPace.Bench
dotnet add bench/FuelPace.Bench reference src/FuelPace.Core
dotnet add bench/FuelPace.Bench package BenchmarkDotNet
```
`PipelineTickBench` com `[MemoryDiagnoser]`: `[GlobalSetup]` escreve a `.ibt` sintética num diretório temporário; `[Benchmark] OneLap()` dá 4.800 `Step()` (uma volta a 60 Hz), reabrindo a fonte quando chega ao fim. `Program` chama `BenchmarkSwitcher.FromAssembly(...).Run(args)`.

- [ ] **Step 4: Rodar**

Run: `dotnet test tests/FuelPace.Tests --filter "FullyQualifiedName~HotPathAllocationTests"` → PASS
Run: `dotnet run -c Release --project bench/FuelPace.Bench -- --filter '*PipelineTickBench*' --job short`
Expected: coluna `Allocated` = `-` (0 B); anotar a média por volta ÷ 4.800 (µs por tick) em `docs/perf/core-bench.md`. Meta de referência: bem abaixo de 1 ms por tick (PERF-02).

- [ ] **Step 5: Commit** — `git commit -m "test(perf): zero-allocation hot path test and per-tick benchmark"`

---

### Task 16: CLI de replay `FuelPace.Replay`

**Files:**
- Create: `tools/FuelPace.Replay/FuelPace.Replay.csproj`, `tools/FuelPace.Replay/Program.cs`
- Test: `tests/FuelPace.Tests/Tools/ReplayCliTests.cs` (o projeto de testes referencia `tools/FuelPace.Replay`)

**Interfaces:**
- Consumes: `TelemetryLoop`, `TelemetryPipeline`, `ProfileStore`, `OverlayText`
- Produces: `static int ReplayCli.Run(string[] args, TextWriter output)`. Uso: `<arquivo.ibt> [--speed max|1|10] [--capture] [--profiles <dir>]`. Imprime: carro/pista/sessão; uma linha por volta concluída (`lap 16  80.897 s  2.382 L  ✓` ou `✗ incident,offtrack`); uma vez por volta, as 4 linhas do overlay (§7.1) com `OverlayText`; no fim, com `--capture`, o resumo da baseline. Saída 0 em sucesso, 2 com argumento inválido ou arquivo inexistente.

- [ ] **Step 1: Teste que falha**

```csharp
[Fact] public void Prints_laps_and_summary_for_synthetic_file()
{ // SyntheticRace (5 voltas, sem CarIdx) → Run([path, "--capture", "--profiles", tmp], sw) == 0
  // saída contém "lap 2", "✓", "ALVO", "Baseline", e um .json foi criado em tmp/profiles
}
[Fact] public void Missing_file_returns_2() { }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar** (`Program.Main` define `Console.OutputEncoding = Encoding.UTF8`, porque o console do Windows não usa UTF-8 por padrão e `✓ — ·` sairiam corrompidos, e depois chama `ReplayCli.Run(args, Console.Out)`). **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Smoke manual com dados reais.** Rodar com dois dos `.ibt` locais (`~/Documentos/dev/github/telemeter/telemetry_data/`): um Dallara P217 de Road Atlanta e o BMW M8 GTE de Interlagos.
Run: `dotnet run --project tools/FuelPace.Replay -- "<arquivo>" --capture --profiles "$TMPDIR/fp"`
Expected: sem exceção; voltas listadas com consumo plausível (> 0, constante entre voltas limpas); voltas de pit marcadas `pit`. Anotar qualquer anomalia em `docs/perf/core-bench.md` › "Smoke com .ibt reais".

- [ ] **Step 6: Commit e verificação final**

```bash
dotnet build -c Release && dotnet test -c Release
git add -A && git commit -m "feat(tools): replay CLI for .ibt files"
```
Expected: build sem warnings (`TreatWarningsAsErrors`); todos os testes passam, com os da fixture ignorados se ela não estiver presente.

Também conferir o publish cruzado a partir do Linux:
Run: `dotnet publish tools/FuelPace.Replay -c Release -r win-x64 --self-contained false -o "$TMPDIR/replay-win"`
Expected: gera `FuelPace.Replay.exe`. Rodar esse exe no Windows contra um `.ibt` é o smoke manual da plataforma-alvo.

---

### Task 17: Lógica da memória compartilhada do SDK (portável, sem handles do SO)

Objetivo: deixar no Core tudo o que dá para testar do leitor ao vivo (Fase E). No Agent sobram só `MemoryMappedFile.OpenExisting` e `EventWaitHandle.OpenExisting` (`Local\IRSDKMemMapFileName` / `Local\IRSDKDataValidEvent`), que só existem no Windows: no Linux, os dois lançam `PlatformNotSupportedException`.

**Files:**
- Create: `src/FuelPace.Core/Telemetry/LiveSnapshotReader.cs`
- Modify: `tests/FuelPace.Tests/Support/IbtFileBuilder.cs` (adicionar `BuildLiveImage`)
- Test: `tests/FuelPace.Tests/Telemetry/LiveSnapshotReaderTests.cs`

**Interfaces:**
- Consumes: `IrsdkHeader`, `VarHeader`, `VarLayout`, `TelemetryFrameReader` (Tasks 1–2)
- Produces:
  - `enum LiveReadStatus { Frame, NotConnected, NoNewData, TornRead, LayoutChanged }`
  - `sealed class LiveSnapshotReader` com `(int maxBufLen = 64 * 1024)`, `LiveReadStatus TryRead(ReadOnlySpan<byte> memory, ref TelemetryFrame frame)`, `VarLayout? Layout`, `int SessionInfoUpdate`, `string ReadSessionInfoYaml(ReadOnlySpan<byte> memory)`. O Agent passa um `ReadOnlySpan<byte>` montado com `new ReadOnlySpan<byte>(ptr, size)` sobre a view (`AcquirePointer`); os testes passam um `byte[]`.
  - Teste: `IbtFileBuilder.BuildLiveImage(string yaml, IReadOnlyList<VarSpec> vars, int numBuf, (int tickCount, double[] values)[] buffers, int status = 1, int sessionInfoUpdate = 1) → byte[]` (mesmo cabeçalho e var headers, sem sub-cabeçalho de disco)

Regras: `status & 1 == 0` → `NotConnected`. Na 1ª leitura, ou quando `NumVars`/`VarHeaderOffset`/`BufLen` mudam, refazer `VarLayout.Resolve` (alocação permitida, só na conexão) e devolver `LayoutChanged` uma vez. Escolher o `varBuf` de maior `tickCount`. Se for igual ao último lido → `NoNewData`. Copiar a linha para um buffer pré-alocado, reler o `tickCount` daquele `varBuf`: se mudou → `TornRead` (descarta). Senão, `TelemetryFrameReader.Read` → `Frame`.

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Picks_buffer_with_highest_tick() { /* 3 buffers com ticks 10, 12, 11 → frame do buffer do tick 12 */ }
[Fact] public void Same_tick_twice_is_no_new_data() { }
[Fact] public void Disconnected_status_is_reported() { /* status 0 → NotConnected */ }
[Fact] public void Torn_read_is_discarded() { /* hook de teste: o reader expõe `Action? AfterCopyForTests`, que altera o tickCount na imagem → TornRead */ }
[Fact] public void Layout_change_is_detected_on_reconnect() { /* 2ª imagem com outra lista de vars → LayoutChanged e depois Frame com os offsets novos */ }
[Fact] public void Session_info_update_counter_is_exposed() { /* sessionInfoUpdate 1 → 2: ReadSessionInfoYaml devolve o YAML novo */ }
[Fact] public void TryRead_allocates_nothing_after_connect() { /* 10.000 leituras alternando ticks → 0 bytes */ }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar.** **Step 4: Rodar e ver passar.**

- [ ] **Step 5: Commit** — `git commit -m "feat(core): portable shared-memory snapshot logic for the live SDK reader"`
