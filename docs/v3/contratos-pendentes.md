# PRD v3: contratos pendentes

Na Onda 1 cada frente anotou em `docs/v3/contratos-pendentes-<frente>.md` o que precisou mudar num contrato da Onda 0; a
integração juntou tudo aqui, com a resolução de cada item, e apagou os arquivos por frente.

## Frente A

- Nenhuma mudança nos contratos da Onda 0 (`GlanceView`, `GlanceConfig`, `DrawList`, `OverlayState`). → Resolução: nada a fazer.

## Frentes B, C e D

- Não abriram arquivo de pendências. As mudanças que precisavam da integração ficaram nas decisões (`decisoes.md`):
  `OverlayViewOptions` fora do `OverlayLayout.cs` e sem `ShowInsightLine` (B, D) → resolvido: arquivo próprio, campo
  removido; `LastLoadMigratedFrom`/`LastLoadWasCorrupt` sem ninguém para logar (D) → resolvido: `AgentOptions.EarlierSettingsLoad`
  leva o load do `Program.cs` ao host, que loga.

## Integração (Onda 2)

- `GlanceView`, `GlanceConfig`, `DrawList` → sem mudança.
- `OverlayState` → perdeu `VisibleEquals` (a dirty flag passou a ser o `GlanceView`); campos sem mudança, o `Insight` (valor) fica para o diagnóstico.
- `OverlayStatePublisher` → `Publish(in OverlayState, in GlanceView)` no lugar de `Publish(in OverlayState)`, `ReadView()` novo, sem `CarShort`/`TrackShort`.
- `TelemetryPipeline` → parâmetro opcional `GlanceConfig? glance` no construtor, `GlanceConfig` e `SetGlanceConfig` novos.
- `AgentOptions` → parâmetro opcional `SettingsLoadReport? EarlierSettingsLoad`.
