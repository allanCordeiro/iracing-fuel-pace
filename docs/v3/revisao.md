# PRD v3: revisão independente (Onda 3)

Revisor com contexto limpo. Base: `docs/PRD-v3-leitura-rapida.md`, v2.1 (`docs/PRD.md` §7.4, §8, §10.0, §11),
`docs/v3/decisoes.md`, o diff `fbba179..1bc4825` (código e testes), as prévias renderizadas em 2× contra os mocks das
telas 9–12, `docs/perf/v3-bench.md` e a lista de itens adiados do ledger. Nenhum teste foi rodado: nenhuma dúvida
precisou disso, e a leitura do código bastou para o único bloqueante.

## Veredito geral

**Aprovado com ressalvas.** O branch não pode ir para o merge sem corrigir o **B1**, que é pequeno.

O núcleo está sólido. O `FuelStrategyEngine` ganhou só duas atribuições (`PaceS`, `FuelVsTimeB`), sem conta nova.
O builder segue as fórmulas da §5. A dirty flag é o `GlanceView` comparado por valor. Builder, formatação, layout e
tick não alocam nada (com teste). A migração do `settings.json` cumpre a §9. As quatro telas dos mocks saem fiéis.
O defeito encontrado está no estado INATIVO: o app nunca mostra a autonomia que a §6.5 pede, e mostra um `—` que a
§3 manda sumir. A prévia `inativo` esconde isso, porque desenha números que o builder nunca produz.

## Critérios V3 → testes

| Critério | Teste(s) | Status |
|---|---|---|
| V3-1 builder com a baseline da fixture | `GlanceViewBuilderTests.V3_1_blue_L19_slack_saturated_left`, `V3_1_red_L22_save_013_5pct_marker_plus13`, `V3_1_not_feasible_L23_no_bar_pit_faltam_1_8`, `V3_1_through_the_engine` | ok |
| V3-2 igualdade e folga zero | `V3_2_equality_and_zero_slack_through_the_engine`, `V3_2_hand_made_zero_slack` (o passo 0 é conferido pelo fantasma, porque sem VOLTA o marcador some pela §5.2; o passo 0 do marcador está em `V3_4_marker_exactly_on_target_is_step_zero`) | ok |
| V3-3 números dos mocks | `V3_3_red_mock`, `V3_3_blue_mock`, `V3_3_pit_mock`, `V3_3_no_baseline_is_compact`, `GlancePreviewsTests.The_builder_makes_the_mock_views_from_the_V3_3_inputs` | ok |
| V3-4 fronteiras da barra | `V3_4_no_projection_marker_absent_ghost_present`, `V3_4_no_last_lap_ghost_absent`, `V3_4_marker_exactly_on_target_is_step_zero`, `V3_4_half_cent_rounds_up_to_plus_one`; cor: `GlanceLayoutTests.Marker_color_follows_its_side` (q 0 → azul, q 1 → vermelho), `Absent_marker_and_ghost_are_not_drawn` | ok |
| V3-5 histerese × marcador | `V3_5_marker_sign_flips_same_tick_decision_after_1_5s` (pelo engine) + `Marker_color_follows_its_side` | ok |
| V3-6 caution | `V3_6_caution_estimate`, `V3_6_through_the_engine_frozen_leader_pace`, `GlanceLayoutTests.Estimate_gets_a_tilde_and_stint_replaces_faltam`, `Pills_sit_under_the_panel_right_aligned_in_order` | ok |
| V3-7 layout | `Full_panel_is_about_300_by_120` (118 px), `Compact_panel_is_300_wide_and_at_most_70_tall` (59 px), `Hiding_the_bar_removes_its_height_and_one_gap`, `Widest_values_stay_inside_the_panel_and_do_not_overlap`, `Scale_2_5_is_proportional_within_1px_per_element` | ok (só com métricas estimadas, ver N10) |
| V3-8 desempenho | `HotPathAllocationTests.Glance_builder_and_number_format_allocate_zero_bytes` (10 000 chamadas), `Pipeline_allocates_zero_bytes_per_tick_*`, `GlanceLayoutTests.Build_allocates_nothing_after_warmup`, `OverlayStatePublisherTests.Version_moves_only_when_the_view_changes_by_value`, `AgentHostFixtureTests.Fixture_at_1x_averages_at_most_10_redraws_per_second_and_none_while_parked_in_the_box`; bench +4,4 %, 0 B | ok (a PERF-06 com a fixture só roda localmente, ver N6) |
| V3-9 migração | `SettingsStoreTests.V3_9_real_v1_file_migrates_to_v2_preserving_the_pilots_settings`, `A_failed_migration_write_keeps_the_original_file_and_returns_the_migrated_settings`, `A_file_that_could_not_be_read_is_never_written_over`, `Corruption_is_reported_and_migration_status_is_reset_on_every_load`, `AgentHostTests.A_schema_1_file_migrated_by_the_host_is_logged`, `A_migration_done_by_the_earlier_load_is_logged`, `A_corrupt_file_found_by_the_earlier_load_is_a_warning` | ok |
| V3-10 preview | `GlancePreviewsTests.Previews_use_the_V3_3_numbers`, `Preview_writes_one_svg_per_preview`, `Committed_previews_match_the_generator` + inspeção visual (abaixo) | ok para os 4 SVGs exigidos; a prévia extra `inativo` é irreal (B1) |
| V3-11 manual no Windows | roteiro em `docs/windows-testing.md` | manual |

Fórmulas da §5 conferidas no `GlanceViewBuilder`:
- ECONOMIZE: `ref − A`, com `p = round(100·(ref − A)/ref)` e mínimo de 1.
- FOLGA: `max(0, (C − res) − Lr·ref)`.
- FALTAM: `Lr·lim − (C − res)`.
- Barra: passos de 0,01, `Q = round(S/0,01)`, saturação em ±Q.
- Autonomia: `dá N v = floor((C − res)/MÉD)` e `faltam = ceil(Lr)`.
- Complemento em segundos: `−b·economia`, que volta para `%` sem ajuste.

Todas batem. A única mudança no engine são as duas atribuições `s.PaceS = myPace` e `s.FuelVsTimeB = …` (mais os NaN
em `Blank`).

Na ligação com o resto do app também não achei problema:
- O pipeline monta a view logo depois do `Compute`, na thread de estratégia.
- O `AgentHost.Render` desenha a v3 a partir do estado publicado, com a config atual.
- Uma mudança de Layout redesenha sem precisar de tick (`A_layout_option_change_redraws_without_new_telemetry`).
- A prévia do Settings usa o mesmo builder.
- O modo edição envolve o painel e as pílulas. A geometria de edição continua testada.
- O auto-hide não mudou.
- Não sobrou referência a `OverlayLayout`, `OverlayTheme`, `OverlayPreviews`, `ShowInsightLine` nem `VisibleEquals`
  em `src/`, `tests/` e `tools/`.
- Os testes do Windows continuam conferindo a cor do estado no pixel: fundo do painel mais bloco F1, no lugar da faixa
  ALVO.

## Fidelidade visual (prévias 2× × mocks 2×)

| Tela | Resultado |
|---|---|
| 9 · vermelho | Fiel: selo, número 0.08, −3%, barra com o marcador vermelho em +8 e o fantasma em +9, VOLTA/ÚLT em vermelho claro, MÉD neutro, divisor e `74.0 L · dá 31 v · faltam 32  ALVO 2.30`. Só diferenças de pixel (o espaço entre `MÉD` e o número fica um pouco curto por causa do medidor estimado). |
| 10 · azul | Fiel: `OK FOLGA 2.7 L` com rótulo e unidade em `#CFE2FF`, marcador azul em −9, fantasma em −8, `ATÉ 2.45`. |
| 11 · PIT | Fiel ao mock, sem barra, apoio neutro e F4 em duas linhas. Arranjo um pouco mais limpo que o do mock: `alvo 2.16 · MÍN 2.23` numa linha só e `faltam 34` embaixo. Hierarquia e ordem iguais. |
| 12 · treino / sem baseline | Fiel: `74.0 L` grande, `dá 25 v · 45 min`, divisor, apoio e pílula `● REC 4/9`. VOLTA aparece em `#D5DAE0` em vez do `#F2F4F7` do mock (decisão da frente B; diferença de tom, não de hierarquia). |
| extra · saturado | Triângulo vermelho na ponta direita, `~2.30`, três pílulas na ordem REC, BoP≠, ~ CAUTION. A F4 cai para o tamanho de reserva por causa do `~`, como diz a decisão. |
| extra · sem barra | Painel sem a F2 e mais baixo; o resto não muda. |
| extra · edição | Contorno tracejado, alças, faixa MODO EDIÇÃO e rodapé `X · Y · escala`; a caixa envolve as pílulas. |
| extra · escala 250 % | Proporcional. |
| extra · inativo | Esmaecido, só a primeira faixa, mas com `dá 25 v · 45 min`, combinação que o builder **nunca** produz no inativo (ver B1). |

## Bloqueantes

### B1. O INATIVO real mostra `dá — v` e nunca a autonomia; a prévia `inativo` esconde isso

- **Onde:**
  - `src/FuelPace.Core/Strategy/FuelStrategyEngine.cs:16-25` (saídas antecipadas para `Inactive`, com só `FuelL` preenchido).
  - `src/FuelPace.Core/Display/GlanceViewBuilder.cs:50-52` (`floor((C − res)/MeanL)` com `MeanL = NaN` vira `LapsPossible = -1`).
  - `src/FuelPace.App/Overlay/GlanceLayout.cs:407-409` (escreve `dá — v`).
  - `src/FuelPace.App/Overlay/GlancePreviews.cs:95` (a prévia `inativo` herda `LapsPossible = 25` e `MinutesPossible = 45` do compacto).
  - `tests/FuelPace.App.Tests/Overlay/GlanceLayoutTests.cs:604` (testa essa view montada à mão).
- **O que está errado:**
  - No caminho `Inactive`, o engine devolve `MeanL` e `PaceS` como NaN. Então o builder sempre gera `LapsPossible = -1`
    e `MinutesPossible = -1`, como o próprio `No_target_is_compact_and_inactive_is_inactive` confirma.
  - O painel inativo real fica `74.0 L ……… dá — v` em pit lane, box, garagem e replay (`Inactive` inclui `OnPitRoad`
    e `PlayerCarInPitStall`). Sem o sim, fica `— L ……… dá — v`.
  - A prévia `inativo` e o teste de layout usam uma view montada à mão que o app nunca gera.
  - O roteiro de `docs/windows-testing.md:104` manda o piloto conferir "só combustível e autonomia" e vai falhar.
- **Por que bloqueia:**
  - A §6.5 pede o INATIVO "com a primeira faixa (COMB e autonomia)".
  - A §3 manda sumir o `—` ("o que não muda decisão some").
  - É um defeito visível para o piloto. Com o `autoHide: false` do `settings.json` real do Allan
    (`tests/fixtures/settings/v1-real.json`), ele fica na tela durante toda parada no box.
  - A prévia de revisão (§11.4) mostra algo diferente do que o app desenha.
- **Como corrigir:**
  1. No `FuelStrategyEngine`, preencher também no caminho `Inactive` (não elétrico) os valores que o builder usa, por
     atribuição e com as mesmas expressões do caminho ativo: `MeanL`/`MeanFromBaseline` (`input.StintMeanL`, senão
     `baseline.FuelMeanL`) e `PaceS` (`myPace`). É "expor sem mudar a matemática" (§8.2): estado e fórmulas não mudam,
     e `AutonomyLaps`/`AutonomyMin` podem continuar NaN se algum teste da v2.1 depender disso.
  2. No `GlanceLayout.CompactFuel`, omitir a parte `dá N v` quando `LapsPossible < 0`, como já se faz com os minutos.
     Isso cobre o sem-sim, o carro elétrico e o compacto no começo de um treino sem MÉD.
  3. Gerar a prévia `inativo` pelo builder a partir de um `OverlayState` inativo coerente com o engine, ou ajustar a
     view à mão para o que o builder gera. Regravar `docs/v3/preview/inativo.svg`.
  4. Adicionar um teste pelo engine: inativo com média de stint dá `LapsPossible ≥ 0`, e sem média o layout não
     escreve `—`.

## Não bloqueantes

- **N1** `GlanceViewBuilder.cs:94`: no PIT sem VOLTA e sem ÚLT (começo do primeiro giro do stint), a F1 mostra `PIT —`,
  embora `FALTAM = Lr·lim − (C − res)` não dependa de `ref`. Segue a §5.1 ao pé da letra; vale revisitar no V3-11.
- **N2** `GlanceViewBuilder.cs:50-52`: com combustível abaixo da reserva, o builder dá `dá — v` em vez de `dá 0 v` (item
  do ledger). Na F4 completa, `0` diz mais ao piloto justamente quando importa. Corrigir junto com o B1 é barato.
- **N3** `GlanceViewBuilder.cs:82-83`: com economia pequena (por exemplo, 0,01 L e b = −2 s/L), o complemento em
  segundos arredonda para `+0.0 s` e aparece assim. Sugestão: voltar para `%` quando |s| < 0,05.
- **N4** `GlanceLayout.cs:45` (`PitTargetFont = 11`): a §5.4 diz 12 px para `alvo {A} ·`. A decisão seguiu o brief e o
  mock (11 px), mas a §11.4 diz que o PRD prevalece em caso de dúvida. É diferença de pixel; alinhar ou registrar no PR.
- **N5** Carro elétrico: a v2.1 avisava "Carro elétrico: sem suporte"; a v3 mostra só o painel inativo esmaecido, sem
  explicação. O PRD v3 não cobre esse caso. Considerar uma pílula ou uma nota no Settings.
- **N6** `AgentHostFixtureTests`: a PERF-06 com a fixture é pulada no CI (a `.ibt` não é versionada). O critério só foi
  verificado localmente (0 redesenhos parado, cerca de 2/s no stint). Registrar isso no PR.
- **N7** `src/FuelPace.Agent/Overlay/DWriteTextMeasurer.cs:12`: o comentário ainda cita a "insight cut-off search", que
  não existe mais.
- **N8** A `Version` também anda por campos que o modo atual não desenha: tempos da linha de pace desligada,
  `LapsRemaining` no inativo. Isso gera um layout a mais no UI thread, sem redesenho (o `ContentHash` barra). Decisão
  aceita; só registrar.
- **N9** O Direct2D cria uma `ID2D1PathGeometry` por triângulo e o WPF uma `StreamGeometry` por triângulo (itens do
  ledger). Fora do hot path, no máximo 10 por segundo. Aceitável.
- **N10** O V3-7 (nenhum texto passa da borda) só é verificado com o `FixedMetricsMeasurer`; as larguras da Inter são
  estimadas. Sugestão: repetir `Widest_values_stay_inside_the_panel_and_do_not_overlap` em `RenderTests` com o
  `DWriteTextMeasurer` (teste de Windows).
- **N11** Testes com asserção fraca (itens do ledger):
  - `Delta_bar_scale_field_accepts_comma_or_dot` testa `NumberInput`, não o binding do XAML.
  - `RenderTests` usa `Assert.True(x == 0)`.
  - `OverlayViewModelTests` trata o NaN dentro da teoria.
  São de estilo.
- **N12** O builder arredonda por `decimal` e o engine por `double`. Num empate exato de meio centésimo (inalcançável
  com telemetria em float), a cor do bloco e o número podem discordar por 0,01. Decisão consciente; registrar no PR.
- **N13** `SettingsStore`: um arquivo com `schemaVersion > 2` não é regravado na leitura, mas a primeira mudança do
  usuário salva o arquivo e descarta os campos desconhecidos. A decisão D ("não perde dados") só vale até essa primeira
  gravação.

## Triagem do ledger

| Item | Triagem |
|---|---|
| Edit chrome duplicado do `OverlayLayout` (tarefa 2) | Resolvido: o `OverlayLayout` foi apagado na integração. |
| Fábrica e geometria por triângulo no D2D; `StreamGeometry` por comando no WPF (tarefa 3) | Fica como está (N9). |
| `RenderTests` com `Assert.True(==0)` (tarefa 3) | Estilo; não precisa antes do merge. |
| Nota de merge em `decisoes-C.md` (tarefa 3) | Resolvido: o arquivo foi juntado em `decisoes.md` e apagado. |
| Migração não chega ao log do Agent, `Program.cs` (tarefa 4) | Resolvido: `AgentOptions.EarlierSettingsLoad` e três testes no `AgentHostTests`. |
| Linha em branco e comentário no meio dos argumentos de `ViewOptions` (tarefa 4) | Resolvido: o `ViewOptions` atual está limpo. |
| `A_v2_file_is_not_migrated_or_rewritten` com timestamps grossos (tarefa 4) | Aceitável: o teste recua o horário 5 min antes de comparar. |
| `Delta_bar_scale_field_accepts_comma_or_dot` não testa o binding (tarefa 4) | Não precisa antes do merge (N11). |
| `decimal` × `double` no arredondamento (tarefa 1) | Aceito; registrar no PR (N12). |
| NaN tratado dentro da teoria em `OverlayViewModelTests` (tarefa 4) | Estilo. |
| Remoção do gerador de insight e ligação da reserva (tarefa 1) | Resolvido na integração (`OverlayText.Insight` saiu; `ToGlanceConfig` usa `Strategy.ReserveL`). |
| `LapsPossible` mostra `—` abaixo da reserva (tarefa 1) | Não bloqueia (N2); recomendo corrigir junto com o B1, no mesmo trecho. |
| F4 do PIT em duas linhas; F4 com um tamanho de reserva (tarefa 2) | Coerentes com o mock e com o V3-7; ficam. |
| Minutos omitidos com `MinutesPossible = -1` (tarefa 2) | Correto pela §3; a mesma regra deve valer para `dá N v` (B1, passo 2). |
| `Builder.T` mede de novo um texto já medido (tarefa 2) | Só CPU no UI thread; fica. |
| Sem revisão por tarefa da integração (tarefa 5) | Coberta por esta revisão. |

**Antes do merge:** só o B1. Os itens do ledger não exigem correção; o N2 é barato e cai no mesmo trecho.

## Ciclo 2

Base: `docs/v3/revisao.md` acima, relatório do corretor (`onda3-fix-report.md`), diff `668df4e..abe8d1f` e as novas
renderizações `cycle2-inativo.png` e `cycle2-pit.png` (comparada com `m-V3Pit.png`). Só olhei o diff da correção, à procura
de quebras novas; o código fora dele não foi revisto de novo.

Rodei um conjunto focado para tirar uma dúvida: se expor a média no inativo quebraria a PERF-06 com o carro parado no box.
Foram `AgentHostFixtureTests`, os testes com `Inactive` no nome e `GlancePreviewsTests`, todos em `FuelPace.App.Tests`,
com a fixture Zolder presente: 16 de 16 aprovados. O relatório do corretor dá 807 de 808 aprovados na suíte Linux (o
ignorado é um teste só de Windows) e build sem avisos.

### Vereditos

| Item | Veredito | Observação |
|---|---|---|
| B1 · INATIVO sem autonomia | **RESOLVIDO** | Ver abaixo. |
| N2 · `dá 0 v` abaixo da reserva | **RESOLVIDO** | `GlanceViewBuilder`: com `MeanL > 0` e resultado finito, `clamp(floor((C − res)/MÉD), 0, 999)`. Média ou combustível desconhecidos continuam −1 (`Laps_clamps_and_noise`). |
| N3 · `+0.0 s` | **RESOLVIDO** | Segundos que arredondam para 0,0 deixam `ComplementSec` em NaN e voltam para `%`; 0,05 continua em `+0.1 s` (`Complement_in_seconds_that_rounds_to_zero_falls_back_to_pct`). |
| N4 · `alvo {A} ·` a 12 px | **RESOLVIDO** | `PitTargetFont = 12`. A tela 11 continua fiel (`cycle2-pit.png`): `74.0 L · dá 31 v   alvo 2.16 · MÍN 2.23` e `faltam 34`; painel com 115 px. Efeito colateral aceitável: só o modelo mais largo do PIT (`100.0 L` com `~`) cai para o tamanho de reserva (≈ 11 px), o que a regra dos dois tamanhos já previa. |
| N7 · comentário do medidor | **RESOLVIDO** | `DWriteTextMeasurer.cs:12` não cita mais a busca de corte do insight. |
| N10 · V3-7 com fontes reais | **RESOLVIDO** (pendente de CI) | `RenderTests.Widest_values_stay_inside_the_panel_with_the_real_fonts`: os 7 casos em 1× e 2,5×, com `DWriteTextMeasurer` e as fontes embutidas, mesmas verificações de borda e sobreposição. Compila no Linux, mas só roda no windows-latest. A primeira execução de CI é a primeira execução dele; se falhar, é um estouro de texto real e precisa de correção. |

**B1 em detalhe.**

- **Engine** (`FuelStrategyEngine.cs`):
  - `myPace`, `MeanFromBaseline`, `MeanL` e `PaceS` foram movidos para antes das saídas antecipadas não elétricas, com as mesmas expressões, e saíram do lugar antigo.
  - Nenhuma expressão nova, e nada que entra em `L_rest`, alvo, factibilidade, estado, histerese ou insight mudou de valor: `myPace` já era calculado antes de `LapsRemaining`, e `MeanL` antes de `refL`.
  - `AutonomyLaps` e `AutonomyMin` continuam NaN no inativo, e o caminho do carro elétrico não mudou.
  - **Conclusão:** é exposição de valor, permitida pela §8.2, e não mudança de fórmula.
- **Teste da v2.1 alterado:** `Inactive_shows_only_fuel` virou `Inactive_shows_only_fuel_and_the_autonomy_inputs`.
  - A asserção da v2.1 era `MeanL = NaN` no inativo, que guardava o visual "só COMB" da v2.1 §7.4. A §6.5 da v3 substitui esse visual.
  - Não é teste de fórmula, então não é o caso da §11.1, item 2c.
  - O teste novo é mais forte: cobre média do stint, média da baseline e ausência de média, e confere que todo o resto continua NaN.
  - **Conclusão:** alteração legítima; citar no PR.
- **Layout:** `CompactFuel` omite `dá N v` quando `LapsPossible < 0`, então sem sim, carro elétrico ou primeira volta sem MÉD aparece só o combustível (`Unknown_autonomy_shows_only_the_fuel`).
- **Prévia:** `inativo` agora sai do builder sobre `InactiveInput`, e `The_inactive_preview_is_what_the_engine_and_builder_make` confere engine, builder e prévia. `cycle2-inativo.png` mostra `74.0 L … dá 31 v · 41 min`, esmaecido, com uma faixa, como pede a §6.5. A ordem dos inicializadores estáticos (`All` depois de `SampleConfig` e `InactiveInput`) está correta.
- **Ponta a ponta:** `GlanceViewBuilderTests.Inactive_through_the_engine_keeps_laps_and_minutes_possible` e `GlanceLayoutTests.Inactive_through_the_engine_shows_fuel_and_autonomy`.

### Quebras novas

Não há bloqueante novo (nenhum B2). Três observações não bloqueantes:

- **N14:** no inativo, a `Version` agora também anda quando a MÉD muda, e o `refL` dos diagnósticos `StateChanged` e `OverlayVersion` passa a ser a MÉD em vez de NaN. Durante um reabastecimento no box, `dá N v` muda e redesenha (no máximo 10/s), o que é a informação que a §6.5 pede. Com o carro parado e o tanque estável continuam 0 redesenhos (`AgentHostFixtureTests` verde).
- **N15:** com combustível abaixo da reserva, o compacto mostra `dá 0 v · 0 min`. É coerente com o N2.
- **N16:** o teste de N10 ainda não rodou (só Windows). Acompanhar o primeiro CI antes do merge.

### Veredito final

**Aprovado.** Os bloqueantes estão resolvidos; o merge fica sujeito ao CI verde no windows-latest, que vai rodar o
`RenderTests.Widest_values_stay_inside_the_panel_with_the_real_fonts` pela primeira vez.

**Para a descrição do PR:**
- os itens N1, N5, N6, N8, N9, N11, N12 e N13 do ciclo 1;
- a troca do teste da v2.1 `Inactive_shows_only_fuel`;
- os itens N14 e N16 deste ciclo.
