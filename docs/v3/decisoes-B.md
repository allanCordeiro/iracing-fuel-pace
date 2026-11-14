# PRD v3: decisões da frente B (GlanceLayout + Preview)

Formato: contexto → escolha → motivo.

- Worktree da frente nasceu do commit inicial, não da `feat/v3-glance` → `reset --hard` do branch do worktree para `9b6ffbd` antes de começar → a base combinada da Onda 1 é `9b6ffbd`.
- Padding do painel: o PRD diz 7 × 9 e o mock tem 7 em cima e 8 embaixo → 7 nos dois → o PRD manda; altura completa 118 px (alvo 120 ± 6).
- Alinhamento vertical na F1 (mock: `align-items: center`) → selo, rótulo, número e complemento centrados no bloco; unidade `L` na linha de base do número → como o mock.
- `DecisionValue = NaN` → só o selo e `—` (sem rótulo, unidade nem complemento) → PRD §5.1 "a F1 mostra só o selo do estado atual e o número —".
- Trilho da barra com pontas arredondadas → duas metades arredondadas lado a lado; as pontas internas ficam sob a linha do alvo (o que sobra é < 0,5 px em 1×) → sem comando novo no `DrawList`.
- Triângulo de saturação → 8 × 16 px em 1×, encostado na ponta do trilho (dentro da margem de 3 px), na cor do lado → o PRD não dá a largura; 8 px ≈ a mesma massa visual do marcador de 6 px.
- Dentro do painel só uso `FillRoundedRect` (linha do alvo, fantasma e divisor com raio 0) → `OverlayEditGeometry.HitTest` trata todo `FillRect` como alça do modo edição; um `FillRect` no painel quebraria o arraste.
- F3 no modo compacto: o mock tem `VOLTA` em `#F2F4F7` e o brief diz `#D5DAE0` "PIT e compacto" → `#D5DAE0` → valores do brief (neutro, como no PIT).
- `alvo 2.16 ·` do PIT: o PRD diz 12 px e o brief/mock 11 px → 11 px → valor literal do brief.
- Linha da F4 no PIT não cabe em 300 px nos tamanhos do PRD (o próprio mock quebra a linha em duas) e o mesmo vale para os valores mais largos → as duas partes secundárias (texto da esquerda e `alvo …` cinza) encolhem pelo mesmo fator até caber, com 8 px de folga; rótulo e valor do alvo mantêm o tamanho → sem quebra de linha (altura fixa) e sem sobreposição; no PIT o fator fica ≈ 0,76 (≈ 9 px), próximo dos 8,5 px que a v2.1 já usava no subtítulo do NÃO FACTÍVEL.
- `~` do alvo estimado no PIT → vai no número cinza de `alvo ~2.16 ·`, não no `MÍN` → `MÍN` é o limite factível, que não é estimado.
- `dá N v` com `LapsPossible = -1` → `dá — v`; `MinutesPossible = -1` no compacto → a parte ` · N min` some → "o que não muda decisão some" (§3), e `— min` não informa nada.
- Linha de pace (§5.5) "no estilo da F3" → pares rótulo 8 px + valor 13 px: `ÚLT 79.51 s`, `MÉD 80.14 s`, `BASE 80.12` (o `BASE` some sem baseline), 6 px abaixo da F4 → mesmo estilo da F3; aparece no completo e no compacto, nunca no inativo (v2.1 também escondia no inativo).
- Pílulas: ordem da esquerda para a direita `DBG`, `REC`, `BoP≠`, `~ CAUTION` → o desenho do §4 põe REC antes de BoP e a v2.1 punha DBG à esquerda do REC; todas no texto `#F2F4F7`, exceto DBG em `#A9B2BD` (como na v2.1).
- Opacidade do conteúdo: o fundo colorido da F1 não é "conteúdo" (igual à faixa ALVO da v2.1); selo, textos, barra, divisor e ponto REC são → mantém a convenção da v2.1.
- Paleta e chrome de edição copiados para `GlanceTheme` e `GlanceLayout` (e `PanelOffset` próprio) → a integração apaga `OverlayLayout`/`OverlayTheme`; o `GlanceLayout` só depende de `OverlayViewOptions`, que mora em `OverlayLayout.cs` e precisa sobreviver à limpeza.
- Teste "o Preview grava os arquivos esperados" sem referenciar o projeto da ferramenta → a escrita mora em `GlancePreviews.WriteAll(dir)` (App) e `tools/FuelPace.Preview` só a chama → o teste fica em `FuelPace.App.Tests` sem mexer no `.csproj` dele; um segundo teste confere que os SVGs versionados em `docs/v3/preview` batem com o gerador.
- `tools/FuelPace.Preview` deixa de gerar os SVGs da v2.1 (`docs/overlay-preview`) → o brief diz "passa a gravar esses SVGs"; os SVGs antigos ficam como estão até a integração apagar a v2.1.
