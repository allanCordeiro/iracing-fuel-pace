# Decisões da Frente C (renderers)

- Cache de layouts de texto → já existe em `D2DDrawListRenderer` (LRU de 64, chave texto+papel+peso+tamanho+espaçamento+itálico, liberado no Dispose; teste `Rendering_the_same_list_again_creates_no_text_layout`) → não mexer → já cumpre PRD §8.2.
- Pesos de fonte → `DWriteFonts` carrega um arquivo por peso (Inter 400/600/700/800, JetBrains Mono 400/500/600/700) e `Load` valida peso exato sem simulação; `Fonts_come_from_the_embedded_files_with_the_exact_weights` já cobre 600/700/800 e 500/600/700 → sem correção.
- Triângulo no Direct2D → `ID2D1PathGeometry` criada e liberada por comando (poucos por quadro, ≤ 10 redesenhos/s) → mais simples que cachear (PERF-06).
- Triângulo no WPF → `StreamGeometry` congelada por comando, mesmo padrão do pincel congelado já usado ali.
- Ramo de trabalho → o worktree partia de `main`; mesclei `feat/v3-glance` para obter o contrato da Onda 0 (`FillTriangle`).
