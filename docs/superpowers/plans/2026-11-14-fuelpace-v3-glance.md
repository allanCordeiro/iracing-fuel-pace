# FuelPace v3 (leitura rápida): plano de execução

**Spec:** `docs/PRD-v3-leitura-rapida.md` (o PRD v3; delta sobre `docs/PRD.md` v2.1). **Decisões da Onda 0:** `docs/v3/decisoes.md`.
**Branch:** `feat/v3-glance` (Onda 0 = commit `654f946`). **Mocks:** `docs/v3/mocks/*.dc.html` (HTML em 2×: divida os px por 2).

## Global Constraints (valem para todas as tasks)

- **Autonomia (PRD §11.1):** não pergunte. Diante de ambiguidade, escolha o mais coerente com o PRD v3 e a v2.1 e registre
  uma linha `contexto → escolha → motivo` em `docs/v3/decisoes-<frente>.md` (A, B, C ou D). Precisou mudar um contrato da
  Onda 0? Mude o mínimo e anote em `docs/v3/contratos-pendentes-<frente>.md`. Pare só nos casos do PRD §11.1 item 2.
- **Posse de arquivos:** só altere os arquivos listados na sua task. Os contratos da Onda 0 (`src/FuelPace.Core/Display/GlanceView.cs`,
  `GlanceConfig.cs`, `DrawList.cs`) só mudam com nota em contratos-pendentes.
- **TDD:** cada critério V3 da sua task vira teste antes do código; veja o teste falhar, depois implemente.
- **Ambiente:** Linux; `export PATH=$HOME/.dotnet:$PATH`. Antes de reportar: `dotnet build FuelPace.sln` (0 avisos:
  `TreatWarningsAsErrors`) e `dotnet test FuelPace.Linux.slnf` verdes. O CI também roda tudo em windows-latest: nada que
  dependa de Linux (separador de caminho, `\n`, case de arquivo, granularidade de Sleep).
- **Partes só-Windows** (Direct2D, WPF) precisam compilar no Linux; testes de comportamento delas vão em
  `tests/FuelPace.Windows.Tests` (rodam só no CI Windows).
- **Desempenho:** builder, layout e formatação numérica com **zero alocação** depois do aquecimento (PERF-03); sem LINQ,
  string nova, boxing ou async nesses caminhos.
- **Engine intocado:** nenhuma fórmula do `FuelStrategyEngine` muda.
- **Textos:** UI em pt-BR; código e comentários em inglês, no estilo do repositório (comentários explicam o porquê).
- **Commits:** pequenos, Conventional Commits (`feat(display): …`, `test(layout): …`), terminando com a linha
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Não faça push. Não abra browser, não instale ferramentas, não rode o iRacing.

## Task 1: Frente A · GlanceViewBuilder + NumberFormat

**Arquivos:** `src/FuelPace.Core/Display/GlanceViewBuilder.cs`, `src/FuelPace.Core/Display/NumberFormat.cs`,
`tests/FuelPace.Tests/Display/*` (novos), `tests/FuelPace.Tests/Perf/HotPathAllocationTests.cs` (só acrescentar casos),
`docs/v3/decisoes-A.md`, `docs/v3/contratos-pendentes-A.md`.

**Entrada:** `GlanceViewBuilder.Build(in OverlayState s, in GlanceConfig cfg)`. O `OverlayState` é a saída do engine, já com
a histerese de 1,5 s em `s.State`. Leia `src/FuelPace.Core/Strategy/FuelStrategyEngine.cs` e `OverlayState.cs` para saber
o que cada campo significa (NaN = indisponível). Spec: PRD v3 §5 e §8.4; critérios V3-1 a V3-6 e V3-8 (alocação).

**Regras (notação do PRD §5):** `res = cfg.ReserveL`, `C = s.FuelL`, `Lr = s.LapsRemaining`, `lim = s.FeasibleMinL`,
`A = R2(s.TargetL)`, `ref = R2(primeiro não-NaN de s.LapProjectionL, s.LastLapL, s.MeanL)` (a mesma escolha do engine),
`R2/R1 = Math.Round(x, 2|1, MidpointRounding.AwayFromZero)`.

- **Modo:** `Inactive` → `GlanceMode.Inactive`; `NoBaseline` e `NoTarget` → `Compact`; `Blue`, `Red`, `NotFeasible` → `Full`.
- **F1 (só Full):** `Red` → `Economize`, `DecisionValue = R2(max(0, ref − A))`, `ComplementPct = −max(1, round(100 × valor / ref))`;
  com `cfg.ComplementSeconds` e `s.FuelVsTimeB` não-NaN, `ComplementSec = R1(−FuelVsTimeB × valor)` (a mesma conta do insight
  "Save" do engine), senão NaN. `Blue` → `Ok`, `DecisionValue = R1(max(0, (C − res) − Lr × ref))`. `NotFeasible` → `Pit`,
  `DecisionValue = R1(max(0, Lr × lim − (C − res)))`. Se `s.LapProjectionL` e `s.LastLapL` forem ambos NaN, `DecisionValue = NaN`
  e sem complemento (PRD §5.1). Fora do Full: `Decision = None`, `DecisionValue = NaN`.
- **F2:** `ShowBar = cfg.ShowBar && estado ∈ {Blue, Red}`; `BarSteps = Q = round(cfg.BarScaleL / 0.01)` (limitado a 5..50).
  Marcador: `clamp(round((R2(s.LapProjectionL) − A) × 100), −Q, Q)`; ausente (`GlanceView.BarAbsent`) se a projeção ou o alvo
  for NaN. Fantasma: o mesmo com `s.LastLapL`; ausente se NaN. Sem barra → os dois ausentes. Calcule em centésimos inteiros
  para não errar por ponto flutuante (V3-4: `VOLTA = 2.385` → 2.39 → passo +1 com `A = 2.38`).
- **F3:** `LapProj/LapLast/LapAvg = R2` dos campos; `LapLastInvalid`, `LapAvgFromBaseline = s.MeanFromBaseline`, `ShowAvg = cfg.ShowAvg`.
- **F4:** `Fuel = R1(C)`; `LapsPossible = floor((C − res) / s.MeanL)` (−1 se NaN, infinito ou negativo; limite 999);
  `LapsRemaining = ceil(Lr)` (−1 se NaN; absorva ruído de ponto flutuante, por exemplo `ceil(round(Lr, 6))`; limite 999);
  `IsStint = s.IsStintMode`. Alvo: `Red` → `TargetLabel.Alvo`, `Blue` → `Ate`, `NotFeasible` → `PitMin` com
  `FeasibleMin = R2(lim)`; `Target = A` nos três; Compact/Inactive → `None` e NaN. `IsEstimate = s.TargetIsEstimate`.
- **Compact/Inactive:** `MinutesPossible = round(LapsPossible × s.PaceS / 60)` quando os dois existem, senão −1 (só nesses
  modos, para não gerar redesenho à toa no Full).
- **Pílulas:** `Rec` se `s.Recording`; `BopMismatch` se `s.BopMismatch`; `Caution` se `s.TargetIsEstimate`. `RecValid/RecTotal`.
- **Linha de pace:** `LastLapTimeS/MeanLapTimeS/BaselineLapTimeS = R2` (NaN passa).
- Zero alocação. Todos os floats já arredondados como exibidos (a igualdade do `GlanceView` é a dirty flag).

**Testes:** V3-1, V3-2, V3-3 e V3-4 com `OverlayState` montado à mão nos valores do PRD (o `State` coerente com cada linha)
e também pelo engine real: monte uma `Baseline` (record em `src/FuelPace.Core/Baselines/Baseline.cs`; veja como
`tests/FuelPace.Tests/Strategy/FuelStrategyEngineTests.cs` cria uma) com `FuelMeanL`/`FuelMinL` que deem `lim = 2.23`
(`lim = min(FuelMeanL × 0.94, FuelMinL)`) e confirme que o engine chega aos estados AZUL/VERMELHO/NÃO FACTÍVEL do V3-1.
V3-5 pelo engine com `SignalHysteresis`: VERMELHO → AZUL; o sinal do `BarMarkerQ` (azul se ≤ 0) muda no mesmo tick e o
`Decision` só depois de 1,5 s estável. V3-6: `TargetIsEstimate` → pílula `Caution` e `IsEstimate`. `NumberFormat`: NaN →
"—", negativos com U+2212, `Count(−1)` → "—", `Percent(−3)` → "−3%", `SignedSeconds(0.9)` → "+0.9 s"; corrija o que achar de
errado na implementação da Onda 0. V3-8: em `HotPathAllocationTests`, 10.000 chamadas do builder e da formatação com
0 bytes (siga o padrão `GC.GetAllocatedBytesForCurrentThread` que já existe ali).

## Task 2: Frente B · GlanceLayout + Preview

**Arquivos:** `src/FuelPace.App/Overlay/GlanceLayout.cs`, `src/FuelPace.App/Overlay/GlanceTheme.cs` (novo: paleta v3),
`src/FuelPace.App/Overlay/GlancePreviews.cs` (novo), `src/FuelPace.App/Overlay/SvgRenderer.cs` (se precisar),
`tools/FuelPace.Preview/**`, `tests/FuelPace.App.Tests/Overlay/Glance*Tests.cs` (novos), `docs/v3/preview/*.svg`,
`docs/v3/decisoes-B.md`, `docs/v3/contratos-pendentes-B.md`. **Não** altere `OverlayLayout.cs`, `OverlayTheme.cs` nem
`OverlayPreviews.cs` (a integração apaga o layout v2.1); o que precisar deles, copie.

**Entrada:** `GlanceLayout.Build(in GlanceView v, OverlayViewOptions o, ITextMeasurer m, DrawList dest)` (ignora
`o.ShowInsightLine`). Siga o padrão de `OverlayLayout.cs`: tudo do painel escala por `Scale × DpiScale`; o chrome do modo
edição (contorno tracejado, alças, faixa "MODO EDIÇÃO", rodapé `X · Y · escala`) só por `DpiScale`, igual à v2.1; a caixa
de edição envolve o painel e a linha de pílulas (PRD §6.6). `dest.Size` cobre tudo. Spec: PRD v3 §4, §5.3, §5.4, §6;
critérios V3-7 e V3-10. Medidas finas: os mocks em `docs/v3/mocks/V3Vermelho|V3Azul|V3Pit|V3Treino.dc.html` (2×; o painel
de 600 px = 300 px em 1×). Onde o PRD e o mock divergem, o PRD manda.

**Geometria em 1× (PRD §4.1, mocks ÷ 2):** painel 300 px, padding 7 (vertical) × 9 (horizontal), raio 6, gap de 6 entre
faixas; fundo `OverlayTheme.Panel(BackgroundOpacity)`. **F1:** bloco com fundo na cor do estado (raio 4, padding 5 × 7),
selo (raio 3, padding 4 × 7, texto 11 px Inter 800, espaçamento 0.08em; branco sobre `#C7362F`/`#1E6BD6`, `#0E1013` sobre
`#F2F4F7` no PIT), rótulo `FOLGA`/`FALTAM` 11 px 600 (0.06em; `#CFE2FF` azul, `#D5DAE0` PIT), número 25 px mono 700, unidade
`L` 12 px (cor do rótulo), complemento à direita 15 px mono 600 `#FFD9D9` (`−3%` ou `+0.9 s`), gap 9. **F2:** altura 18,
margem lateral 3; trilho de 4 px centrado (metade esquerda `rgba(47,128,237,0.42)`, direita `rgba(235,87,87,0.42)`, pontas
arredondadas), linha do alvo de 1,5 px na altura toda `#F2F4F7`, fantasma 1,5 × 12 `rgba(242,244,247,0.55)`, marcador 6 × 16
(raio 1,5) em `x = centro + q / Q × meia-largura`, azul `#6AAEFF` se q ≤ 0, vermelho `#FF7A7A` se q > 0; com `|q| = Q`, o
marcador vira triângulo (`DrawList.FillTriangle`) encostado na ponta, apontando para fora. **F3:** rótulos 8 px 600
(0.1em, `#A9B2BD`), números 13 px mono (VOLTA 600, os outros 500), 5 px rótulo → número, ~17 px entre grupos; VOLTA e ÚLT no
tom claro do estado (`#FF7A7A`/`#FFA8A8` vermelho, `#6AAEFF`/`#9CC8FF` azul, `#D5DAE0` PIT e compacto), MÉD `#D5DAE0`
(itálico se `LapAvgFromBaseline`, some se `!ShowAvg`), `*` depois de ÚLT inválida. **Divisor** 1 px
`rgba(169,178,189,0.22)` com 4 px acima da F4. **F4:** à esquerda, mono 12 px `#D5DAE0`: combustível em 600 `#F2F4F7`, depois
` L · dá N v · faltam N` (`stint N` com `IsStint`); à direita rótulo 8 px (`ALVO`/`ATÉ`/`MÍN`) + valor 15 px mono 700
`#F2F4F7`, `~` antes do valor com `IsEstimate`; no PIT, antes do rótulo, `alvo 2.16 ·` 11 px mono `#A9B2BD`.
**Compacto (§6.4):** combustível 21 px mono 600 + `L` 11 px `#A9B2BD`; à direita `dá 25 v · 45 min` 13 px mono `#D5DAE0`;
divisor; F3. **Inativo (§6.5):** só a primeira faixa do compacto, conteúdo a 50%. **Pílulas (§4.3):** abaixo do painel,
alinhadas à direita, gap 4, fundo do painel, raio 5, padding 4 × 7, texto 10 px mono 600: `REC 4/9` com ponto `#FF5A5A`
de 6 px à esquerda, `BoP≠`, `~ CAUTION`, e o `DBG` da v2.1 (`o.DebugTag`). Sem pílulas, a linha não ocupa espaço.
**Linha de pace** (`o.ShowPaceLine`, §5.5): abaixo da F4 no estilo da F3 (`ÚLT 79.51 s · MÉD 80.14 s · base 80.12`).
Formate números com `FuelPace.Core.Display.NumberFormat` (a frente A mexe nele; use só as assinaturas atuais).

**Previews:** `GlancePreviews.All` com `GlanceView` montados à mão nos valores do V3-3: `vermelho`, `azul`, `pit`, `compacto`
(com REC 4/9), mais `inativo`, `saturado` (marcador em +Q, pílulas REC, BoP≠ e ~ CAUTION, alvo `~`), `sem-barra`
(`ShowBar = false`), `edicao` (escala 2, modo edição) e `escala-250`. `tools/FuelPace.Preview` passa a gravar esses SVGs
em `docs/v3/preview/` (padrão; `args[0]` troca a pasta), com o cenário de fundo do `SvgRenderer`. Gere e versione os SVGs.

**Testes (V3-7, com `FixedMetricsMeasurer`):** painel completo 300 ± 4 × 120 ± 6 px (o retângulo do painel, sem pílulas);
compacto 300 de largura e ≤ 70 de altura; `ShowBar = false` reduz a altura em 18 + 6 px; com os valores mais largos
(`ECONOMIZE 0.50 −20%`, `faltam 999`, `100.0 L`, `dá 99 v`, alvo `~2.30`) nenhum texto passa da borda interna do painel;
escala 2,5 proporcional (±1 px por elemento); o triângulo aparece só com `|q| = Q`; sem pílulas a altura não muda; layout
sem alocação depois do aquecimento (veja como os testes de `OverlayLayout` medem); o Preview grava os arquivos esperados.

## Task 3: Frente C · Renderers (triângulo, fontes, cache de texto)

**Arquivos:** `src/FuelPace.Agent/Overlay/**` (principalmente `D2DDrawListRenderer.cs`, `DWriteFonts.cs`),
`src/FuelPace.Settings/WpfDrawListRenderer.cs`, `tests/FuelPace.Windows.Tests/RenderTests.cs` (só acrescentar),
`docs/v3/decisoes-C.md`, `docs/v3/contratos-pendentes-C.md`.

**Contexto:** a v3 não reescreve o renderer (decisão da Onda 0): o Agent desenha um `DrawList` genérico
(`src/FuelPace.App/Overlay/DrawList.cs`) com Direct2D/DirectWrite (`D2DDrawListRenderer`, usado pelos backends composition e
layered), e o Settings desenha a prévia com WPF (`WpfDrawListRenderer`). A Onda 0 criou `CommandKind.FillTriangle`
(`DrawList.FillTriangle(RectF, bool pointsRight, Rgba)`: triângulo isósceles dentro do retângulo, ponta no meio da borda
direita se `Radius > 0`, da esquerda senão; o `SvgRenderer` já desenha, use-o como referência).

**Entrega:** (1) `FillTriangle` nos dois renderers (Direct2D: geometria de caminho; WPF: `StreamGeometry`), com as mesmas
regras de cor e opacidade dos outros comandos; sem vazar objetos COM (libere ou reutilize). (2) Confira em `DWriteFonts` que os
pesos usados pela v3 resolvem para as faces embutidas certas: Inter 600/700/800 (o selo usa 800 = `Inter-ExtraBold.ttf`),
JetBrains Mono 500/600/700. Corrija se algum cair em outra face. (3) PRD §8.2 pede um `IDWriteTextLayout` em cache por campo:
veja como o renderer cria os layouts de texto hoje. Se cria um por comando a cada quadro, faça um cache pequeno e limitado
(chave: texto + papel + peso + tamanho + espaçamento + itálico) que reaproveite o layout quando o texto não mudou, liberado no
Dispose e quando a fábrica é recriada. Se já há cache equivalente, registre isso e não mexa. Os redesenhos são ≤ 10/s
(PERF-06), então simplicidade vale mais que micro-otimização. (4) Em `RenderTests.cs` (Windows), um teste que desenha um
`FillTriangle` para cada lado e confere pixels da ponta e da base no bitmap (siga o padrão dos testes existentes ali).
Precisa compilar no Linux (`dotnet build FuelPace.sln`); os testes Windows rodam só no CI.

## Task 4: Frente D · Configurações v3 (schema 2, migração, seção Layout)

**Arquivos:** `src/FuelPace.App/Settings/AppSettings.cs`, `src/FuelPace.App/Settings/SettingsStore.cs`,
`src/FuelPace.App/SettingsUi/ViewModels/OverlayViewModel.cs`, `src/FuelPace.Settings/Views/OverlayPage.xaml` (e `.xaml.cs`),
`tests/FuelPace.App.Tests/Settings/*`, `tests/FuelPace.App.Tests/SettingsUi/OverlayViewModelTests.cs`,
`tests/FuelPace.Windows.Tests/Settings*.cs` (só o que citar a linha de insight), `tests/fixtures/settings/*`, uma linha em
`src/FuelPace.App/Host/AgentHost.cs` (onde lê `ShowInsightLine`: passe `false`), `docs/v3/decisoes-D.md`,
`docs/v3/contratos-pendentes-D.md`. **Não** altere `WpfDrawListRenderer.cs` (frente C) nem o layout do overlay.

**Spec:** PRD v3 §7 e §9; critério V3-9. A Onda 0 já pôs em `OverlaySettings` os campos `ShowDeltaBar` (true),
`DeltaBarScaleLpl` (0.20), `RedComplement` (`Pct` | `Seconds`, JSON `"pct"` | `"seconds"`) e `ShowAvgInSupport` (true).

**Entrega:** (1) `AppSettings.SchemaVersion` passa a 2; `OverlaySettings.ShowInsightLine` sai (um `showInsightLine` num
arquivo antigo é ignorado na leitura). `Normalize`: `DeltaBarScaleLpl` limitado a 0.05–0.50 (NaN → 0.20, arredondado a 0,01),
`RedComplement` indefinido → `Pct`. (2) Migração no `SettingsStore.Load`: arquivo lido sem erro com `schemaVersion` < 2 (ou
ausente) → valores preservados, campos novos com padrão, `SchemaVersion = 2`, gravado de volta de forma atômica
(`AtomicFile`, como o `Save`). Nunca grave em cima de um arquivo que não pôde ser lido (`LastLoadWasFallback`). Arquivo
corrompido continua indo para `.corrupt` com padrões. Exponha o que aconteceu (por exemplo `LastLoadMigratedFrom` e
`LastLoadWasCorrupt`) para o Agent logar; a integração liga o log. Um `schemaVersion` maior que 2 é lido como está, sem
regravar. (3) V3-9: `tests/fixtures/settings/v1-real.json` é um `settings.json` v1 real do app atual (do pacote de
diagnóstico do piloto): teste que ele migra para v2 preservando monitor, posição, escala, opacidades, `autoHide`, estratégia
e atalhos, e que o arquivo regravado tem `schemaVersion: 2`, os campos novos e não tem `showInsightLine`. (4) Settings,
"Overlay e cálculo": sai a caixa "Linha de insight"; entra uma seção **LAYOUT** (mesmo estilo de títulos das seções da página)
com: "Barra de delta" (caixa), "Escala da barra" (slider 0,05–0,50 passo 0,01 + campo, mostrando `±0.20 L/v`; desabilitada
sem barra), "Complemento do vermelho" (`%` ou `s/v`, com a ajuda "s/v precisa de uma baseline com ajuste consumo × tempo;
sem ele, mostra %") e "Mostrar MÉD" (caixa). Siga os padrões do `OverlayViewModel` (edições com debounce, `Re(...)` para a
edição pendente, `NumberInput` aceita vírgula ou ponto) e os testes existentes em `OverlayViewModelTests.cs`. Onde o
view model monta `OverlayViewOptions` para a prévia, passe `false` no `ShowInsightLine` (a integração troca a prévia
pelo layout v3).
