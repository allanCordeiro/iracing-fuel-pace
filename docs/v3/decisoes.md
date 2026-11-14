# PRD v3: decisões (contexto → escolha → motivo)

Na Onda 1 cada frente registrou as suas em `docs/v3/decisoes-<frente>.md` (A, B, C, D) para não haver conflito de merge;
a integração (Onda 2) juntou tudo aqui e apagou os arquivos por frente. Uma decisão que a integração mudou leva a nota
"(integração: …)".

## Onda 0 (contratos)

- Branch base: v2.1 ainda não está na `main` (PR #1 aberto) → `feat/v3-glance` sai de `feat/fuelpace` e o PR final aponta para `feat/fuelpace` → o diff do PR mostra só a v3; se a `main` for a base desejada, basta trocar o alvo do PR.
- PRD §8.2 põe `GlanceLayout` no Core e reescreve o renderer → o app já tem layout independente de plataforma em `FuelPace.App/Overlay` (`OverlayLayout` → `DrawList`, testado no Linux) e renderers genéricos de `DrawList` (Direct2D, WPF, SVG) → `GlanceLayout.Build(GlanceView, OverlayViewOptions, ITextMeasurer, DrawList)` fica em `FuelPace.App/Overlay`; os renderers só ganham o comando novo `FillTriangle` (triângulo de saturação da barra).
- Entrada do builder → `GlanceViewBuilder.Build(in OverlayState, in GlanceConfig)`: o `OverlayState` do engine já traz o estado com a histerese de 1,5 s, e o builder só deriva valores de exibição → nenhuma fórmula do engine muda.
- O builder precisa do ritmo usado na autonomia (minutos do modo compacto) e da inclinação consumo × tempo (complemento em segundos) → o engine passa a expor `OverlayState.PaceS` e `OverlayState.FuelVsTimeB`, atribuições sem conta nova (§8.2 permite expor).
- `GlanceView` → `record struct` (igualdade por valor; NaN == NaN) para servir de dirty flag (§8.4); campos a mais em relação ao §8.1: `BarSteps` (Q, para o layout posicionar o marcador), `ShowAvg` (CFG-V3-04) e os tempos da linha de pace (§5.5); `short` −1 e `float` NaN = "—"; `BarAbsent` = `sbyte.MinValue`.
- Estado `NoTarget` da v2.1 (baseline sem limite ou sem referência) → layout compacto, como `SEM BASELINE` (§6.4: "o que não muda decisão some"). Carro elétrico → `Inactive` (o engine já devolve inativo).
- Remoção do gerador de texto de insight (`OverlayText.Insight`, `InsightKind` no `OverlayState`) → fica para a integração: o layout v2.1 ainda o usa até a troca pelo `GlanceLayout`. (integração: `OverlayText.Insight` saiu; o `Insight` do engine, valor e não texto, fica, porque alimenta o evento de diagnóstico `InsightChanged` do feedback loop.)
- Configurações: o código real fica em `FuelPace.App/Settings/AppSettings.cs` (não em `Core/Config`) → campos `ShowDeltaBar`, `DeltaBarScaleLpl`, `RedComplement` (`"pct"` | `"seconds"`), `ShowAvgInSupport`; o IPC transporta o `AppSettings` inteiro, então não precisa de mensagem nova.
- §8.3 (Inter 800) → `assets/fonts/Inter-ExtraBold.ttf` já está embutida e em uso (selo NÃO FACTÍVEL da v2.1) → nada a fazer.
- Mocks → as telas 9–12 e a tela 8 (Configurações) do canvas foram baixadas para `docs/v3/mocks/*.dc.html` (HTML em 2×), referência para o layout e para o revisor.

## Frente A (builder + NumberFormat)

- Half-cent: `Math.Round(2.385, 2, AwayFromZero)` dá 2.38 (o double é 2.38499…) mas o V3-4 exige 2.39 → o builder arredonda via `decimal` (15 dígitos significativos), que não aloca; NaN e |x| ≥ 1e9 usam `Math.Round` → o empate digitado vale como empate. Só difere do `Round2` do engine em empates de ponto flutuante, e a cor do marcador é independente da histerese por desenho (§5.2). Exemplo: VOLTA 2.385 com A 2.38 → o engine fica AZUL (double), o builder mostra VOLTA 2.39 e marcador +1; inalcançável com telemetria em float.
- `NumberFormat.Fixed` usava `ReadOnlySpan<string> formats = [...]` → trocado por `switch` sobre literais → não depende de como o compilador materializa a coleção (provavelmente inline array nas versões novas); o switch é garantidamente sem alocação.
- `NumberFormat.Fixed`/`SignedSeconds` com negativo que arredonda para zero (−0.001) → escreve "0.00" / "+0.0 s" sem sinal → "−0.00" seria ruído no overlay.
- Red sem referência (`LapProjectionL` e `LastLapL` ambos NaN, mesmo com `MeanL`) → `DecisionValue = NaN`, sem complemento, `Decision = Economize` mantido → PRD §5.1 (selo do estado atual e número `—`); o estado do engine ainda usa `MeanL` como referência.
- `ComplementPct` saturado em −127 (tipo `sbyte`) → valores absurdos de economia não estouram o tipo.
- `BarSteps` é calculado em todos os modos (depende só da configuração) → o dirty flag não muda à toa.
- `IsEstimate` copia `TargetIsEstimate` em qualquer modo (o engine só o liga com alvo) → literal do brief.
- `LapsRemaining` com ruído: `ceil(round(Lr, 6))`; NaN/infinito → −1; teto 999 em `LapsPossible`, `LapsRemaining` e `MinutesPossible`.

- Red durante a espera de 1,5 s (estado ainda VERMELHO, mas ref ≤ A) → `DecisionValue = 0.00` sem complemento (`ComplementPct = 0`, `ComplementSec = NaN`); o mínimo 1% só vale com economia > 0 → evita "ECONOMIZE 0.00 −1%" em toda transição de lift & coast.

## Frente B (GlanceLayout + Preview)

- Worktree da frente nasceu do commit inicial, não da `feat/v3-glance` → `reset --hard` do branch do worktree para `9b6ffbd` antes de começar → a base combinada da Onda 1 é `9b6ffbd`.
- Padding do painel: o PRD diz 7 × 9 e o mock tem 7 em cima e 8 embaixo → 7 nos dois → o PRD manda; altura completa 118 px (alvo 120 ± 6).
- Alinhamento vertical na F1 (mock: `align-items: center`) → selo, rótulo, número e complemento centrados no bloco; unidade `L` na linha de base do número → como o mock.
- `DecisionValue = NaN` → só o selo e `—` (sem rótulo, unidade nem complemento) → PRD §5.1 "a F1 mostra só o selo do estado atual e o número —".
- Trilho da barra com pontas arredondadas → duas metades arredondadas lado a lado; as pontas internas ficam sob a linha do alvo (o que sobra é < 0,5 px em 1×) → sem comando novo no `DrawList`.
- Triângulo de saturação → 8 × 16 px em 1×, encostado na ponta do trilho (dentro da margem de 3 px), na cor do lado → o PRD não dá a largura; 8 px ≈ a mesma massa visual do marcador de 6 px.
- Dentro do painel só uso `FillRoundedRect` (linha do alvo, fantasma e divisor com raio 0) → `OverlayEditGeometry.HitTest` trata todo `FillRect` como alça do modo edição; um `FillRect` no painel quebraria o arraste.
- F3 no modo compacto: o mock tem `VOLTA` em `#F2F4F7` e o brief diz `#D5DAE0` "PIT e compacto" → `#D5DAE0` → valores do brief (neutro, como no PIT).
- `alvo 2.16 ·` do PIT: o PRD diz 12 px e o brief/mock 11 px → 11 px → valor literal do brief. (revisão N4: 12 px, porque a §11.4 diz que o PRD prevalece; o painel PIT continua com 115 px e a F4 em duas linhas.)
- Linha da F4 no PIT não cabe numa linha em 300 px nos tamanhos do PRD (o mock da tela 11 quebra em duas) → (revisão, rodada 1, decisão do controlador) F4 do PIT em duas linhas no tamanho cheio, usando a altura que a barra ausente libera: linha 1 `74.0 L · dá 31 v` à esquerda e `alvo 2.16 · MÍN 2.23` à direita, linha 2 `faltam 34` (ou `stint N`) 6 px abaixo, com a mesma altura de linha → segue o arranjo da tela 11; painel PIT com 115 px (V3-7: 120 ± 6). O `·` que sobra no fim da linha 1 do mock sai, e `alvo 2.16 ·` fica numa linha só (no mock ele quebra por falta de espaço).
- Encolhimento da F4 → (revisão, rodada 2, refina a rodada 1) dois tamanhos só: tamanho cheio (12 px à esquerda, valor 15 px, × escala) sempre que a linha atual cabe; se não cabe, um único tamanho de reserva calculado contra o modelo mais largo de cada arranjo (`100.0 L · dá 99 v · faltam 999` + `ALVO ~2.30`; no PIT, `100.0 L · dá 99 v` + `alvo ~2.30 · MÍN 2.30`), nunca contra os valores da tela → os números dos mocks ficam no tamanho do PRD §4.1, e o tamanho só troca quando o conteúdo cruza o limite de caber (ex.: `faltam 999 → 99`; no vermelho, o `~` do caution ao lado de `faltam 32` já passa do limite), não por ruído de quadro a quadro. Reserva ≈ 10,9 px em 1× (fator 0,91 com as métricas fixas); o PIT cabe no modelo e fica sempre em 12 / 11 px. (revisão N4: com o `alvo` a 12 px, o modelo mais largo do PIT, `100.0 L` com `~`, deixa de caber e usa a própria reserva, ≈ 10,95 px em 1× com as métricas fixas, que também encolhe o `alvo`; os números do mock e qualquer PIT com `74.0 L` continuam em 12 px.)
- Barra → desenhada só com `ShowBar` **e** `Decision` ECONOMIZE ou OK → PRD §5.2 (a barra só existe no azul e no vermelho), mesmo que o builder mande `ShowBar` no PIT.
- Alças do modo edição no painel inativo → usam só a opacidade de conteúdo do usuário, sem o esmaecimento de 50% do inativo → o esmaecimento é do conteúdo, não do chrome (revisão, rodada 1).
- `~` do alvo estimado no PIT → vai no número cinza de `alvo ~2.16 ·`, não no `MÍN` → `MÍN` é o limite factível, que não é estimado.
- `dá N v` com `LapsPossible = -1` → `dá — v`; `MinutesPossible = -1` no compacto → a parte ` · N min` some → "o que não muda decisão some" (§3), e `— min` não informa nada. (revisão B1: no compacto e no inativo, `LapsPossible = -1` também faz `dá N v` sumir, e a primeira faixa fica só com o combustível; na F4 completa o `dá — v` fica, porque lá a MÉD cai na baseline e é sempre conhecida.)
- Linha de pace (§5.5) "no estilo da F3" → pares rótulo 8 px + valor 13 px: `ÚLT 79.51 s`, `MÉD 80.14 s`, `BASE 80.12` (o `BASE` some sem baseline), 6 px abaixo da F4 → mesmo estilo da F3; aparece no completo e no compacto, nunca no inativo (v2.1 também escondia no inativo).
- Pílulas: ordem da esquerda para a direita `DBG`, `REC`, `BoP≠`, `~ CAUTION` → o desenho do §4 põe REC antes de BoP e a v2.1 punha DBG à esquerda do REC; todas no texto `#F2F4F7`, exceto DBG em `#A9B2BD` (como na v2.1).
- Opacidade do conteúdo: o fundo colorido da F1 não é "conteúdo" (igual à faixa ALVO da v2.1); selo, textos, barra, divisor e ponto REC são → mantém a convenção da v2.1.
- Paleta e chrome de edição copiados para `GlanceTheme` e `GlanceLayout` (e `PanelOffset` próprio) → a integração apaga `OverlayLayout`/`OverlayTheme`; o `GlanceLayout` só depende de `OverlayViewOptions`, que mora em `OverlayLayout.cs` e precisa sobreviver à limpeza. (integração: `OverlayViewOptions` foi para `OverlayViewOptions.cs`, sem `ShowInsightLine`; `OverlayLayout`, `OverlayTheme` e `OverlayPreviews` saíram.)
- Teste "o Preview grava os arquivos esperados" sem referenciar o projeto da ferramenta → a escrita mora em `GlancePreviews.WriteAll(dir)` (App) e `tools/FuelPace.Preview` só a chama → o teste fica em `FuelPace.App.Tests` sem mexer no `.csproj` dele; um segundo teste confere que os SVGs versionados em `docs/v3/preview` batem com o gerador.
- `tools/FuelPace.Preview` deixa de gerar os SVGs da v2.1 (`docs/overlay-preview`) → o brief diz "passa a gravar esses SVGs"; os SVGs antigos ficam como estão até a integração apagar a v2.1. (integração: `docs/overlay-preview` apagada.)

## Frente C (renderers)

- Cache de layouts de texto → já existe em `D2DDrawListRenderer` (LRU de 64, chave texto+papel+peso+tamanho+espaçamento+itálico, liberado no Dispose; teste `Rendering_the_same_list_again_creates_no_text_layout`) → não mexer → já cumpre PRD §8.2.
- Pesos de fonte → `DWriteFonts` carrega um arquivo por peso (Inter 400/600/700/800, JetBrains Mono 400/500/600/700) e `Load` valida peso exato sem simulação; `Fonts_come_from_the_embedded_files_with_the_exact_weights` já cobre 600/700/800 e 500/600/700 → sem correção.
- Triângulo no Direct2D → `ID2D1PathGeometry` criada e liberada por comando (poucos por quadro, ≤ 10 redesenhos/s) → mais simples que cachear (PERF-06).
- Triângulo no WPF → `StreamGeometry` congelada por comando, mesmo padrão do pincel congelado já usado ali.
- Ramo de trabalho → o worktree partia de `main`; mesclei `feat/v3-glance` para obter o contrato da Onda 0 (`FillTriangle`).

## Frente D (configurações)

- Branch do worktree saiu da `main` → reset para `feat/v3-glance` antes de começar → os contratos da Onda 0 e a fixture v1-real só existem lá.
- `schemaVersion` ausente no JSON desserializa como o padrão (2) → a versão é lida do texto (`PeekSchemaVersion`) antes de decidir migrar → senão um v1 sem o campo nunca migraria.
- `schemaVersion` com tipo errado (texto) → continua indo para `.corrupt` com padrões → é o comportamento do binder para qualquer tipo errado; não vale um caso especial.
- Falha ao regravar o arquivo migrado (IO/acesso) → engole, devolve as configurações migradas em memória e tenta migrar de novo na próxima leitura → o arquivo original continua intacto e nada é perdido.
- `schemaVersion` > 2 → lido como está (campos desconhecidos ignorados), nunca regravado, `SchemaVersion` mantém o valor lido → um app mais novo não perde dados por causa deste.
- `Load` expõe `LastLoadMigratedFrom` (int?) e `LastLoadWasCorrupt` → o Agent loga na integração; ambos zerados a cada `Load`. (integração: logados; ver Onda 2.)
- Complemento do vermelho na UI → dois botões de rádio (`%` / `s/v`) ligados a `RedComplementSeconds` → mesmo padrão do modo de voltas já existente.
- Escala da barra → o texto `±0.20 L/v` usa InvariantCulture (ponto) → igual ao resto dos números da página (ruling A25); o campo aceita vírgula ou ponto via `NumberInput`.
- `OverlayViewOptions.ShowInsightLine` permanece (arquivo da frente B/integração) → a prévia e o `AgentHost` passam `false`. (integração: o campo saiu.)

## Onda 2 (integração)

- Onde o `GlanceView` nasce → como o brief recomenda: o `TelemetryPipeline` recebe a `GlanceConfig` (parâmetro opcional do construtor e `SetGlanceConfig`) e roda o `GlanceViewBuilder` logo depois do `FuelStrategyEngine.Compute`, na thread de estratégia (10 Hz); o `OverlayStatePublisher` guarda estado e view, e a `Version` só avança quando a view muda por valor → a dirty flag da v3 fica no mesmo lugar da v2.1, e o `RenderScheduler` não muda.
- Troca da `GlanceConfig` entre threads → a config (dois `double` e três `bool`) vai numa caixa imutável trocada por referência (`Volatile`) → não há leitura rasgada, e só a mudança de configuração aloca; o tick segue com 0 bytes (`HotPathAllocationTests`).
- Config padrão do pipeline sem `GlanceConfig` explícita (testes, replay, bench) → `GlanceConfig.Default` com a reserva da `StrategyConfig` → "dá N v" e folga/faltam sempre usam a reserva do engine.
- `AgentHost.Render` → constrói o `GlanceView` de novo a partir do `OverlayState` publicado (ou do `NoSim`) com a config das configurações atuais, e desenha com `GlanceLayout` → cobre o estado sem sim e uma mudança de configuração que nenhum tick viu ainda (o builder é barato e não aloca); a view publicada serve só de dirty flag. O host guarda `LastRenderedView` para os testes.
- Mapeamento configurações → `GlanceConfig` num lugar só → `AppSettings.ToGlanceConfig()` (`ReserveL = Strategy.ReserveL`, `BarScaleL = Overlay.DeltaBarScaleLpl`, `ComplementSeconds = RedComplement == Seconds`, `ShowBar = Overlay.ShowDeltaBar`, `ShowAvg = Overlay.ShowAvgInSupport`); `ApplySettings` chama `SetGlanceConfig` quando ela muda, e o `InvalidateView` que já existia pede o redesenho.
- `AutoHidePolicy` → continua com o estado do engine (`OverlayState.State`), sem mudança.
- `OverlayState.VisibleEquals` (dirty flag da v2.1) e os rótulos carro/pista do `OverlayStatePublisher` (só a linha de insight "Sem baseline para …" os usava) → saíram. `TelemetryPipeline.CarShort/TrackShort` ficam (testes da decisão 3 e diagnóstico).
- `OverlayText` → só o `Insight` (gerador da §7.5) saiu; `Liters`, `PerLap`, `Laps`, `Minutes` e `Seconds` ficam porque o `FuelPace.Replay` imprime com eles. O replay passa a imprimir o bloco de decisão da v3 (`ECONOMIZE 0.08 L/v −3%`, `OK folga 2.7 L`, `PIT faltam 2.3 L`) no lugar da linha de insight.
- Log da migração (pendência da frente D) → o `Program.cs` continua carregando antes do host (precisa do backend para criar a janela), e passa o que esse load fez em `AgentOptions.EarlierSettingsLoad` (`SettingsLoadReport`: `MigratedFrom`, `WasCorrupt`); o host combina com o próprio load e loga `settings`, Info "settings.json migrated from schema 1 to 2" e, se corrompido, Warn → o load que migra é o do `Program.cs`, e o do host já encontra o arquivo na versão 2. O re-load depois de um fallback (arquivo travado) também loga.
- Prévia do Settings → o `GlanceViewBuilder` sobre a entrada V3-3 do `azul` (`GlancePreviews.Inputs`), com a config da seção Layout (barra, escala, complemento, MÉD) e a reserva do mock (0,5) → a prévia usa o mesmo builder do overlay, então a escala da barra move o marcador de verdade; a reserva não é opção de Layout e mudaria os números do mock.
- Entradas V3-3 em código de produção → `GlancePreviews.Inputs` e `GlancePreviews.SampleConfig` (no App) → a prévia do Settings precisa delas; as views continuam escritas à mão, e o teste confere builder(entrada) == view, campo a campo.
- Views à mão que não batiam com o builder → `MinutesPossible` das views completas passou de 41 para −1 (o builder só calcula os minutos para o layout compacto, o único que os mostra) e o `compacto` ganhou `BarSteps = 20` (o builder calcula Q em todos os modos) → nenhum dos dois é desenhado; os SVGs não mudaram.
- `OverlayLayoutTests` (v2.1) → apagado; o que era genérico e não tinha par no `GlanceLayoutTests` migrou para lá (textos do rodapé e da faixa do modo edição, cabeçalho e fim do SVG). `OverlayEditGeometryTests` e `OverlayPlacementTests` passam a usar `GlanceLayout`/`GlancePreviews`.
- `RenderTests.Renders_all_states_to_png` (Windows) → a amostra de cor da faixa ALVO da v2.1 vira duas: o fundo do painel (padding de cima, todo preview) e o fundo do bloco F1 na cor da decisão (previews no modo completo), mesma tolerância de 8 → mantém o que o teste verifica (a cor do estado chega ao pixel) no desenho novo.
- PERF-06 com a fixture → `AgentHostFixtureTests`: o Agent inteiro com o `FakeSource` (relógio manual, 1/60 s por quadro, ou seja 1× no tempo dos dados), `AutoHide` desligado para o painel inativo ficar na tela e os redesenhos contarem; "parado no box" = os quadros iniciais com `PlayerCarInPitStall` e velocidade < 0,1 m/s (301 na fixture); redesenho = `IOverlaySurface.Render` (a lista mudou de `ContentHash`). Resultado local: 0 redesenhos parado, 1591 em 785 s (≈ 2,0/s). A fixture entra no `FuelPace.App.Tests` pelo mesmo `Fixtures.cs` dos testes do Core (link), com o mesmo pulo quando falta.
- A `Version` também anda por campos que o modo atual não desenha (ex.: `LapsRemaining` no inativo) → aceito: o render roda no UI thread, mas a lista sai com o mesmo `ContentHash` e não chega ao renderer; esconder esses campos no builder mudaria a frente A sem ganho medido.

## Onda 3 (correções da revisão)

- B1, INATIVO sem autonomia → o engine atribui `MeanFromBaseline`, `MeanL` e `PaceS` (as mesmas expressões, só movidas para antes das duas saídas antecipadas do caminho não elétrico: `Inactive` e `L_rest ≤ 0`); `AutonomyLaps`, `AutonomyMin`, alvo, estado e insight não mudam → §6.5 pede COMB e autonomia no inativo e §8.2 permite expor sem conta nova; o carro elétrico continua sem nada (nem o combustível, como na v2.1).
- B1, teste da v2.1 `Inactive_shows_only_fuel` (afirmava `MeanL = NaN`) → virou `Inactive_shows_only_fuel_and_the_autonomy_inputs`, que afirma a média e o ritmo expostos e o resto NaN → o que ele protegia na v2.1 era o visual "só combustível", que a §6.5 da v3 substitui; não é fórmula, então não é bloqueio (§11.1 item 2c).
- B1, efeito colateral aceito → no inativo o `refL` dos eventos de diagnóstico `StateChanged`/`OverlayVersion` passa a ser a MÉD em vez de NaN, e a `Version` anda quando a MÉD muda no inativo (raro: parado, a média do stint não muda) → só diagnóstico; o `ContentHash` segue barrando redesenho à toa.
- B1, prévia `inativo` → montada pelo builder a partir de `GlancePreviews.InactiveInput` (box durante a corrida dos mocks: C 74.0, MÉD do stint 2.37 a 80.14 s → `dá 31 v · 41 min`), e um teste confere que o engine real gera exatamente esse `OverlayState` → a prévia mostra o que o app desenha; os números da corrida (e não os do treino da tela 12) porque o box é o caso comum do INATIVO.
- B1, ordem dos inicializadores estáticos de `GlancePreviews` → `All` passou para depois de `SampleConfig`, `Inputs` e `InactiveInput` → o C# roda os inicializadores na ordem do texto, e `Build()` agora lê dois deles.
- N2, combustível abaixo da reserva → `LapsPossible = 0` (`dá 0 v`, e no compacto `0 min`) quando a MÉD é conhecida e positiva; MÉD ou combustível desconhecidos (ou MÉD 0, que daria infinito) continuam −1 → o `0` diz ao piloto que não sobra volta justamente quando importa.
- N3, complemento em segundos que arredonda para `+0.0 s` (|s| < 0,05, por exemplo 0,01 L × 2 s/L) → o builder deixa `ComplementSec = NaN` e o layout mostra o `%` (mínimo −1%) → mesma volta para `%` do caso sem `fuelVsTime`; `0.05` arredonda para `0.1` (meia unidade para longe do zero) e fica em segundos.
- N4, `alvo {A} ·` do PIT → 12 px (`PitTargetFont`), como a §5.4 → a §11.4 manda o PRD prevalecer sobre o mock; o arranjo em duas linhas e a regra de dois tamanhos da F4 ficam, e só o modelo mais largo do PIT passa a cair no tamanho de reserva.
