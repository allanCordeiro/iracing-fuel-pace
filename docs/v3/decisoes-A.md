# PRD v3: decisões da frente A (builder + NumberFormat)

contexto → escolha → motivo

- Half-cent: `Math.Round(2.385, 2, AwayFromZero)` dá 2.38 (o double é 2.38499…) mas o V3-4 exige 2.39 → o builder arredonda via `decimal` (15 dígitos significativos), que não aloca; NaN e |x| ≥ 1e9 usam `Math.Round` → o empate digitado vale como empate. Só difere do `Round2` do engine em empates de ponto flutuante, e a cor do marcador é independente da histerese por desenho (§5.2).
- `NumberFormat.Fixed` usava `ReadOnlySpan<string> formats = [...]` → aloca o array a cada chamada → trocado por `switch` sobre literais, para cumprir PERF-03.
- `NumberFormat.Fixed`/`SignedSeconds` com negativo que arredonda para zero (−0.001) → escreve "0.00" / "+0.0 s" sem sinal → "−0.00" seria ruído no overlay.
- Red sem referência (`LapProjectionL` e `LastLapL` ambos NaN, mesmo com `MeanL`) → `DecisionValue = NaN`, sem complemento, `Decision = Economize` mantido → PRD §5.1 (selo do estado atual e número `—`); o estado do engine ainda usa `MeanL` como referência.
- `ComplementPct` saturado em −127 (tipo `sbyte`) → valores absurdos de economia não estouram o tipo.
- `BarSteps` é calculado em todos os modos (depende só da configuração) → o dirty flag não muda à toa.
- `IsEstimate` copia `TargetIsEstimate` em qualquer modo (o engine só o liga com alvo) → literal do brief.
- `LapsRemaining` com ruído: `ceil(round(Lr, 6))`; NaN/infinito → −1; teto 999 em `LapsPossible`, `LapsRemaining` e `MinutesPossible`.
