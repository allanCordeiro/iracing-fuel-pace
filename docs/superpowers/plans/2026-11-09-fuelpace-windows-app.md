# FuelPace App (Fases E–H) — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar o app Windows completo (Agent com overlay, Settings, IPC, modo debug com logs e gravação de telemetria), todo escrito e testado no Linux tanto quanto possível, compilado, testado e empacotado no job Windows do GitHub Actions, para o piloto testar no jogo e devolver os logs.

**Architecture:** Toda a lógica de app fica em `FuelPace.App` (`net10.0`, portável, testada no Linux): orquestração do Agent (`AgentHost`), layout do overlay em `DrawList`, posicionamento, settings, IPC por named pipe, sink de log, gravador de telemetria e pacote de diagnóstico. Os projetos Windows são finos: `FuelPace.Agent` (Win32 + DirectComposition/Direct2D via Vortice + CsWin32) e `FuelPace.Settings` (WPF, com views sobre ViewModels portáveis). Eles implementam as interfaces de plataforma do `FuelPace.App` e só são testados no CI Windows e pelo piloto.

**Tech Stack:** .NET 10, Vortice.Windows (Direct2D1, DirectWrite, DirectComposition, DXGI, Direct3D11), Microsoft.Windows.CsWin32, WPF, CommunityToolkit.Mvvm, System.IO.Pipes, xUnit v3, GitHub Actions (`ubuntu-latest` + `windows-latest`).

**Spec:** `docs/PRD.md` (v2.1) + mocks aprovados (https://claude.ai/artifact/Q6er7raafCpiD56Z1LC96Y; geometria transcrita no Task 6). **Pré-requisito:** o plano `2026-11-09-fuelpace-core.md` concluído.

## Global Constraints

- Vale tudo o que está nas Global Constraints do plano do Core (alvo Windows, zero alocação no hot path, InvariantCulture, padrões, persistência atômica, sem rede, commits com `Co-Authored-By`).
- **Desenvolvimento no Linux:** `Directory.Build.props` ganha `<EnableWindowsTargeting Condition="'$(OS)' != 'Windows_NT'">true</EnableWindowsTargeting>`. Todos os projetos, inclusive `net10.0-windows`, precisam compilar no Linux sem warnings. Testes portáveis rodam no Linux; os de `tests/FuelPace.Windows.Tests` só rodam no CI Windows.
- No Linux, os testes rodam via `FuelPace.Linux.slnf` (`dotnet test FuelPace.Linux.slnf`). O CI Windows roda a solução inteira.
- `FuelPace.Agent` e `FuelPace.Settings` não têm lógica de decisão. Se dá para testar sem Win32/WPF, o código vai para `FuelPace.App`.
- O Agent nunca rouba foco nem captura input em modo travado (NFR-02); nunca chama `timeBeginPeriod`; roda com prioridade `BelowNormal`.
- Redesenho do overlay só quando `OverlayStatePublisher.Version` ou as opções de visualização mudam, no máximo 10/s (PERF-06). Telemetria → UI por um delegate em cache (sem closure por mensagem).
- **Um único escritor de disco:** só o Agent grava perfis e `settings.json`. O Settings pede tudo por IPC.
- Textos para o piloto em português, iguais aos do PRD e dos mocks.
- **Versão:** `<Version>0.1.0</Version>` em `Directory.Build.props`; o CI passa `-p:InformationalVersion=0.1.0+<run_number>.<sha7>` (local: `0.1.0+local`). A versão aparece no log de startup, no tooltip da bandeja e no Settings › Diagnóstico.

## Decisões

1. **"Versão debug" = os mesmos binários Release + modo debug ligado.** O comportamento no jogo é o mesmo da versão normal; só muda o que é registrado. O modo debug liga se existir o arquivo `FuelPace.debug` ao lado do exe, se o Agent for iniciado com `--debug`, ou pelo toggle no Settings › Diagnóstico (persistido). O CI publica dois zips: `FuelPace-<ver>-win-x64.zip` e `FuelPace-<ver>-win-x64-debug.zip` (o mesmo conteúdo + `FuelPace.debug` + `FuelPace.FakeSim.exe`). PDBs embutidos nos dois (`<DebugType>embedded</DebugType>`), para os stack traces terem número de linha.
2. **O modo debug faz:** `DiagLog` em `Trace` (o normal é `Info`); grava a telemetria completa (todas as variáveis do `TelemetryFrame`, inclusive `CarIdx*`, a 60 Hz) em `.ibt` que o replay do Linux lê (R5 do PRD); habilita o atalho de **marcador** `Ctrl+Shift+M` (o piloto aperta quando vê algo errado; o log ganha `UserMark` com hora e `SessionTime`); mostra uma pílula `DBG` ao lado do REC e `FuelPace (debug)` no tooltip; adiciona ao menu da bandeja *Inserir marcador*, *Gerar pacote de diagnóstico* e *Abrir pasta de logs*.
3. **Logs:** JSON Lines em `%APPDATA%\FuelPace\logs\fuelpace-<yyyyMMdd>-<n>.jsonl`, rotação a cada 20 MB, até 200 MB no total. Fora do modo debug, o nível `Info` atende a NFR-06 (`PerfMinute` a cada minuto, voltas, capturas, erros).
4. **Gravações:** `%APPDATA%\FuelPace\recordings\<yyyyMMdd-HHmmss>_<carPath>_<trackName>.ibt`, um arquivo por contexto de sessão (muda com o perfil ou com o `SessionNum`), até `MaxRecordingsGB` (padrão 2 GB; apaga as mais antigas). Cerca de 450 MB/h com 64 carros.
5. **Pacote de diagnóstico:** zip em `Desktop\FuelPace-diag-<yyyyMMdd-HHmmss>.zip` com logs, `settings.json`, perfis, as 2 gravações mais recentes (opcional), `system-info.txt` e `manifest.json`. É isso que o piloto me traz.
6. **Distribuição:** self-contained `win-x64`, ReadyToRun, sem single-file. Agent, Settings, Replay e FakeSim ficam na mesma pasta; basta descompactar e rodar `FuelPace.Agent.exe`, sem instalar runtime.
7. **Overlay:** DirectComposition + Direct2D é o backend principal. **Fallback automático** para janela layered (`UpdateLayeredWindow` com bitmap D2D/WIC) se a inicialização do DComp falhar; ele também pode ser forçado em `settings.json` (`overlay.renderBackend: "auto" | "composition" | "layered"`). O backend escolhido e qualquer HRESULT vão para o log. A ≤ 10 redesenhos/s, a cópia pela CPU do layered (300×110 px) custa pouco.
8. **Fontes:** JetBrains Mono e Inter (OFL), TTF em `assets/fonts/` (commitados, com `OFL.txt`), carregadas pelo DirectWrite por um loader em memória, sem instalar nada no sistema.
9. **CI:** o job Linux roda em todo push e PR. O job Windows roda em push na `main`, PRs, tags `v*` e `workflow_dispatch`, com `concurrency` cancelando a execução anterior do mesmo ref. Tags `v*` criam um GitHub Release com os dois zips; nos outros casos, os zips ficam como artifacts do workflow (retenção de 14 dias). O repositório é privado: minutos de Windows contam em dobro na cota do Actions.

## Review Focus

1. **Overlay invisível na máquina do piloto** (DComp + `WS_EX_LAYERED|WS_EX_TRANSPARENT` não compõe; iRacing em tela cheia exclusiva). Esperado: fallback automático para layered, detecção de tela cheia exclusiva com `SHQueryUserNotificationState` → aviso na bandeja e no Settings, tudo no log. Testes no Task 11 (seleção de backend com fábrica falsa) e no Task 9 (decisão de aviso).
2. **Atalho já registrado por outro programa.** Esperado: o Agent segue funcionando, registra `HotkeyRegistered ok=0`, avisa uma vez na bandeja e o Settings mostra o atalho em vermelho. Teste no Task 9 (com `IHotkeyService` falso).
3. **Agent aberto antes do iRacing, iRacing fechado ou reaberto, iRacing em replay** (critério 7). Esperado: reconexão sem intervenção, overlay INATIVO/oculto enquanto não há dados. Teste no Task 9 (fábrica de fontes falsa) e no Task 13 (FakeSim real no CI Windows).
4. **Duas instâncias do Agent** (autostart + clique manual). Esperado: a segunda detecta a primeira (mutex), pede por IPC que ela abra o Settings e sai com código 0. Teste no Task 9 (`ISingleInstance` falso) e no Task 13 (mutex real).
5. **Mudança de monitores** (monitor desconectado, troca de resolução ou de DPI com o Agent aberto). Esperado: o overlay volta para dentro da área visível de um monitor existente, na escala certa (`WM_DPICHANGED`, `WM_DISPLAYCHANGE`). Teste no Task 7 (matemática de posicionamento).

---

## Estrutura de arquivos

```
src/FuelPace.App/                                (net10.0, portável)
  Settings/AppSettings.cs, SettingsStore.cs, Hotkey.cs
  Diagnostics/AppDiagEvents.cs, DiagFileSink.cs, CrashHandler.cs, DiagnosticBundle.cs, SystemInfoKeys.cs
  Recording/IbtStreamWriter.cs, TelemetryRecorder.cs, IbtTrimmer.cs
  Live/IrsdkLiveImageWriter.cs
  Simulation/LapSim.cs, SyntheticRace.cs           (movidos de tests/Support)
  Overlay/DrawList.cs, OverlayTheme.cs, OverlayLayout.cs, ITextMeasurer.cs, FixedMetricsMeasurer.cs, SvgRenderer.cs
  Placement/MonitorDesc.cs, PixelRect.cs, OverlayPlacement.cs
  Ipc/IpcMessages.cs, IpcJsonContext.cs, IpcServer.cs, IpcClient.cs
  Host/Platform.cs (interfaces), AgentHost.cs, RenderScheduler.cs, AutoHidePolicy.cs
  SettingsUi/ViewModels/*.cs, ReasonLabels.cs, LapTimeFormat.cs
src/FuelPace.Agent/                               (net10.0-windows, WinExe)
  Program.cs, app.manifest, NativeMethods.txt (CsWin32)
  Win32/MessageWindow.cs, SingleInstance.cs, Autostart.cs, SystemInfoCollector.cs, FullscreenDetector.cs
  Live/IRacingMemoryReader.cs, LiveSourceFactory.cs
  Overlay/OverlayWindow.cs, CompositionRenderer.cs, LayeredRenderer.cs, D2DDrawListRenderer.cs, DWriteFonts.cs, DWriteTextMeasurer.cs
  Tray/TrayIcon.cs, Hotkeys.cs
src/FuelPace.Settings/                            (net10.0-windows, WPF)
  App.xaml, MainWindow.xaml, Views/{CapturePage,OverlayPage,ProfilesPage,DiagnosticsPage}.xaml, WpfDrawListRenderer.cs, Theme.xaml
tools/FuelPace.FakeSim/                           (net10.0-windows) simulador de memória compartilhada do iRacing
tools/FuelPace.Preview/                           (net10.0) gera docs/overlay-preview/*.svg
tools/FuelPace.Replay/                            (+ subcomandos trim e diag)
tests/FuelPace.App.Tests/                         (net10.0)
tests/FuelPace.Windows.Tests/                     (net10.0-windows) só no CI Windows
assets/fonts/, assets/icon.ico
build/package.ps1
FuelPace.Linux.slnf
docs/windows-testing.md, docs/feedback-loop.md
```

---

### Task 1: Projetos Windows compilando no Linux + CI Windows + empacotamento

**Files:**
- Modify: `Directory.Build.props` (`EnableWindowsTargeting`, `Version`, `DebugType embedded`), `.github/workflows/ci.yml`, `.gitignore` (`feedback/`, `out/`)
- Create: `src/FuelPace.App/FuelPace.App.csproj`, `src/FuelPace.Agent/{FuelPace.Agent.csproj,Program.cs,app.manifest,NativeMethods.txt}`, `src/FuelPace.Settings/{FuelPace.Settings.csproj,App.xaml,App.xaml.cs,MainWindow.xaml,MainWindow.xaml.cs}`, `tools/FuelPace.FakeSim/{FuelPace.FakeSim.csproj,Program.cs}`, `tests/FuelPace.App.Tests/FuelPace.App.Tests.csproj`, `tests/FuelPace.Windows.Tests/FuelPace.Windows.Tests.csproj`, `FuelPace.Linux.slnf`, `build/package.ps1`, `src/FuelPace.App/AppInfo.cs`
- Test: `tests/FuelPace.App.Tests/AppInfoTests.cs`, `tests/FuelPace.Windows.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: `static class AppInfo { string Version; string ExeDirectory; bool DebugMarkerPresent /* FuelPace.debug ao lado do exe */; }`; `build/package.ps1 -Version <v> -OutDir out` gera `out/FuelPace-<v>-win-x64.zip` e `out/FuelPace-<v>-win-x64-debug.zip`.

- [ ] **Step 1: Criar os projetos.** Agent: `<OutputType>WinExe</OutputType>`, `<TargetFramework>net10.0-windows</TargetFramework>`, `<ApplicationManifest>app.manifest</ApplicationManifest>` (PerMonitorV2, Windows 10+), `<ServerGarbageCollection>false</ServerGarbageCollection>`, `<TieredPGO>true</TieredPGO>`, pacotes `Vortice.Direct2D1`, `Vortice.DirectComposition`, `Vortice.Direct3D11`, `Vortice.DXGI`, `Microsoft.Windows.CsWin32` (PrivateAssets all), referências a Core e App. Settings: `<UseWPF>true</UseWPF>`, `CommunityToolkit.Mvvm`, referências a Core e App. FakeSim: console `net10.0-windows`. `Program.cs` do Agent por ora só escreve a versão num log e sai.
- [ ] **Step 2: Teste portável**

```csharp
[Fact] public void Version_is_informational_version() { Assert.Matches(@"^0\.1\.0\+", AppInfo.Version); }
[Fact] public void Debug_marker_detection_uses_exe_directory() { /* cria FuelPace.debug num diretório temporário passado por um overload interno → true */ }
```

- [ ] **Step 3: Compilar tudo no Linux**

Run: `dotnet build FuelPace.sln -c Release`
Expected: sucesso, 0 warnings (Agent, Settings e FakeSim compilam via `EnableWindowsTargeting`). Se o WPF não compilar no Linux, registrar em `docs/perf/core-bench.md` › "Limitações", tirar o Settings do `.slnf` e deixar só o CI Windows compilá-lo. Não contornar de outro jeito.

- [ ] **Step 4: CI.** `ci.yml` com dois jobs:
  - `linux`: `dotnet test FuelPace.Linux.slnf -c Release`.
  - `windows`: `dotnet test FuelPace.sln -c Release -p:InformationalVersion=…`, `pwsh build/package.ps1`, `actions/upload-artifact@v4` com os dois zips, `tests/**/TestResults/**` e `artifacts/**` (renders, logs do smoke). Em tags `v*`: `gh release create $tag out/*.zip --generate-notes`.
  
  `package.ps1` publica Agent, Settings, Replay e FakeSim com `-r win-x64 --self-contained true -p:PublishReadyToRun=true` na mesma pasta, copia `assets/fonts/OFL.txt` e `docs/windows-testing.md` (como `LEIA-ME.md`), zipa; para o zip debug, acrescenta o arquivo vazio `FuelPace.debug`.
- [ ] **Step 5: `SmokeTests.Agent_starts_and_exits`** (Windows): roda `FuelPace.Agent.exe --exit-after 2` → código 0.
- [ ] **Step 6: Verificar o CI.** `git push` numa branch → `gh run watch` → os dois jobs verdes; `gh run download <id> -n <artifact>` baixa os zips. Expected: os zips contêm `FuelPace.Agent.exe`, `FuelPace.Settings.exe`, e o debug também `FuelPace.debug`.
- [ ] **Step 7: Commit** — `git commit -m "build: windows projects, cross-OS CI and packaging"`

---

### Task 2: `AppSettings`, `SettingsStore` e `Hotkey`

**Files:**
- Create: `src/FuelPace.App/Settings/{AppSettings,SettingsStore,Hotkey}.cs`
- Test: `tests/FuelPace.App.Tests/Settings/{SettingsStoreTests,HotkeyTests}.cs`

**Interfaces:**
- Consumes: `StrategyConfig` (Core Task 12), `AtomicFile` (Core Task 8)
- Produces:
  - `enum RenderBackend { Auto, Composition, Layered }`
  - `sealed record OverlaySettings { string? MonitorDeviceName; int OffsetX = 40; int OffsetY = 40 /* px físicos relativos ao canto da área de trabalho do monitor */; double Scale = 1.0 /* 0.5–2.5 */; double BackgroundOpacity = 0.6 /* 0–1 */; double ContentOpacity = 1.0 /* 0.3–1 */; bool ShowPaceLine = false; bool ShowInsightLine = true; bool AutoHide = true; RenderBackend RenderBackend = Auto; }`
  - `sealed record HotkeySettings { string ToggleEdit = "Ctrl+Shift+F"; string ToggleVisible = "Ctrl+Shift+H"; string DebugMark = "Ctrl+Shift+M"; }`
  - `sealed record DebugSettings { bool Enabled = false; bool RecordTelemetry = true; double MaxRecordingsGB = 2; }`
  - `sealed record AppSettings { int SchemaVersion = 1; OverlaySettings Overlay; StrategyConfig Strategy; HotkeySettings Hotkeys; DebugSettings Debug; bool StartWithWindows = false; AppSettings Normalize(); }`: `Normalize` faz clamp de cada faixa e troca hotkeys inválidas pelo padrão
  - `sealed class SettingsStore` com `(string rootDir)`, `AppSettings Load()` (ausente → padrão; corrompido → `.corrupt` + padrão), `void Save(AppSettings s)`
  - `readonly record struct Hotkey(HotkeyModifiers Modifiers, ushort VirtualKey)` com `[Flags] enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }` (valores iguais aos `MOD_*` do Win32), `static bool TryParse(string text, out Hotkey key)` (`Ctrl+Shift+F`, `Alt+F9`, `Ctrl+Shift+1`; teclas A–Z, 0–9, F1–F24; exige ≥ 1 modificador), `string ToString()` (forma canônica `Ctrl+Shift+F`)

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Missing_file_gives_defaults() { /* Scale 1.0, BackgroundOpacity 0.6, ContentOpacity 1.0, ShowInsightLine, AutoHide, hotkeys padrão, Strategy.ReserveL 0.5 */ }
[Fact] public void Round_trip_preserves_values() { }
[Fact] public void Normalize_clamps_ranges() { /* Scale 3 → 2.5; 0.1 → 0.5; ContentOpacity 0.1 → 0.3; BackgroundOpacity −1 → 0 */ }
[Fact] public void Invalid_hotkey_falls_back_to_default() { }
[Fact] public void Corrupt_settings_are_quarantined() { }
[Theory] [InlineData("Ctrl+Shift+F", 6, 0x46)] [InlineData("alt+f9", 1, 0x78)] [InlineData("Ctrl+Shift+1", 6, 0x31)]
public void Parses_hotkeys(string s, int mods, int vk) { }
[Theory] [InlineData("F")] [InlineData("Ctrl+")] [InlineData("Ctrl+Shift+Enter2")] public void Rejects_invalid(string s) { }
[Fact] public void Json_uses_camel_case_and_string_enums() { /* "renderBackend": "auto", "mode": "auto" */ }
```

- [ ] **Step 2: Rodar e ver falhar.** **Step 3: Implementar** (STJ com source generator `AppSettingsJsonContext`). **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `git commit -m "feat(app): settings model, store and hotkey parsing"`

---

### Task 3: Sink de log, crash handler e eventos do App

**Files:**
- Create: `src/FuelPace.App/Diagnostics/{AppDiagEvents,DiagFileSink,CrashHandler}.cs`
- Test: `tests/FuelPace.App.Tests/Diagnostics/DiagFileSinkTests.cs`

**Interfaces:**
- Consumes: `DiagLog`, `DiagRecord`, `DiagText`, `DiagEventCatalog`, `DiagJsonFormatter` (Core Task 1b)
- Produces:
  - `static class AppDiagEvents` com constantes `DiagEvent` a partir de 1000 e `static void RegisterAll()`: `Startup`, `Shutdown`, `OverlayBackend` (I0 backend, I1 hresult), `OverlayRedraws` (I0 por minuto), `DeviceLost` (I1 hresult), `WindowPlaced` (A x, B y, C scale, D dpiScale), `MonitorsChanged` (I0 count), `HotkeyRegistered` (I0 id, I1 ok), `HotkeyPressed` (I0 id), `TrayCommand` (I0), `EditMode` (I0 on), `Visibility` (I0 visible, I1 reason: 0 usuário, 1 sim fora, 2 fora da pista), `IpcClient` (I0 connected), `IpcMessage` (Debug; `Text` com o tipo), `SimWindow` (`Text` com estilos e retângulo da janela do iRacing), `FullscreenExclusive` (I0 detected), `TopmostReasserted` (Trace, I0 cause: 0 foreground, 1 timer), `SettingsSaved`, `RecordingStarted`/`RecordingStopped` (I0 frames, I1 dropped), `BundleCreated` (`Text` com o caminho), `UserMark` já existe no Core
  - `sealed class DiagFileSink : IDisposable` com `(DiagLog log, string logsDir, long maxFileBytes = 20 << 20, long maxTotalBytes = 200 << 20)`: thread de fundo (`BelowNormal`) drena a cada 250 ms e escreve JSONL; `void FlushNow()`; rotação e limpeza por tamanho total
  - `static class CrashHandler { static void Install(DiagLog log, DiagFileSink sink); }`: `AppDomain.UnhandledException` e `TaskScheduler.UnobservedTaskException` → `log.Exception("fatal", ex)` + `sink.FlushNow()`

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Writes_one_json_object_per_line() { /* 3 Emit + 1 Text → arquivo com 4 linhas que fazem JsonDocument.Parse */ }
[Fact] public void Rotates_at_max_file_size() { /* maxFileBytes 2 KB, 200 eventos → ≥ 2 arquivos fuelpace-<data>-1.jsonl, -2.jsonl */ }
[Fact] public void Deletes_oldest_beyond_total_cap() { }
[Fact] public void Dispose_flushes_pending_events() { }
[Fact] public void App_events_have_catalog_entries() { /* todo valor de AppDiagEvents tem DiagEventCatalog.Get não nulo */ }
[Fact] public void Dropped_count_is_reported_in_file() { /* ring cheio → linha {"ev":"DiagDropped","count":n} */ }
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Commit** — `git commit -m "feat(app): JSONL diagnostic sink with rotation and crash handler"`

---

### Task 4: Gravador de telemetria em `.ibt` + recorte de gravações

**Files:**
- Create: `src/FuelPace.App/Recording/{IbtStreamWriter,TelemetryRecorder,IbtTrimmer}.cs`
- Move: `tests/FuelPace.Tests/Support/{LapSim,SyntheticRace}.cs` → `src/FuelPace.App/Simulation/` (públicos; os testes do Core passam a referenciar `FuelPace.App`; o Bench linka os novos caminhos)
- Modify: `tools/FuelPace.Replay` (subcomando `trim`)
- Test: `tests/FuelPace.App.Tests/Recording/{TelemetryRecorderTests,IbtTrimmerTests}.cs`

**Interfaces:**
- Consumes: `IFrameTap`, `TelemetryVars`, `TelemetryFrame`, `IrsdkHeader`, `IbtReader`, `IbtTelemetrySource` (Core)
- Produces:
  - `sealed class IbtStreamWriter : IDisposable` com `(string path, int tickRate, int reservedYamlBytes = 1 << 20)`, `void WriteSessionInfo(string yaml)` (reescreve a região reservada; YAML maior que a região → grava `<path>.yaml` ao lado e loga `Warn`), `void Append(in TelemetryFrame f)`, `void Checkpoint()` (atualiza `RecordCount` e `sessionEndTime` no cabeçalho), `Dispose` → `Checkpoint` final. Escreve todas as vars de `TelemetryVars.All`.
  - `sealed class TelemetryRecorder : IFrameTap, IDisposable` com `(string recordingsDir, DiagLog log, double maxTotalGB, int ringCapacity = 1024)`: `OnFrame` copia o frame para um ring pré-alocado (zero alocação; ring cheio → descarta e conta); uma thread de fundo escreve, faz `Checkpoint` a cada 5 s e rotaciona o arquivo quando `OnSessionInfo` traz outro `ProfileKey` ou outro `SessionNum` do frame; `OnSourceEnded` fecha o arquivo; limpeza por tamanho total
  - `static class IbtTrimmer { static void Trim(string input, string output, double fromSessionTime, double toSessionTime); }`
  - CLI: `FuelPace.Replay trim <in.ibt> --from <st> --to <st> --out <file>`

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Recording_round_trips_through_replay_source() { /* SyntheticRace 3 voltas com CarIdx → recorder → IbtTelemetrySource: mesmos frames (campo a campo) e mesmo YAML */ }
[Fact] public void Crash_without_dispose_is_still_readable() { /* sem Dispose, só Checkpoint → IbtReader lê todos os registros do último checkpoint em diante (fallback por tamanho do Core Task 1) */ }
[Fact] public void Rotates_on_profile_change() { /* OnSessionInfo com outro carro → 2 arquivos, cada um com o seu YAML */ }
[Fact] public void OnFrame_allocates_nothing() { }
[Fact] public void Full_ring_drops_and_counts() { /* writer bloqueado por um hook de teste → RecordingStopped mostra dropped > 0 */ }
[Fact] public void Enforces_total_size_cap() { }
[Fact] public void Trim_keeps_only_requested_window() { /* 1º e último SessionTime dentro de [from,to]; o pipeline sobre o arquivo recortado não lança */ }
[Fact] public void Core_pipeline_detects_same_laps_on_recording() { /* voltas do pipeline sobre a gravação == voltas sobre os frames originais */ }
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Commit** — `git commit -m "feat(app): crash-safe .ibt telemetry recorder and trimming"`

---

### Task 5: Pacote de diagnóstico + subcomando `diag`

**Files:**
- Create: `src/FuelPace.App/Diagnostics/{DiagnosticBundle,SystemInfoKeys}.cs`
- Modify: `tools/FuelPace.Replay` (subcomando `diag`)
- Test: `tests/FuelPace.App.Tests/Diagnostics/DiagnosticBundleTests.cs`, `tests/FuelPace.Tests/Tools/DiagCliTests.cs`

**Interfaces:**
- Produces:
  - `sealed record BundleOptions(bool IncludeRecordings = true, int MaxRecordings = 2)`
  - `static class DiagnosticBundle { static string Create(string appDataRoot, string destDir, BundleOptions o, IReadOnlyDictionary<string, string> systemInfo, DiagLog log, TimeProvider clock); }`: antes de zipar, chama `sink.FlushNow()` (recebido via `Action flush`). Gera `FuelPace-diag-<yyyyMMdd-HHmmss>.zip` com `logs/`, `settings.json`, `profiles/`, `recordings/` (as N mais recentes), `system-info.txt` (`chave: valor`, uma por linha) e `manifest.json` (versão, data, contagem de marcadores, lista de arquivos com tamanho)
  - `static class SystemInfoKeys` com as chaves que o Agent preenche: `version`, `os`, `dotnet`, `cpuCount`, `monitors` (cada um com nome, retângulo, DPI), `gpu`, `overlayBackend`, `debugMode`, `simWindow`, `fullscreenExclusive`
  - CLI: `FuelPace.Replay diag <bundle.zip|pasta>` → resumo: versão e sistema; erros e warnings (com stack); linha do tempo de `StateChanged`, `CaptureStarted`/`Stopped`, `LapCompleted`; para cada `UserMark`, os eventos de ±30 s em volta; tabela de `PerfMinute` (p99/máx do tick, gen0, working set, redesenhos/min); gravações incluídas com duração e voltas detectadas

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Bundle_contains_expected_entries() { /* raiz temporária com logs/, profiles/, recordings/ (3 arquivos) → zip com 2 gravações, system-info.txt e manifest.json */ }
[Fact] public void Bundle_without_recordings_option() { }
[Fact] public void Bundle_works_with_missing_folders() { /* nenhuma pasta ainda → zip só com system-info e manifest */ }
[Fact] public void Diag_cli_summarizes_marks_and_errors() { /* JSONL sintético com 1 UserMark e 1 Error → a saída tem "Marcador 1", o stack trace e a tabela PerfMinute */ }
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Commit** — `git commit -m "feat(app): diagnostic bundle and diag summary command"`

---

### Task 6: Layout do overlay em `DrawList` + prévia SVG

**Files:**
- Create: `src/FuelPace.App/Overlay/{DrawList,OverlayTheme,OverlayLayout,ITextMeasurer,FixedMetricsMeasurer,SvgRenderer}.cs`, `tools/FuelPace.Preview/{FuelPace.Preview.csproj,Program.cs}`
- Test: `tests/FuelPace.App.Tests/Overlay/OverlayLayoutTests.cs`

**Interfaces:**
- Consumes: `OverlayState`, `SignalState`, `OverlayText` (Core Tasks 12–13)
- Produces:
  - `enum FontRole : byte { Label, Value, ValueBold, Body, BodyBold, Badge }` (Label/Body/BodyBold/Badge = Inter; Value/ValueBold = JetBrains Mono)
  - `readonly record struct Rgba(byte R, byte G, byte B, byte A)` + `Rgba WithOpacity(double f)`
  - `DrawList`: lista reutilizável (sem alocação depois do aquecimento, texto num arena `char[]` interno) de comandos `FillRoundedRect(RectF, float radius, Rgba)`, `StrokeDashedRect(RectF, float width, Rgba)`, `FillRect`, `StrokeRect`, `Text(int arenaStart, int length, FontRole, float sizePx, float letterSpacingEm, bool italic, Rgba, float x, float baselineY)`; `void Clear()`, `ReadOnlySpan<DrawCommand> Commands`, `ReadOnlySpan<char> TextOf(in DrawCommand c)`, `SizeF Size`, `long ContentHash` (hash de todos os comandos; o renderer usa para pular quadros iguais)
  - `interface ITextMeasurer { float Width(ReadOnlySpan<char> text, FontRole role, float sizePx, float letterSpacingEm); float Ascent(FontRole role, float sizePx); }`
  - `FixedMetricsMeasurer` (testes e SVG): Mono = 0,6 em por char; Inter = 0,56 em (maiúsculas 0,68 em); ascent 0,8 em
  - `sealed record OverlayViewOptions(double Scale, double BackgroundOpacity, double ContentOpacity, bool ShowPaceLine, bool ShowInsightLine, bool EditMode, bool DebugTag, string ToggleEditHotkey, int ScreenX, int ScreenY)`
  - `static class OverlayLayout { static void Build(in OverlayState s, ReadOnlySpan<char> carShort, ReadOnlySpan<char> trackShort, OverlayViewOptions o, ITextMeasurer m, DrawList dest); }`
  - `static class SvgRenderer { static string Render(DrawList list); }` (fontes por `font-family`, só para prévia)
  - `tools/FuelPace.Preview`: escreve `docs/overlay-preview/{azul,vermelho,nao-factivel,treino-sem-baseline,edicao,inativo,opacidade-30,opacidade-90,escala-200}.svg` a partir de `OverlayState`s fixos que reproduzem os valores dos mocks

**Geometria a 1× (transcrita do mock 6, "tamanho real 1×"; tudo × `Scale` × DPI):**

| Elemento | Especificação |
|---|---|
| Painel | largura 300; padding 9 (vertical) / 11 (horizontal); raio 6; fundo `rgba(10,12,16, BackgroundOpacity)`; espaço de 6 entre linhas |
| Rótulos | Inter 9 px, peso 600, letter-spacing 0,1 em, `#A9B2BD`, caixa alta; largura fixa de 46 nas colunas da esquerda (43 na linha do ALVO) |
| Linha 1 | `COMB` · 8 · valor Mono 21 px 600 `#F2F4F7` · 8 · `L` Inter 10 px `#A9B2BD`; à direita, `17.3 v · 23 min` Mono 12 px `#D5DAE0` |
| Linha 2 | grupos separados por 20: `VOLTA` · 8 · Mono 16 px **700** (cor do estado); `ÚLT` · 6 · Mono 16 px 500 (cor suave do estado; sufixo `*` se inválida); `MÉD` · 6 · Mono 16 px 500 `#F2F4F7` (itálico se vier da baseline) · 6 · `L/v` 9 px |
| Linha 3 (ALVO) | retângulo de x = 7 a 293 (margem −4); padding 5/7; raio 4; esquerda: `ALVO` (cor tingida) · 8 · Mono 18 px 700 (`~` como prefixo se estimativa) · 8 · `L/v` 9 px tingido; centro: `faltam 16.6 v` / `STINT 25` / `treino · sem limite` / `sem limite` Mono 11 px; direita: selo, padding 3/6, raio 3, Inter 8 px 700, letter-spacing 0,08 em |
| Linha 3 NÃO FACTÍVEL | esquerda em coluna: `NÃO FACTÍVEL` Inter 12 px 800, letter-spacing 0,06 em, `#FFFFFF`; 1 px; `alvo 2.01 · mín possível 2.23` Mono 8,5 px `#C9CFD6`; selo `PIT` com fundo `#F2F4F7`, texto `#0E1013`, peso 800 |
| Linha de pace (opcional) | depois do ALVO: `ÚLT 79.51 s · MÉD 80.14 · base 80.12`, Mono 10 px `#D5DAE0` |
| Linha 4 (insight) | Inter 11 px `#E3E7EC`; os primeiros `boldLength` chars em 700 |
| Pílula REC | 4 px abaixo do painel, alinhada à direita; padding 4/7; raio 4; mesmo fundo; ponto Ø6 `#FF5A5A` · 5 · `REC` Inter 8 px 700, letter-spacing 0,1 em · 5 · `4/5 válidas` Mono 9 px `#D5DAE0`; com `DebugTag`, pílula `DBG` (Inter 8 px 700 `#A9B2BD`) à esquerda da REC, separada por 4 |
| Modo edição | contorno tracejado de 1 px `rgba(255,255,255,0.75)`, 3 px fora do painel; alças 7×7 brancas com borda de 1 px `#1E6BD6` nos cantos, 7 px fora; acima do painel, faixa `#1E6BD6` com `MODO EDIÇÃO` (600, letter-spacing 0,08 em) + `arraste para mover · puxe um canto para redimensionar` (400), Inter 11 px fixo (sem escala); abaixo, duas pílulas `rgba(10,12,16,0.75)` de 11 px: `<hotkey> trava e volta ao modo click-through` (hotkey em Mono 600) e `X 1840 · Y 980 · escala 200%` |
| INATIVO | só a linha 1; opacidade de conteúdo × 0,5 |

**Cores por estado:**

| Estado | Fundo do ALVO | Tinta do rótulo e do `L/v` | Texto central | Selo | `VOLTA` / `ÚLT` |
|---|---|---|---|---|---|
| Blue | `rgba(47,128,237,0.26)` | `#CFE2FF` | `#E6EEF9` | `NO ALVO` em `#1E6BD6` | `#6AAEFF` / `#9CC8FF` |
| Red | `rgba(235,87,87,0.28)` | `#FFD9D9` | `#FBE9E9` | `ACIMA` em `#C7362F` | `#FF7A7A` / `#FFA8A8` |
| NotFeasible | `rgba(255,255,255,0.12)` | — | `#E6E9ED` | `PIT` (acima) | `#F2F4F7` / `#F2F4F7` |
| NoBaseline / NoTarget | `rgba(255,255,255,0.07)` | `#A9B2BD` (valor `—` também) | `#A9B2BD` | nenhum | `#F2F4F7` / `#F2F4F7` |

`ContentOpacity` multiplica o alfa de todo texto, selo e alça; `BackgroundOpacity` só afeta o fundo do painel e o das pílulas.

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Blue_state_has_blue_row_badge_and_volta_color() { /* comandos: FillRoundedRect com rgba(47,128,237,0.26) em x=7 w=286; texto "NO ALVO" com fundo #1E6BD6; VOLTA #6AAEFF; ÚLT #9CC8FF */ }
[Fact] public void Red_state_uses_acima_badge() { }
[Fact] public void Not_feasible_shows_title_subtitle_and_pit_badge() { /* "NÃO FACTÍVEL", "alvo 2.01 · mín possível 2.23", "PIT"; VOLTA neutra */ }
[Fact] public void No_baseline_practice_shows_dash_and_treino_sem_limite() { }
[Fact] public void Stint_mode_shows_stint_label() { /* "STINT 25" no lugar de "faltam" */ }
[Fact] public void Estimate_prefixes_tilde() { /* "~2.43" */ }
[Fact] public void Invalid_last_lap_gets_asterisk() { }
[Fact] public void Mean_from_baseline_is_italic() { }
[Fact] public void Insight_bold_prefix_split() { /* "Folga 1.8 L" BodyBold + " · pode atacar" Body */ }
[Fact] public void Inactive_shows_only_fuel_row_dimmed() { }
[Fact] public void Rec_pill_only_when_recording_and_debug_tag_when_enabled() { }
[Fact] public void Scale_2_doubles_every_dimension() { /* Size em 2× == 2 × Size em 1× (±0,5 px) */ }
[Fact] public void Opacity_options_affect_alpha_only() { }
[Fact] public void Edit_mode_adds_outline_handles_banner_and_footer() { /* footer contém "Ctrl+Shift+F" e "X 1840 · Y 980 · escala 200%" */ }
[Fact] public void Hidden_insight_line_shrinks_height() { }
[Fact] public void Build_allocates_nothing_after_warmup() { }
[Fact] public void Content_hash_changes_only_when_visible_output_changes() { }
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Gerar as prévias.** `dotnet run --project tools/FuelPace.Preview -- docs/overlay-preview` → 9 SVGs. Abrir no navegador e comparar com os mocks. Commitar os SVGs (o piloto revisa pelo GitHub).
- [ ] **Step 6: Commit** — `git commit -m "feat(app): overlay layout as draw list, matching approved mocks; SVG previews"`

---

### Task 7: Posicionamento multi-monitor e DPI

**Files:**
- Create: `src/FuelPace.App/Placement/{MonitorDesc,PixelRect,OverlayPlacement}.cs`
- Test: `tests/FuelPace.App.Tests/Placement/OverlayPlacementTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct PixelRect(int X, int Y, int Width, int Height)`
  - `sealed record MonitorDesc(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double DpiScale, bool IsPrimary)`
  - `static class OverlayPlacement`:
    - `PixelRect Resolve(OverlaySettings o, IReadOnlyList<MonitorDesc> monitors, SizeF logicalSize, out MonitorDesc monitor)`: acha o monitor por `DeviceName` (fallback: o primário); tamanho físico = `logicalSize × Scale × DpiScale`; posição = `WorkArea.X + OffsetX`, `WorkArea.Y + OffsetY`; empurra para dentro da `WorkArea` (de preferência com tudo visível; se for maior que a área, alinha no canto superior esquerdo)
    - `OverlaySettings FromUserMove(OverlaySettings o, PixelRect newRect, IReadOnlyList<MonitorDesc> monitors)`: monitor = o que contém o centro do retângulo; recalcula os offsets
    - `double ScaleFromUserResize(PixelRect newRect, SizeF logicalSize, double dpiScale)`: escala pela largura, clamp em 0,5–2,5
    - `string DescribeForUi(MonitorDesc m, int index)` → `"1 · 2560 × 1440"`

- [ ] **Step 1: Testes que falham**

```csharp
[Fact] public void Places_on_named_monitor_with_offsets() { }
[Fact] public void Missing_monitor_falls_back_to_primary_and_stays_visible() { }   // Review Focus 5
[Fact] public void Clamps_into_work_area_after_resolution_drop() { /* offset 3000 num monitor de 1920 → x + w ≤ WorkArea.Right */ }
[Fact] public void Dpi_scale_multiplies_size() { /* DpiScale 1.5 e Scale 2 → tamanho físico = 3 × lógico */ }
[Fact] public void Move_to_second_monitor_updates_device_and_offsets() { }
[Fact] public void Resize_maps_to_scale_and_clamps() { }
[Fact] public void Negative_coordinates_left_monitor() { /* monitor à esquerda do primário com X = −1920 */ }
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Commit** — `git commit -m "feat(app): multi-monitor, DPI-aware overlay placement"`

---

### Task 8: IPC (named pipe) — contratos, servidor e cliente

**Files:**
- Create: `src/FuelPace.App/Ipc/{IpcMessages,IpcJsonContext,IpcServer,IpcClient}.cs`
- Test: `tests/FuelPace.App.Tests/Ipc/IpcTests.cs`

**Interfaces:**
- Consumes: `AppSettings` (Task 2), `ProfileDocument`, `CapturedLap` (Core)
- Produces:
  - Mensagens (`abstract record IpcMessage`, discriminador JSON `"type"`). Settings → Agent: `Hello(string Version)`, `GetSnapshot`, `SetCapture(bool On)`, `UpdateSettings(AppSettings Settings)`, `SetEditMode(bool On)`, `ListProfiles`, `GetProfile(string Key)`, `ProfileCommand(string Key, string Op /*"setLapExcluded"|"setCaptureActive"|"deleteCapture"*/, string CaptureId, int? Lap, bool? Value)`, `CreateBundle(bool IncludeRecordings)`, `SetDebugMode(bool On)`, `ShowSettingsRequest` (enviada por uma 2ª instância do Agent). Agent → Settings: `Snapshot(AgentSnapshot S)`, `CaptureLapAdded(CapturedLapDto Lap, int Valid, int Total)`, `CaptureFinished(string ProfileKey, string CaptureId)`, `ProfileList(IReadOnlyList<ProfileSummary> Items)`, `Profile(string Key, string Json)`, `SettingsApplied(AppSettings Settings)`, `BundleCreated(string Path)`, `Notice(string Level, string Text)`, `Error(string Text)`
  - `sealed record AgentSnapshot(string Version, bool SimConnected, string? ProfileKey, string? CarName, string? TrackName, string? SessionType, bool CaptureActive, IReadOnlyList<CapturedLapDto> CaptureLaps, int Valid, int Total, AppSettings Settings, bool DebugMode, string OverlayBackend, bool FullscreenExclusive, IReadOnlyList<string> HotkeyFailures, IReadOnlyList<string> MonitorLabels)`
  - `sealed class IpcServer : IDisposable` com `(string pipeName, Func<IpcMessage, IpcServerContext, ValueTask> handler, DiagLog log)`, `void Start()`, `ValueTask BroadcastAsync(IpcMessage m)`. Até 4 clientes; `PipeOptions.CurrentUserOnly | Asynchronous`; uma mensagem por linha (JSON UTF-8 + `\n`); mensagem maior que 8 MB → desconecta o cliente
  - `sealed class IpcClient : IAsyncDisposable` com `(string pipeName)`, `Task<bool> ConnectAsync(TimeSpan timeout)`, `ValueTask SendAsync(IpcMessage m)`, `event Action<IpcMessage>? Received`, `event Action? Disconnected`
  - Nome de produção: `FuelPace`. Testes usam um nome aleatório.

- [ ] **Step 1: Testes que falham** (named pipe real, que funciona no Linux)

```csharp
[Fact] public void Request_and_reply_round_trip() { /* cliente GetSnapshot → servidor responde Snapshot → cliente recebe com campos iguais */ }
[Fact] public void Broadcast_reaches_all_clients() { }
[Fact] public void Client_disconnect_does_not_break_server() { }
[Fact] public void Oversized_message_disconnects_client() { }
[Fact] public void Unknown_type_is_ignored_and_logged() { }
[Fact] public void All_message_types_serialize_round_trip() { /* teoria sobre todas as mensagens */ }
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Commit** — `git commit -m "feat(app): named-pipe IPC between Agent and Settings"`

---

### Task 9: `AgentHost`: orquestração portável do Agent

**Files:**
- Create: `src/FuelPace.App/Host/{Platform,AgentHost,RenderScheduler,AutoHidePolicy}.cs`
- Test: `tests/FuelPace.App.Tests/Host/AgentHostTests.cs` (+ fakes em `tests/FuelPace.App.Tests/Fakes/`)

**Interfaces:**
- Consumes: tudo do Core; Tasks 2–8
- Produces (`Platform.cs`):
  - `interface ITelemetrySourceFactory { ITelemetrySource? TryOpen(); }` (live ou replay; `null` = sim ausente)
  - `interface IOverlaySurface : IDisposable { void SetBounds(PixelRect r); void Render(DrawList list); void SetVisible(bool v); void SetEditMode(bool on); string BackendName { get; } event Action<PixelRect>? UserMoved; event Action<PixelRect>? UserResized; }`
  - `interface IUiDispatcher { void Post(Action a); }` (o host só passa delegates em cache)
  - `interface IHotkeyService { bool Register(int id, Hotkey key); void UnregisterAll(); event Action<int>? Pressed; }`
  - `interface ITrayIcon { void SetTooltip(string text); void Notify(string title, string text); void SetDebugMenu(bool visible); event Action<TrayCommand>? Command; }` com `enum TrayCommand { OpenSettings, ToggleVisible, ToggleEdit, InsertMark, CreateBundle, OpenLogs, Exit }`
  - `interface IMonitorProvider { IReadOnlyList<MonitorDesc> GetMonitors(); event Action? Changed; }`
  - `interface ISystemServices { IReadOnlyDictionary<string, string> CollectSystemInfo(); bool IsFullscreenExclusiveActive(); void OpenFolder(string path); void LaunchSettings(); void SetAutostart(bool on); string DesktopDir { get; } }`
  - `sealed record AgentOptions(string AppDataRoot, bool DebugFromCommandLine, bool DebugMarker, string? ReplayPath, ReplaySpeed ReplaySpeed, TimeSpan? ExitAfter, string PipeName = "FuelPace")`
  - `sealed class AgentHost : IDisposable` com `(AgentOptions o, ITelemetrySourceFactory sources, IOverlaySurface surface, IUiDispatcher ui, IHotkeyService hotkeys, ITrayIcon tray, IMonitorProvider monitors, ISystemServices sys, ITextMeasurer measurer, TimeProvider clock)`, `void Start()`, `Task Completion` (termina em `Exit` ou `ExitAfter`), `bool DebugMode`
  - `sealed class RenderScheduler` (no thread de telemetria: pede um render pelo `ui` quando `publisher.Version` ou a versão das opções mudam, com pelo menos 100 ms entre pedidos e no máximo um pendente) e `static class AutoHidePolicy { static bool ShouldShow(bool userVisible, bool autoHide, bool simConnected, in OverlayState s, bool editMode); }` (edição sempre mostra; com `AutoHide`, oculta sem sim ou com `Inactive` por > 3 s seguidos)

**Comportamento do `AgentHost`:**
- **Startup:** `AppDiagEvents.RegisterAll()`; `SettingsStore.Load`; modo debug = `DebugFromCommandLine || DebugMarker || settings.Debug.Enabled`; `DiagLog` (Trace no debug, senão Info) + `DiagFileSink` + `CrashHandler`; `Text(Info, "startup", <versão, args, sistema, settings JSON>)`; IPC no ar; hotkeys registradas (falhas → `HotkeyRegistered ok=0` + `tray.Notify` uma vez + lista em `AgentSnapshot.HotkeyFailures`).
- **Thread de telemetria** (`Normal`, nome `FuelPace.Telemetry`): laço `TryOpen` → `TelemetryLoop.Step()` até `EndOfStream`/`Disconnected` → `pipeline.Flush()`, `recorder.OnSourceEnded()` → tenta abrir de novo a cada 1 s. O pipeline é o mesmo entre conexões (a baseline continua); o detector de voltas é zerado em cada conexão nova.
- **Render** na thread de UI: `OverlayLayout.Build` num `DrawList` reutilizado → `surface.Render` só se o `ContentHash` mudou → contador `OverlayRedraws` por minuto. Posição por `OverlayPlacement`; `UserMoved`/`UserResized` → settings → `Save` (debounce de 500 ms) → `SettingsApplied` por broadcast.
- **Atalhos:** ToggleEdit → modo edição; ToggleVisible → visibilidade; DebugMark (só no debug) → `UserMark` com número sequencial + `tray.Notify("FuelPace", "Marcador N")`.
- **IPC:** `SetCapture` → `pipeline.RequestCaptureStart/Stop`; `CaptureCompleted` → salva o perfil pela fila de I/O e manda `CaptureFinished` por broadcast; voltas da captura → `CaptureLapAdded`; `ProfileCommand` → `ProfileOperations` + `Save` + `Profile` atualizado + `OnSessionContext` se for o perfil atual; `UpdateSettings` → `Normalize` → aplica (`pipeline.SetConfig`, hotkeys de novo, autostart, `DiagLog.Level`, recorder liga/desliga) → `Save`; `CreateBundle` → `DiagnosticBundle.Create` em `sys.DesktopDir` → `BundleCreated`; `ShowSettingsRequest` → `sys.LaunchSettings()`.
- **Tela cheia exclusiva:** a cada 5 s com o sim conectado, `sys.IsFullscreenExclusiveActive()`; quando muda → `FullscreenExclusive` + `Notice` + `tray.Notify` (uma vez por sessão).
- **Topmost:** responsabilidade da superfície (Task 11), não do host.
- **ExitAfter** (testes de fumaça): encerra limpo, com `Shutdown` no log.

- [ ] **Step 1: Testes que falham** (fakes de todas as interfaces; `ITelemetrySourceFactory` falso entrega `.ibt` sintéticos; `FakeTimeProvider` próprio)

```csharp
[Fact] public void Renders_at_most_10_per_second_and_zero_when_static() { /* 60 s de frames constantes → renders ≤ 1 depois do 1º; com dados mudando → ≤ 10/s */ }  // PERF-06
[Fact] public void Reconnects_after_disconnect_and_shows_data_again() { /* fábrica: null, null, fonte A (termina), null, fonte B → depois de B o overlay volta a ter FuelL */ }  // critério 7
[Fact] public void Capture_via_ipc_persists_and_broadcasts() { /* IpcClient real: SetCapture(true) → voltas → CaptureLapAdded; SetCapture(false) → CaptureFinished; perfil no disco */ }  // critério 12 (lado Agent)
[Fact] public void Capture_survives_settings_client_disconnect() { /* cliente liga a captura e desconecta → voltas continuam; cliente novo → Snapshot.CaptureLaps traz todas */ }  // critério 12
[Fact] public void Hotkey_failure_is_reported_once_and_app_continues() { }       // Review Focus 2
[Fact] public void Debug_mode_sources_are_ored() { /* marcador OU --debug OU settings → DiagLog Trace, recorder ativo, hotkey de marcador registrada */ }
[Fact] public void Mark_hotkey_emits_numbered_user_marks() { }
[Fact] public void Settings_update_applies_live() { /* ReserveL 1.0 → pipeline usa no próximo publish; Scale 2 → SetBounds com o dobro */ }
[Fact] public void User_move_persists_after_debounce() { }
[Fact] public void Auto_hide_rules() { /* AutoHidePolicy: tabela de casos */ }
[Fact] public void Fullscreen_exclusive_notice_once_per_session() { }             // Review Focus 1 (lado host)
[Fact] public void Second_instance_request_launches_settings() { }                // Review Focus 4 (lado host)
[Fact] public void Bundle_command_creates_zip_on_desktop() { }
[Fact] public void Exit_after_shuts_down_cleanly_and_logs() { }
[Fact] public void Log_contains_startup_lap_and_perf_events() { /* replay sintético 3 min → JSONL com Startup, LapCompleted, PerfMinute */ }
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Commit** — `git commit -m "feat(app): portable agent host orchestrating telemetry, overlay, IPC and debug"`

---

### Task 10: Agent no Windows — leitor ao vivo, processo, instância única, sistema

**Files:**
- Create: `src/FuelPace.Agent/Live/{IRacingMemoryReader,LiveSourceFactory}.cs`, `src/FuelPace.Agent/Win32/{MessageWindow,SingleInstance,Autostart,SystemInfoCollector,FullscreenDetector}.cs`, `src/FuelPace.App/Live/IrsdkLiveImageWriter.cs`
- Modify: `src/FuelPace.Agent/Program.cs`, `src/FuelPace.Agent/NativeMethods.txt`
- Test: `tests/FuelPace.App.Tests/Live/LiveImageWriterTests.cs` (Linux), `tests/FuelPace.Windows.Tests/LiveReaderTests.cs` (Windows)

**Interfaces:**
- Consumes: `LiveSnapshotReader` (Core Task 17), `AgentHost` (Task 9)
- Produces:
  - `sealed class IrsdkLiveImageWriter` (portável; usado pelo FakeSim e pelos testes): `(IReadOnlyList<VarSpec> vars, int numBuf = 3, int yamlCapacity = 1 << 20)`, `int TotalSize`, `void Initialize(Span<byte> image, string yaml)`, `void UpdateSessionInfo(Span<byte> image, string yaml)` (incrementa `sessionInfoUpdate`), `void WriteFrame(Span<byte> image, in TelemetryFrame f)` (próximo buffer, `tickCount` crescente), `void SetConnected(Span<byte> image, bool on)`
  - `sealed class IRacingMemoryReader : ITelemetrySource` com `(string mapName = @"Local\IRSDKMemMapFileName", string eventName = @"Local\IRSDKDataValidEvent")`, `static IRacingMemoryReader? TryOpen(...)`: `MemoryMappedFile.OpenExisting` (leitura), `AcquirePointer` uma vez, `EventWaitHandle.OpenExisting`; `TryRead` = `WaitOne(200)` + `LiveSnapshotReader.TryRead(new ReadOnlySpan<byte>(ptr, size))`; `NotConnected` por > 2 s → `Disconnected`
  - `LiveSourceFactory : ITelemetrySourceFactory` (`--replay` → `IbtTelemetrySource`; senão `IRacingMemoryReader.TryOpen`)
  - `SingleInstance`: mutex `Local\FuelPace.Agent`. Se já existir, `IpcClient` manda `ShowSettingsRequest` e o processo sai com 0
  - `Program.Main`: parse de `--debug`, `--replay <ibt>`, `--speed max|1|10`, `--exit-after <s>`, `--appdata <dir>`; `Process.PriorityClass = BelowNormal`; `GCSettings.LatencyMode = SustainedLowLatency`; `MessageWindow` (janela só de mensagens: hotkeys, bandeja, `WM_APP` do dispatcher, `WM_DISPLAYCHANGE`/`WM_DPICHANGED`/`WM_SETTINGCHANGE` → `IMonitorProvider.Changed`); laço `GetMessage`
  - `SystemInfoCollector` (as chaves de `SystemInfoKeys`; GPU via DXGI `AdapterDescription`; janela do iRacing via o processo `iRacingSim64DX11` → estilos e retângulo); `FullscreenDetector` (`SHQueryUserNotificationState == QUNS_RUNNING_D3D_FULL_SCREEN`); `Autostart` (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, valor `FuelPace`, caminho entre aspas)

- [ ] **Step 1: Teste portável que falha:** `LiveImageWriterTests`: escreve uma imagem com 3 buffers e 5 frames → `LiveSnapshotReader` lê o último frame, `SessionInfoUpdate` correto, `SetConnected(false)` → `NotConnected`.
- [ ] **Step 2: Testes Windows** (`LiveReaderTests`, nomes de mapa e evento aleatórios): o teste cria `MemoryMappedFile.CreateNew` + `EventWaitHandle` e escreve com `IrsdkLiveImageWriter` numa thread a 60 Hz; o `IRacingMemoryReader` recebe os frames em ordem, sem leitura rasgada aceita; ao desconectar → `Disconnected`; ao recriar o mapa → `TryOpen` volta a funcionar (critério 7). Um teste de `SingleInstance` com dois mutexes.
- [ ] **Step 3: Implementar.** **Step 4:** Linux: `dotnet test FuelPace.Linux.slnf` passa e `dotnet build src/FuelPace.Agent` compila; push → CI Windows verde.
- [ ] **Step 5: Commit** — `git commit -m "feat(agent): live iRacing reader, process setup, single instance, system info"`

---

### Task 11: Overlay no Windows — janela, DirectComposition/Direct2D, fallback layered

**Files:**
- Create: `src/FuelPace.Agent/Overlay/{OverlayWindow,CompositionRenderer,LayeredRenderer,D2DDrawListRenderer,DWriteFonts,DWriteTextMeasurer,BackendSelector}.cs`, `assets/fonts/{JetBrainsMono-Regular,JetBrainsMono-Medium,JetBrainsMono-SemiBold,JetBrainsMono-Bold,Inter-Regular,Inter-SemiBold,Inter-Bold,Inter-ExtraBold}.ttf`, `assets/fonts/OFL.txt`
- Test: `tests/FuelPace.App.Tests/Overlay/BackendSelectorTests.cs` (lógica portável em `FuelPace.App/Overlay/BackendSelection.cs`), `tests/FuelPace.Windows.Tests/RenderTests.cs`, `tests/FuelPace.Windows.Tests/OverlayWindowTests.cs`

**Interfaces:**
- Consumes: `IOverlaySurface`, `DrawList`, `ITextMeasurer`, `RenderBackend` (Tasks 2, 6, 9)
- Produces:
  - `static class BackendSelection { static RenderBackend Choose(RenderBackend requested, Func<bool> tryComposition, out string reason); }` (portável: `Auto` tenta composition; se falhar → `Layered`, com o motivo)
  - `OverlayWindow : IOverlaySurface`:
    - `CreateWindowEx` com `WS_POPUP` e `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, mais `WS_EX_NOREDIRECTIONBITMAP` (composition) ou `WS_EX_LAYERED` (fallback)
    - travado: `WS_EX_LAYERED | WS_EX_TRANSPARENT` (+ `SetLayeredWindowAttributes(255, LWA_ALPHA)` no composition) → click-through
    - edição: sem `WS_EX_TRANSPARENT`; `WM_NCHITTEST` → `HTTOPLEFT/HTTOPRIGHT/HTBOTTOMLEFT/HTBOTTOMRIGHT` nas alças e `HTCAPTION` no resto do painel; `WM_SIZING` mantém a proporção; `WM_EXITSIZEMOVE` → `UserMoved`/`UserResized`
    - `WM_MOUSEACTIVATE` → `MA_NOACTIVATE`; `ShowWindow(SW_SHOWNOACTIVATE)`
    - topmost: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)` + timer de 2 s → `SetWindowPos(HWND_TOPMOST, …, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` + `TopmostReasserted` (Trace)
  - `CompositionRenderer`: D3D11 (hardware, BGRA; fallback WARP) → swap chain de composição (B8G8R8A8, `AlphaMode.Premultiplied`, 2 buffers, `FlipSequential`) → `IDCompositionDevice` + target topmost + visual; `Render` = `D2DDrawListRenderer` no back buffer → `Present(0, 0)` → `Commit`; `DXGI_ERROR_DEVICE_REMOVED/RESET` → `DeviceLost` + recriação
  - `LayeredRenderer`: bitmap WIC/D2D premultiplicado → `UpdateLayeredWindow`
  - `D2DDrawListRenderer`: desenha os comandos; `IDWriteTextLayout` em cache por (texto, papel, tamanho), recriado só quando muda; cache LRU de 64
  - `DWriteFonts` (coleção em memória a partir de `assets/fonts` embutidos como `EmbeddedResource`); `DWriteTextMeasurer : ITextMeasurer`

- [ ] **Step 1: Teste portável** `BackendSelectorTests` (Auto + falha → Layered com motivo; Composition forçado + falha → Layered e `Warn`; Layered forçado → nem tenta composition).
- [ ] **Step 2: Fontes.** Baixar os TTF oficiais (releases do GitHub `JetBrains/JetBrainsMono` e `rsms/inter`) e conferir a licença OFL de cada um. Commitar com `OFL.txt`.
- [ ] **Step 3: Testes Windows**
  - `RenderTests.Renders_all_states_to_png`: device WARP + `D2DDrawListRenderer` num bitmap offscreen para os 9 estados da prévia em 1× e 2× → `artifacts/renders/<estado>@<escala>x.png` (sobem como artifact do CI). Confere que o PNG não está vazio e que o pixel no centro da linha do ALVO tem a cor do estado (±8 por canal), compondo sobre preto.
  - `OverlayWindowTests` (pula com motivo se não houver composição DWM no runner): a janela é criada; travada tem `WS_EX_TRANSPARENT|WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` e não aparece na barra de tarefas (critério 5, parte automatizável); `WindowFromPoint` no centro do overlay travado não devolve o HWND do overlay; em edição devolve.
- [ ] **Step 4: Implementar.** Linux: compila. CI Windows: verde. Depois do CI, baixar os PNGs com `gh run download <id> -n renders` e comparar com os mocks; ajustar o layout (Task 6) se divergirem.
- [ ] **Step 5: Commit** — `git commit -m "feat(agent): overlay window with DirectComposition/Direct2D and layered fallback"`

---

### Task 12: Bandeja e atalhos globais

**Files:**
- Create: `src/FuelPace.Agent/Tray/{TrayIcon,Hotkeys}.cs`, `assets/icon.ico`
- Modify: `src/FuelPace.Agent/Program.cs` (liga tudo ao `AgentHost`)
- Test: `tests/FuelPace.Windows.Tests/HotkeysTests.cs`

**Interfaces:**
- Consumes: `ITrayIcon`, `IHotkeyService`, `TrayCommand` (Task 9), `MessageWindow` (Task 10)
- Produces:
  - `TrayIcon : ITrayIcon`: `Shell_NotifyIcon` (NIM_ADD/MODIFY/DELETE, `NOTIFYICON_VERSION_4`, balão via `NIF_INFO`). Menu de contexto: *Abrir configurações* · *Mostrar/ocultar overlay* · *Modo edição* · (debug) *Inserir marcador* · *Gerar pacote de diagnóstico* · *Abrir pasta de logs* · *Sair*. Duplo clique → abrir configurações. Recria o ícone em `TaskbarCreated` (Explorer reiniciado).
  - `Hotkeys : IHotkeyService`: `RegisterHotKey(messageHwnd, id, mods | MOD_NOREPEAT, vk)`; `WM_HOTKEY` → `Pressed`
  - `assets/icon.ico`: 16/32/48 px, quadrado escuro arredondado (`#0A0C10`) com "F" branco e ponto `#FF5A5A`; gerado por qualquer ferramenta e commitado

- [ ] **Step 1: Teste Windows:** registrar um hotkey incomum (`Ctrl+Alt+Shift+F24`) → `true`; registrar o mesmo de novo em outra janela → `false` (Review Focus 2 em ambiente real).
- [ ] **Step 2: Implementar.** **Step 3:** Linux compila; CI Windows verde; `SmokeTests.Agent_starts_and_exits` continua verde com bandeja e hotkeys ativos.
- [ ] **Step 4: Commit** — `git commit -m "feat(agent): tray icon menu and global hotkeys"`

---

### Task 13: FakeSim + testes ponta a ponta no CI Windows

**Files:**
- Modify: `tools/FuelPace.FakeSim/Program.cs`
- Test: `tests/FuelPace.Windows.Tests/EndToEndTests.cs`

**Interfaces:**
- Consumes: `IrsdkLiveImageWriter` (Task 10), `IbtTelemetrySource`, `SyntheticRace` (Core/App)
- Produces: `FuelPace.FakeSim <arquivo.ibt | --synthetic-race <voltas>> [--speed 1|10|max] [--loop] [--cars 20] [--disconnect-after <s>] [--map <nome>] [--event <nome>]`: cria a memória compartilhada e o evento com os nomes do iRacing (ou os informados), publica os frames no ritmo pedido (com `CarIdx*` sintéticos no modo `--synthetic-race`), e no fim zera o bit de status e fecha. Serve para o piloto testar o Agent no Windows sem abrir o iRacing.

- [ ] **Step 1: Testes Windows**
  - `Agent_follows_fake_sim_and_reconnects` (critério 7): FakeSim (nomes aleatórios via `--map/--event`, também passados ao Agent pelas variáveis de ambiente `FUELPACE_IRSDK_MAP` e `FUELPACE_IRSDK_EVENT`, que o `LiveSourceFactory` lê com prioridade sobre os nomes padrão) com corrida sintética a 10× → Agent `--debug --exit-after 40 --appdata <tmp>`; no meio, FakeSim é encerrado e reiniciado. Expected: código 0; o JSONL tem `SourceOpened` 2×, `SourceEnded` 1×, ≥ 3 `LapCompleted`, `PerfMinute`, nenhum `Error`; existe uma gravação `.ibt` legível pelo `IbtReader`.
  - `Second_agent_instance_exits_and_requests_settings` (Review Focus 4).
  - `Capture_end_to_end_with_ipc` (critério 12, sem UI): `IpcClient` liga a captura, espera 4 voltas, desconecta, reconecta, desliga → perfil salvo com as voltas.
  - Logs e gravações do teste vão para `artifacts/e2e/` (artifact do CI).
- [ ] **Step 2: Implementar o FakeSim.** **Step 3:** CI Windows verde; baixar `artifacts/e2e` e rodar `FuelPace.Replay diag` no Linux sobre eles (o ciclo de feedback funcionando de ponta a ponta).
- [ ] **Step 4: Commit** — `git commit -m "test(e2e): fake iRacing shared-memory simulator and agent end-to-end tests"`

---

### Task 14: ViewModels do Settings (portáveis)

**Files:**
- Create: `src/FuelPace.App/SettingsUi/{ReasonLabels,LapTimeFormat}.cs`, `src/FuelPace.App/SettingsUi/ViewModels/{ShellViewModel,CaptureViewModel,OverlayViewModel,ProfilesViewModel,DiagnosticsViewModel,LapRowViewModel}.cs`, `src/FuelPace.App/SettingsUi/IAgentConnection.cs`
- Test: `tests/FuelPace.App.Tests/SettingsUi/*Tests.cs`

**Interfaces:**
- Consumes: `IpcClient` e mensagens (Task 8), `AppSettings` (Task 2), `OverlayLayout`/`DrawList` (Task 6, para a prévia)
- Produces:
  - `interface IAgentConnection { Task<bool> ConnectAsync(); ValueTask SendAsync(IpcMessage m); event Action<IpcMessage>? Received; bool IsConnected { get; } void LaunchAgent(); }` (implementação real sobre `IpcClient`; os testes usam uma falsa)
  - `ReasonLabels.Format(LapReasons r) → string`: `pit`, `incidente`, `fora da pista`, `bandeira amarela`, `reset/reboque`, `reabastecimento`, `incompleta`, `lenta`, `excluída` (juntos por `, `). O status da linha: `válida`, `válida · economia` (consumo < mediana − 1 desvio da baseline), `inválida · <motivos>`, `em andamento`
  - `LapTimeFormat.Format(double? s) → "1:20.897"` (ou `—`)
  - `ShellViewModel`: seções `Captura`, `Overlay e cálculo`, `Perfis`, `Diagnóstico`; `AgentOnline`; banner `Agent não está rodando` com o comando `IniciarAgent`; banner de tela cheia exclusiva; título `FuelPace · Configurações`
  - `CaptureViewModel` (mock 7): `bool Capturing` (two-way → `SetCapture`), `string SessionLine` (`Ligier JS P320 · Zolder Circuit (Alternate) · Practice`), `string CounterText` (`6 válidas / 9`), cartões `CONSUMO MÉDIO` / `TEMPO MÉDIO` / `AUTONOMIA` / `BASELINE` (`Confiança média`, `6 voltas válidas · ideal ≥ 10`), `ObservableCollection<LapRowViewModel> Laps` com ação `Excluir`/`Incluir` → `ProfileCommand`
  - `OverlayViewModel` (mock 8): monitor, posição X/Y, escala (50–250%), opacidades, linhas opcionais, ocultar fora da pista, parâmetros de cálculo, modo de voltas (auto/stint + voltas), atalhos (texto + validação + falhas vindas do snapshot em vermelho), `PosicionarNoJogo` → `SetEditMode(true)`, `DrawList Preview` (reconstruída quando as opções mudam). Mudanças → `UpdateSettings` com debounce de 300 ms
  - `ProfilesViewModel`: lista `carro · pista · n capturas · confiança`; ao selecionar, as capturas (data, tipo de sessão, condições, válidas/total, toggle `ativa`, `Apagar` com confirmação)
  - `DiagnosticsViewModel`: versão, backend do overlay, toggle `Modo debug` (→ `SetDebugMode`), `Gerar pacote` (com `Incluir gravações`) → mostra o caminho + `Abrir pasta`, `Abrir pasta de logs`

- [ ] **Step 1: Testes que falham** (conexão falsa)

```csharp
[Fact] public void Capture_toggle_sends_set_capture() { }
[Fact] public void Lap_rows_follow_capture_lap_added() { /* status "inválida · fora da pista, incidente"; tempo "1:33.175"; ação "Incluir" */ }
[Fact] public void Economy_lap_is_labelled() { /* volta 23 da fixture → "válida · economia" */ }
[Fact] public void Exclude_sends_profile_command() { }
[Fact] public void Summary_cards_match_mock_text() { /* "2.373", "mediana 2.386 · mín 2.289", "1:20.140", "42.1", "v · 56 min", "tanque 100 L", "Confiança média", "6 voltas válidas · ideal ≥ 10" */ }
[Fact] public void Overlay_changes_debounced_into_single_update() { }
[Fact] public void Invalid_hotkey_text_is_flagged_and_not_sent() { }
[Fact] public void Agent_offline_banner_and_launch() { }
[Fact] public void Reconnect_restores_state_from_snapshot() { }   // critério 12 (lado Settings)
```

- [ ] **Step 2–4:** falhar → implementar → passar.
- [ ] **Step 5: Commit** — `git commit -m "feat(settings): portable view models for capture, overlay, profiles and diagnostics"`

---

### Task 15: Views WPF do Settings + screenshots no CI

**Files:**
- Create: `src/FuelPace.Settings/{Theme.xaml,WpfDrawListRenderer.cs}`, `src/FuelPace.Settings/Views/{CapturePage,OverlayPage,ProfilesPage,DiagnosticsPage}.xaml(.cs)`
- Modify: `src/FuelPace.Settings/{App.xaml.cs,MainWindow.xaml}`
- Test: `tests/FuelPace.Windows.Tests/SettingsScreenshotTests.cs`

**Interfaces:**
- Consumes: ViewModels (Task 14), `DrawList` (Task 6)
- Produces:
  - `MainWindow` 1120×820 (mínimo 960×700) com `WindowChrome` próprio: barra de título `#0C0E11` com `FuelPace · Configurações` 13 px `#B7BFC9`, botões de minimizar e fechar de 44×32, menu lateral de 200 px `#0F1114` (item ativo `#1E2632`, branco, 600), conteúdo `#121418`, bordas `#2A2F37`/`#22262D`, cartões `#181B20`, inputs `#181B20` com borda `#343A44`, acento `#2F80ED`, links `#7DB6FF`. Inter e JetBrains Mono a partir de `assets/fonts` (Resource). As páginas seguem os mocks 7 e 8; as páginas de Perfis e Diagnóstico usam a mesma linguagem visual.
  - `WpfDrawListRenderer : FrameworkElement` (desenha um `DrawList` com `DrawingContext`; é a prévia "PRÉVIA · 100%" do mock 8)
  - Fechar a janela encerra o processo (PERF-07)

- [ ] **Step 1: Testes Windows** `SettingsScreenshotTests`: em thread STA, cria cada página com ViewModels de dados fixos (os valores dos mocks) → `RenderTargetBitmap` 1120×820 → `artifacts/settings/<pagina>.png`. Confere que não lança e que o PNG não está vazio.
- [ ] **Step 2: Implementar.** **Step 3:** CI Windows verde; baixar os PNGs e comparar com os mocks 7 e 8; corrigir o que divergir.
- [ ] **Step 4: Commit** — `git commit -m "feat(settings): WPF views matching approved mocks"`

---

### Task 16: Documentação do teste no Windows e do ciclo de feedback

**Files:**
- Create: `docs/windows-testing.md` (vai no zip como `LEIA-ME.md`), `docs/feedback-loop.md`, `tools/perf/presentmon.ps1`, `tools/perf/README.md`
- Modify: `tools/FuelPace.Replay` (subcomando `presentmon <csv-com> <csv-sem>` → resumo do PERF-01)
- Test: `tests/FuelPace.Tests/Tools/PresentMonCliTests.cs`

**Conteúdo:**
- `windows-testing.md` (para o piloto, em pt-BR):
  1. **Baixar:** Actions › último run verde › artifact `FuelPace-…-win-x64-debug`, ou a página de Releases.
  2. **Instalar:** descompactar numa pasta fixa e rodar `FuelPace.Agent.exe`; o Windows SmartScreen pode avisar porque o exe não é assinado (*Mais informações › Executar assim mesmo*).
  3. **iRacing em janela sem borda.**
  4. **Primeira sessão:** abrir o Settings pela bandeja, marcar Capturar, rodar 8–15 voltas, desmarcar e conferir o resumo.
  5. **Corrida.**
  6. **Durante o teste:** apertar `Ctrl+Shift+M` sempre que algo parecer errado; se puder, anotar o número do marcador e o que viu.
  7. **Depois:** bandeja › *Gerar pacote de diagnóstico* → um zip na Área de Trabalho.
  8. **Enviar:** copiar o zip para a máquina Linux em `~/Documentos/dev/github/iracing-fuel-pace/feedback/` (ignorada pelo git) e me dizer o nome do arquivo e as anotações dos marcadores.
  9. **Checklist** do que conferir (critérios 5, 6, 7 e 12; os 5 estados do overlay; posição e escala persistem; segundo monitor; ocultar/mostrar; modo edição).
  10. **Roteiro PERF-01:** PresentMon, 3 × 5 min com e sem o app, mesmo carro/pista/clima, usando `tools/perf/presentmon.ps1`.
  11. **Testar sem o iRacing:** `FuelPace.FakeSim.exe --synthetic-race 20 --speed 1`.
- `feedback-loop.md` (para mim/agentes):
  1. Descompactar em `feedback/<data>/`.
  2. `FuelPace.Replay diag feedback/<data>` → resumo.
  3. Para cada marcador e cada erro: localizar na linha do tempo; recortar a gravação em volta (`FuelPace.Replay trim … --from <st−60> --to <st+30>`); reproduzir com `FuelPace.Replay <recorte> --capture`.
  4. Transformar o caso num teste de regressão: o recorte vai para `tests/fixtures/regression/` se tiver < 5 MB (senão, fica fora do git com `SkipUnless`), e o teste confere o comportamento esperado.
  5. Corrigir, publicar um build novo no CI e pedir ao piloto para repetir o cenário.
  6. Registrar o caso em `docs/feedback/<data>.md` (o que aconteceu, a causa, o teste, o build da correção).
- `presentmon.ps1`: recebe `-Label com|sem -Minutes 5`, roda `PresentMon` (o caminho do exe é parâmetro; o piloto baixa do GitHub da Intel) filtrando `iRacingSim64DX11.exe`, e salva o CSV em `%APPDATA%\FuelPace\perf\` (o pacote de diagnóstico inclui essa pasta: ajustar o `DiagnosticBundle` e o teste do Task 5).
- `FuelPace.Replay presentmon`: fps médio, 1% low e a contagem de frames > 2× o frametime médio para cada CSV, mais o veredito dos limites do PERF-01 (≥ 99,5% e ≥ 99%).

- [ ] **Step 1: Teste que falha** `PresentMonCliTests`: dois CSVs sintéticos (60 fps estáveis vs. 59,5 com picos) → saída com `fps médio`, `1% low` e `PERF-01: REPROVADO`. Teste espelho com `APROVADO`.
- [ ] **Step 2–4:** falhar → implementar → passar. Escrever os dois documentos.
- [ ] **Step 5: Release de teste.** `git tag v0.1.0-test.1 && git push --tags` → o CI cria a Release com os dois zips. Expected: `gh release view v0.1.0-test.1` lista `FuelPace-…-win-x64.zip` e `…-debug.zip`.
- [ ] **Step 6: Commit** — `git commit -m "docs: windows testing guide, feedback loop and PERF-01 tooling"`
