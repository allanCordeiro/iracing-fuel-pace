# PRD v3: decisões (contexto → escolha → motivo)

Cada frente da Onda 1 registra as suas em `docs/v3/decisoes-<frente>.md` (A, B, C, D) para não haver conflito de merge; a
integração junta tudo aqui.

## Onda 0 (contratos)

- Branch base: v2.1 ainda não está na `main` (PR #1 aberto) → `feat/v3-glance` sai de `feat/fuelpace` e o PR final aponta para `feat/fuelpace` → o diff do PR mostra só a v3; se a `main` for a base desejada, basta trocar o alvo do PR.
- PRD §8.2 põe `GlanceLayout` no Core e reescreve o renderer → o app já tem layout independente de plataforma em `FuelPace.App/Overlay` (`OverlayLayout` → `DrawList`, testado no Linux) e renderers genéricos de `DrawList` (Direct2D, WPF, SVG) → `GlanceLayout.Build(GlanceView, OverlayViewOptions, ITextMeasurer, DrawList)` fica em `FuelPace.App/Overlay`; os renderers só ganham o comando novo `FillTriangle` (triângulo de saturação da barra).
- Entrada do builder → `GlanceViewBuilder.Build(in OverlayState, in GlanceConfig)`: o `OverlayState` do engine já traz o estado com a histerese de 1,5 s, e o builder só deriva valores de exibição → nenhuma fórmula do engine muda.
- O builder precisa do ritmo usado na autonomia (minutos do modo compacto) e da inclinação consumo × tempo (complemento em segundos) → o engine passa a expor `OverlayState.PaceS` e `OverlayState.FuelVsTimeB`, atribuições sem conta nova (§8.2 permite expor).
- `GlanceView` → `record struct` (igualdade por valor; NaN == NaN) para servir de dirty flag (§8.4); campos a mais em relação ao §8.1: `BarSteps` (Q, para o layout posicionar o marcador), `ShowAvg` (CFG-V3-04) e os tempos da linha de pace (§5.5); `short` −1 e `float` NaN = "—"; `BarAbsent` = `sbyte.MinValue`.
- Estado `NoTarget` da v2.1 (baseline sem limite ou sem referência) → layout compacto, como `SEM BASELINE` (§6.4: "o que não muda decisão some"). Carro elétrico → `Inactive` (o engine já devolve inativo).
- Remoção do gerador de texto de insight (`OverlayText.Insight`, `InsightKind` no `OverlayState`) → fica para a integração: o layout v2.1 ainda o usa até a troca pelo `GlanceLayout`.
- Configurações: o código real fica em `FuelPace.App/Settings/AppSettings.cs` (não em `Core/Config`) → campos `ShowDeltaBar`, `DeltaBarScaleLpl`, `RedComplement` (`"pct"` | `"seconds"`), `ShowAvgInSupport`; o IPC transporta o `AppSettings` inteiro, então não precisa de mensagem nova.
- §8.3 (Inter 800) → `assets/fonts/Inter-ExtraBold.ttf` já está embutida e em uso (selo NÃO FACTÍVEL da v2.1) → nada a fazer.
- Mocks → as telas 9–12 e a tela 8 (Configurações) do canvas foram baixadas para `docs/v3/mocks/*.dc.html` (HTML em 2×), referência para o layout e para o revisor.
