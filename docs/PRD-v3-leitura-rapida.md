# PRD v3 (diferencial): overlay de leitura rápida

> **Produto:** FuelPace · **Autor:** Allan · **Data:** 2026-11-13 · **Status:** pronto para implementação no Claude Code, em modo multiagente e autônomo
>
> **Base:** este documento é um **delta** sobre o `PRD-overlay-combustivel.md` **v2.1**, que já está implementado. Tudo o que este documento não menciona continua valendo como está na v2.1 (captura, baseline, `L_rest`, ritmo do líder, factibilidade, arquitetura, desempenho, persistência de perfis).
>
> **Mocks da v3 (referência visual oficial):** [canvas "Mocks overlay FuelPace"](https://claude.ai/artifact/Q6er7raafCpiD56Z1LC96Y), telas **9 a 12** ("Proposta · …"), à direita das telas antigas: 9 = vermelho, 10 = azul, 11 = não factível (PIT), 12 = treino/sem baseline. As telas 1 a 5 são o layout v2.1, que este PRD substitui. O canvas é **privado** (abre só com a conta do Allan). Se um agente não conseguir abrir o link, as §4 a §6 descrevem as telas por completo e são a fonte da verdade.
>
> **Como ler:** cada seção diz qual seção da v2.1 ela **substitui**, **altera** ou **remove**.

---

## 1. Motivação

No teste em pista de 2026-11-11 (Ligier JS P320, Mobility Resort Motegi, treino), a captura funcionou e os números bateram com o widget nativo do iRacing (média de 2,92 L/v nos dois). O problema foi a **leitura**:

- O overlay tinha ~20 elementos para ler: 5 rótulos, 3 unidades, 7 números e uma frase. Na reta, o olhar dura menos de 1 s e pega 1 ou 2 coisas.
- A decisão ("economizo ou não?") dependia de comparar de cabeça três números quase iguais e do mesmo tamanho (`VOLTA 2.91 · ÚLT 2.91 · MÉD 2.92`) com o `ALVO`.
- A linha de insight era uma frase e já aparecia truncada (`Sem baseline para Ligier JS P320 · Mobility Resort …`).
- No treino sem baseline, metade do overlay mostrava `—`, `sem limite` ou `sem baseline`, informação que não muda nenhuma decisão.

**Objetivo da v3:** o piloto decide em **um olhar** se economiza, segue ou vai parar, e consegue o detalhe num segundo olhar, se a reta permitir.

## 2. Escopo do delta

### Muda
- Layout e hierarquia visual do overlay (§4, §5 e §6 deste documento). **Substitui a §7.1** da v2.1.
- Conteúdo exibido por estado. **Altera a §7.2 e a coluna "Visual" da §7.4**.
- Linha de insight em frase. **Remove a §7.5**, cujo conteúdo vai para o bloco de decisão e para as pílulas de status.
- Novos valores derivados: número de decisão, folga, alvo por volta com rótulo, barra de delta (§5).
- Configurações do overlay. **Altera a §8** (§7 deste documento).
- `OverlayState`, renderer, layout, `settings.json` (§8 e §9).

### Não muda
- `FuelStrategyEngine`: cálculo de `L_rest`, `ALVO`, `consumo_ref`, `limite_factível`, estado AZUL/VERMELHO/NÃO FACTÍVEL/SEM BASELINE/INATIVO, histerese de 1,5 s e comparação nos valores arredondados a 0,01. **Nenhuma fórmula da v2.1 é alterada.** A v3 só acrescenta valores derivados por cima.
- Captura, validação de voltas, baseline, perfis, ritmo do líder, IPC, leitor de telemetria.
- Orçamento de desempenho (§11 da v2.1). Ele continua obrigatório e ganha uma regra nova para a barra (§8.4).

## 3. Princípio: três níveis de leitura

| Nível | Como é lido | O que mostra | Tempo |
|---|---|---|---|
| **1. Cor** | visão periférica, sem focar | bloco de decisão inteiro na cor do estado | < 0,3 s |
| **2. Palavra + número** | um olhar focado | `ECONOMIZE 0.08` · `OK FOLGA 2.7 L` · `PIT FALTAM 2.3 L` | < 1 s |
| **3. Apoio** | reta longa | barra de delta, volta/última/média, combustível, autonomia, alvo por volta | 1–2 s |

Regras derivadas:
- **Uma palavra de decisão por estado**, sempre escrita (não depender só da cor, por causa do daltonismo).
- **Sem unidades redundantes:** `L/v` some de toda a tela, e `L` fica só onde o número é um volume de combustível.
- **Sem frases.** Avisos viram pílulas curtas (§4.3).
- **O que não muda decisão some**: `—`, `sem limite`, `sem baseline` e `treino`.

## 4. Layout (substitui a §7.1 da v2.1)

Painel de **~300 × 120 px na escala 1×** (antes era ~300 × 110). Os mocks estão em 2×. Quatro faixas de cima para baixo, com espaçamento vertical de 6 px (1×):

```
┌──────────────────────────────────────────┐
│█ [ECONOMIZE]  0.08                 −3% █│  F1 · bloco de decisão (fundo na cor do estado)
│   ◄────────────┼──▮╎──────────────►      │  F2 · barra de delta (│ alvo · ▮ volta atual · ╎ última)
│   VOLTA 2.38   ÚLT 2.39   MÉD 2.37       │  F3 · apoio
│  ─────────────────────────────────────── │       divisor
│   74.0 L · dá 31 v · faltam 32  ALVO 2.30│  F4 · combustível, autonomia, voltas, alvo por volta
└──────────────────────────────────────────┘
                              ● REC 4/9  BoP≠   pílulas de status (§4.3)
```

### 4.1 Faixas

| Faixa | Conteúdo | Tamanho em 1× | Cor |
|---|---|---|---|
| **F1 · Decisão** | selo com a palavra (caixa alta, peso 800) + rótulo opcional + **número de decisão** + complemento à direita | selo 11 px · rótulo 11 px · número **25 px** (mono, 700) · complemento 15 px | fundo e selo na cor do estado (§4.2) |
| **F2 · Barra de delta** | trilho em duas metades (azul à esquerda, vermelho à direita), linha central = alvo, marcador = volta atual, traço fantasma = última volta | altura total 18 px · trilho 4 px · marcador 6 × 16 px · fantasma 1,5 × 12 px | §4.2 |
| **F3 · Apoio** | `VOLTA x.xx  ÚLT x.xx  MÉD x.xx` | rótulos 8 px (600, caixa alta) · números 13 px (mono) | `VOLTA` e `ÚLT` em tom claro da cor do estado, `MÉD` neutro |
| **F4 · Autonomia** | `{COMB} L · dá {N} v · faltam {L}` à esquerda; `{rótulo} {alvo}` à direita | 12 px (mono) · `COMB` em peso 600 e branco · valor do alvo 15 px (700) | neutro (`#D5DAE0`, valor `#F2F4F7`) |

Acima da F4 vai um divisor de 1 px em `rgba(169,178,189,0.22)`. O padding do painel é de 7 × 9 px (1×) e o raio, de 6 px.

### 4.2 Paleta (substitui a tabela de paleta da v2.1)

| Uso | Cor |
|---|---|
| Fundo do painel e das pílulas | `rgba(10,12,16, opacidade)`, padrão 60% (sem mudança) |
| Texto principal / secundário / rótulo | `#F2F4F7` / `#D5DAE0` / `#A9B2BD` |
| **Vermelho**: fundo da F1 / selo / número claro / `ÚLT` | `rgba(235,87,87,0.30)` / `#C7362F` / `#FF7A7A` / `#FFA8A8` |
| Vermelho: complemento (`−3%`) | `#FFD9D9` |
| **Azul**: fundo da F1 / selo / número claro / `ÚLT` | `rgba(47,128,237,0.28)` / `#1E6BD6` / `#6AAEFF` / `#9CC8FF` |
| Azul: rótulo `FOLGA` e unidade | `#CFE2FF` |
| **PIT**: fundo da F1 / selo (texto) | `rgba(255,255,255,0.14)` / `#F2F4F7` (texto `#0E1013`) |
| Barra: metade azul / metade vermelha | `rgba(47,128,237,0.42)` / `rgba(235,87,87,0.42)` |
| Barra: linha do alvo / fantasma | `#F2F4F7` / `rgba(242,244,247,0.55)` |
| Barra: marcador | `#6AAEFF` à esquerda do alvo (ou sobre ele), `#FF7A7A` à direita |
| Ponto REC | `#FF5A5A` (sem mudança) |

Sem gradientes, sombras ou animações (regra da v2.1 mantida).

### 4.3 Pílulas de status (substituem os insights 2 e 6 da §7.5)

Uma linha de pílulas abaixo do painel, alinhada à direita, com 4 px de gap, no mesmo fundo do painel e com texto de 10 px (mono, 600):

| Pílula | Quando | Texto |
|---|---|---|
| REC | captura ligada | `● REC {válidas}/{total}` (mais curto que o `REC 4/9 válidas` da v2.1) |
| BoP | baseline com BoP ou tanque diferentes da sessão (CAP-09) | `BoP≠` |
| Estimativa | `ALVO` estimado (caution congelando `t_L`) | `~ CAUTION` |

Sem nenhuma pílula ativa, a linha não ocupa espaço.

## 5. Valores por elemento (altera a §7.2 da v2.1)

Notação: `res` = reserva; `C` = `FuelLevel`; `ref` = `consumo_ref` **arredondado a 0,01** (mesma regra da v2.1); `A` = `ALVO` arredondado a 0,01; `Lr` = `L_rest` fracionado; `lim` = `limite_factível`.

### 5.1 F1: palavra e número de decisão

| Estado | Selo | Rótulo | Número | Complemento |
|---|---|---|---|---|
| VERMELHO | `ECONOMIZE` | (nenhum) | `ref − A`, 2 casas, sem unidade (`0.08`) | `−{p}%`, com `p = round(100 × (ref − A) / ref)`, mínimo 1 |
| AZUL | `OK` | `FOLGA` | `max(0, (C − res) − Lr × ref)`, 1 casa, + `L` | (nenhum) |
| NÃO FACTÍVEL | `PIT` | `FALTAM` | `Lr × lim − (C − res)`, 1 casa, + `L` (mesma fórmula do insight 1 da v2.1) | (nenhum) |
| SEM BASELINE / INATIVO | **a F1 não existe** (layout compacto, §6.4) | | | |

- O complemento do vermelho pode passar a mostrar o custo em segundos (`+0.9 s`) via configuração (CFG-V3-03). Se `fuelVsTime = null`, volta para `%`.
- Com `ref` indisponível (`VOLTA = —` e sem `ÚLT`), a F1 mostra só o selo do estado atual e o número `—`.
- O número de decisão é recalculado a 10 Hz como os demais, mas **a cor e o selo seguem o estado com histerese** (v2.1, 1,5 s).

### 5.2 F2: barra de delta

- **Centro** = `A`. **Escala** = ±`S` L/v, com `S` configurável e padrão **0,20** (CFG-V3-02).
- **Marcador** em `x = clamp((ref_volta − A) / S, −1, 1)`, onde `ref_volta` é a projeção `VOLTA` arredondada a 0,01. Se `VOLTA = —` (início de volta ou volta de pit), o marcador some e fica só o fantasma.
- **Fantasma** em `clamp((ÚLT − A) / S, −1, 1)`. Ele some se não houver `ÚLT`.
- **Saturação:** com `|x| = 1`, o marcador encosta na ponta e vira um triângulo apontando para fora, para mostrar que está além da escala.
- **Cor do marcador:** segue a **posição instantânea** (azul se `x ≤ 0`, vermelho se `x > 0`), sem histerese. Isso é intencional: durante o lift & coast, o piloto vê o marcador cruzar o alvo antes de o bloco F1 trocar de cor.
- A barra **só existe nos estados AZUL e VERMELHO**. No PIT, não há alvo atingível para servir de centro.

### 5.3 F3: apoio

Mesmos valores e regras da v2.1 (`VOLTA` projetada com `—` abaixo de 15% da volta, `ÚLT` com `*` se inválida, `MÉD` em itálico enquanto usa a baseline). O que muda:
- Os rótulos ficam menores e os números saem do destaque (13 px, antes 16 px).
- A unidade `L/v` some.
- No PIT, os três ficam neutros (`#D5DAE0`).

### 5.4 F4: autonomia e alvo por volta

| Parte | Valor | Formato |
|---|---|---|
| `COMB` | `C` | 1 casa + ` L` |
| `dá N v` | `floor((C − res) / MÉD)` | inteiro (voltas completas possíveis; conservador) |
| `faltam L` | `ceil(Lr)` | inteiro. O cálculo continua usando `Lr` fracionado. No modo stint, vira `stint {N}`. |
| Alvo por volta (VERMELHO) | rótulo `ALVO` + `A` | o que é **preciso** fazer por volta |
| Alvo por volta (AZUL) | rótulo `ATÉ` + `A` | o **máximo** que dá para gastar e ainda chegar |
| Alvo por volta (PIT) | `alvo {A} ·` (12 px, cinza) + rótulo `MÍN` + `lim` | mostra por que não fecha |

- `A` é o mesmo número nos dois estados, a mesma fórmula da v2.1. Só o rótulo muda, para dizer como ler o número.
- Com `ALVO` estimado (caution), o valor ganha o prefixo `~` (`~2.30`), além da pílula `~ CAUTION`.
- `faltam` em corrida por tempo usa o ritmo do líder (v2.1 §7.3), sem mudança.

### 5.5 Linha opcional de pace

A linha opcional de pace continua, desligada por padrão, e entra abaixo da F4 com o estilo da F3.

## 6. Estados (substitui a coluna "Visual" da §7.4 da v2.1)

As condições de cada estado não mudam. Muda só o que aparece. O visual de cada estado está no [canvas de mocks](https://claude.ai/artifact/Q6er7raafCpiD56Z1LC96Y), nas telas indicadas.

### 6.1 VERMELHO (tela 9)
`ECONOMIZE 0.08 −3%` · barra com o marcador à direita · `VOLTA 2.38 ÚLT 2.39 MÉD 2.37` · `74.0 L · dá 31 v · faltam 32   ALVO 2.30`

### 6.2 AZUL (tela 10)
`OK FOLGA 2.7 L` · barra com o marcador à esquerda · `VOLTA 2.36 ÚLT 2.37 MÉD 2.37` · `74.0 L · dá 31 v · faltam 30   ATÉ 2.45`

### 6.3 NÃO FACTÍVEL (tela 11)
`PIT FALTAM 2.3 L` · **sem barra** · apoio neutro · `74.0 L · dá 31 v · faltam 34   alvo 2.16 · MÍN 2.23`

### 6.4 SEM BASELINE e treino (tela 12): layout compacto
Duas faixas, sem F1 nem F2:
```
┌──────────────────────────────────────────┐
│ 74.0 L                  dá 25 v · 45 min │  ← COMB 21 px + autonomia em voltas e minutos
│ ──────────────────────────────────────── │
│ VOLTA 2.91   ÚLT 2.91   MÉD 2.92         │
└──────────────────────────────────────────┘
                                  ● REC 4/9
```
- Os minutos usam `dá N v × tempo médio de volta do stint` (ou da baseline, se houver).
- **Não mostrar** `ALVO —`, `treino · sem limite` nem `Sem baseline para …`. A orientação "ative Capturar" fica só no Settings, que já mostra o estado do perfil.
- Treino **com** baseline e com tempo restante válido usa os estados normais (como na v2.1).

### 6.5 INATIVO
Layout compacto esmaecido (conteúdo a 50% de opacidade), só com a primeira faixa (`COMB` e autonomia).

### 6.6 Modo edição
Sem mudança: contorno tracejado, alças e rodapé `X · Y · escala`. A caixa de edição envolve o painel **e** a linha de pílulas.

## 7. Configurações (altera a §8 da v2.1)

| ID | Mudança |
|---|---|
| CFG-07 | **Alterado:** sai o toggle "linha de insight" (a linha não existe mais). Fica só o toggle da linha de pace. |
| CFG-V3-01 | **Novo:** mostrar a barra de delta (padrão: ligado). Desligada, a F2 some e o painel encolhe. |
| CFG-V3-02 | **Novo:** escala da barra `S`, de 0,05 a 0,50 L/v (padrão 0,20), com slider e campo. |
| CFG-V3-03 | **Novo:** complemento do vermelho: `%` (padrão) ou `s/v`. A opção `s/v` exige `fuelVsTime`; sem ele, mostra `%`. |
| CFG-V3-04 | **Novo:** mostrar `MÉD` na F3 (padrão: ligado). Desligada, a F3 fica com `VOLTA` e `ÚLT`. |

A tela "Configurações › Overlay e cálculo" (tela 8 do canvas) ganha esses controles numa seção "Layout". O restante da tela não muda.

## 8. Arquitetura e código (altera a §10 da v2.1)

### 8.1 `OverlayState` → `GlanceView`
O `OverlayState` (struct produzida pelo Core) é substituído por **`GlanceView`**, também uma struct blittable e sem strings:

```csharp
public enum GlanceMode : byte { Full, Compact, Inactive }
public enum DecisionKind : byte { None, Economize, Ok, Pit }
public enum TargetLabel : byte { None, Alvo, Ate, PitMin }
[Flags] public enum StatusPills : byte { None = 0, Rec = 1, BopMismatch = 2, Caution = 4 }

public struct GlanceView {
    public GlanceMode Mode;
    public DecisionKind Decision;          // selo + cor do bloco (com histerese)
    public float DecisionValue;            // NaN = "—"
    public sbyte ComplementPct;            // −3 → "−3%"; 0 = sem complemento
    public float ComplementSec;            // NaN = não usar
    public bool  ShowBar;
    public sbyte BarMarkerQ, BarGhostQ;    // posição quantizada −Q..+Q; sbyte.MinValue = ausente (§8.4)
    public float LapProj, LapLast, LapAvg; // NaN = "—"
    public bool  LapLastInvalid, LapAvgFromBaseline;
    public float Fuel;
    public short LapsPossible;             // "dá N v"
    public short LapsRemaining;            // ceil(Lr) ou stint
    public bool  IsStint, IsEstimate;
    public TargetLabel TargetLabel;
    public float Target, FeasibleMin;
    public short MinutesPossible;          // modo compacto
    public StatusPills Pills;
    public short RecValid, RecTotal;
}
```

O nome e os campos podem ser ajustados pelo agente de contratos (§11, Onda 0), desde que se mantenham estas propriedades: struct blittable, zero strings e todos os valores já arredondados para exibição.

### 8.2 Novos componentes

```
FuelPace.Core
├─ Display/GlanceViewBuilder.cs   ← NOVO · função pura: (StrategyResult, frame, baseline, config) → GlanceView (§5)
├─ Display/GlanceLayout.cs        ← NOVO · função pura: (GlanceView, escala, config) → retângulos de cada elemento
├─ Display/NumberFormat.cs        ← NOVO · formata float → Span<char> sem alocar (2 casas, 1 casa, inteiro, sinal)
└─ Config/OverlaySettings.cs      ← ALTERADO · campos da §7 + migração (§9)
FuelPace.Agent
└─ Overlay/OverlayRenderer.cs     ← REESCRITO · desenha GlanceView usando GlanceLayout; um IDWriteTextLayout em cache por campo
FuelPace.Settings
└─ Views/OverlaySettingsView      ← ALTERADO · seção "Layout" (§7)
tools/FuelPace.Preview            ← NOVO · CLI net10.0 (multiplataforma): GlanceView + GlanceLayout → SVG
```

- **`GlanceLayout` fica no Core** para que a geometria (posições, tamanhos, faixas que somem) seja testável em Linux. O renderer só traduz retângulos e textos em chamadas Direct2D/DirectWrite.
- **`FuelPace.Preview`** gera um SVG por estado a partir dos mesmos `GlanceView` e `GlanceLayout`. Serve para os agentes compararem o resultado com os mocks sem Windows (§11.4). É ferramenta de desenvolvimento e **não entra no instalador**.
- O que sai: o gerador de texto de insight (§7.5 da v2.1) e os campos de string do `OverlayState`. O `FuelStrategyEngine` continua expondo os mesmos valores. Se algum valor de que o builder precisa não estiver exposto, como `limite_factível`, o agente expõe esse valor **sem mudar a matemática**.

### 8.3 Fontes
Sem mudança: JetBrains Mono para os números e Inter para rótulos e selos. Adicionar o peso **800 (ExtraBold)** da Inter para os selos, embutido como os demais.

### 8.4 Desempenho: regra nova para a barra
O marcador da barra muda a cada tick. Sem cuidado, isso quebraria a PERF-06 (≤ 10 redesenhos/s, 0 quando nada muda). Por isso:
- A posição é **quantizada** em passos de 0,01 L/v: `Q = round(S / 0,01)` passos por lado, ou seja, 20 com `S = 0,20`. O `GlanceView` guarda o passo inteiro (`BarMarkerQ`), não o float.
- A dirty flag compara o `GlanceView` inteiro por valor. Como todos os campos já vêm arredondados para exibição, um redesenho só acontece quando algo **visível** muda.
- O teto de 10 redesenhos/s continua valendo.
- O `GlanceViewBuilder` roda na thread de estratégia (10 Hz), não no hot path de 60 Hz. **Zero alocação** é exigido nele também (PERF-03 estendida).

## 9. Persistência (altera a §9 da v2.1)

`settings.json` ganha `schemaVersion: 2`. Migração automática na leitura:
- remove `showInsight` (ou o nome equivalente no código atual);
- adiciona `showDeltaBar: true`, `deltaBarScaleLpl: 0.20`, `redComplement: "pct"` e `showAvgInSupport: true`;
- grava de volta de forma atômica (NFR-05). Um `settings.json` v1 corrompido ou desconhecido cai nos padrões, com aviso no log.

Os perfis (`profiles/*.json`) **não mudam**.

## 10. Critérios de aceite v3

Todos os critérios da v2.1 (§13) continuam valendo, exceto os trechos sobre o **texto** do insight nos critérios 3 e 4, substituídos pelos itens V3-1 e V3-2 abaixo.

**V3-1 · `GlanceViewBuilder` com a baseline da fixture** (`C = 50 L`, `res = 0,5`, `ref = 2,38`, `lim = 2,23`, `S = 0,20`):

| `Lr` | Estado | F1 | Barra (marcador, em passos de ±20) | F4 alvo |
|---|---|---|---|---|
| 19 | AZUL | `OK FOLGA 4.3 L` | `2.38 − 2.61 = −0.23` → **saturado em −20** | `ATÉ 2.61` |
| 22 | VERMELHO | `ECONOMIZE 0.13 −5%` | `+0.13` → **+13** | `ALVO 2.25` |
| 23 | NÃO FACTÍVEL | `PIT FALTAM 1.8 L` | sem barra | `alvo 2.15 · MÍN 2.23` |

**V3-2 · Igualdade e folga zero:** `C = 48,1`, `res = 0,5`, `Lr = 20`, `consumo_ref = 2,384` (arredonda para 2,38) → AZUL, `OK FOLGA 0.0 L` (o `max(0, …)` evita `−0.1`), `ATÉ 2.38`, marcador no passo 0.

**V3-3 · Números dos mocks** (`res = 0,5`, `MÉD = 2,37`, `lim = 2,23`):
- `C = 74,0`, `Lr = 32`, `VOLTA = 2,38`, `ÚLT = 2,39` → `ECONOMIZE 0.08 −3%`, `dá 31 v`, `faltam 32`, `ALVO 2.30`, marcador +8, fantasma +9.
- `C = 74,0`, `Lr = 30`, `VOLTA = 2,36`, `ÚLT = 2,37` → `OK FOLGA 2.7 L`, `ATÉ 2.45`, marcador −9, fantasma −8.
- `C = 74,0`, `Lr = 34` → `PIT FALTAM 2.3 L`, `alvo 2.16 · MÍN 2.23`, `ShowBar = false`.
- Sem baseline, `C = 74,0`, `MÉD = 2,92` → `Mode = Compact`, `dá 25 v`, `Decision = None`, `ShowBar = false`.

**V3-4 · Fronteiras da barra:** `VOLTA = —` → marcador ausente e fantasma presente. Sem `ÚLT` → fantasma ausente. Marcador exatamente no alvo → passo 0 e cor azul. `VOLTA = 2,385` (arredonda para 2,39) com `A = 2,38` → passo +1 e cor vermelha.

**V3-5 · Histerese × marcador:** com o estado mudando de VERMELHO para AZUL, o marcador troca de cor no mesmo tick, e o `Decision` só muda depois de 1,5 s com o novo estado estável.

**V3-6 · Caution:** com `t_L` congelado → pílula `Caution` ativa, `IsEstimate = true` e o alvo renderizado com `~`.

**V3-7 · Layout (`GlanceLayout`, escala 1×):** o painel completo tem 300 ± 4 px de largura e 120 ± 6 px de altura. O compacto tem 300 px de largura e ≤ 70 px de altura. Com `showDeltaBar = false`, a altura cai pelo tamanho da F2 mais um gap. Nenhum texto ultrapassa a borda do painel com os valores mais largos: `ECONOMIZE 0.50 −20%`, `faltam 999`, `100.0 L`, `dá 99 v`. Em 2,5×, tudo escala proporcionalmente (tolerância de ±1 px por elemento).

**V3-8 · Desempenho:** PERF-03 (zero alocação) estendida a 10.000 chamadas do `GlanceViewBuilder` e à formatação numérica. PERF-06 com replay da fixture a 1×: média ≤ 10 redesenhos/s e 0 redesenhos/s com o carro parado no box.

**V3-9 · Migração:** um `settings.json` v1 real do app atual migra para v2 preservando posição, escala, opacidades e atalhos.

**V3-10 · Preview:** `FuelPace.Preview` gera `vermelho.svg`, `azul.svg`, `pit.svg` e `compacto.svg` com os dados do V3-3, e eles correspondem às telas 9–12 em hierarquia, ordem e proporção (verificação visual pelo agente revisor, §11.4).

**V3-11 · Manual no Windows (único passo do piloto):** em pista, olhar o overlay numa reta e identificar estado e número de decisão sem precisar de um segundo olhar. Repetir a rodada de PresentMon da PERF-01.

## 11. Diretrizes de execução: multiagente e autônomo

**Meta:** o Claude Code implementa a v3 do início ao fim **sem perguntar nada ao Allan**. A única interação humana prevista é revisar e aprovar o PR final e fazer o teste V3-11 no Windows.

### 11.1 Regras de autonomia (valem para todos os agentes)
1. **Não pergunte.** Diante de ambiguidade, escolha a opção mais coerente com este PRD e com a v2.1, registre a decisão em `docs/v3/decisoes.md` (uma linha: contexto → escolha → motivo) e siga em frente.
2. **Pare só se:** (a) a mudança exigir alterar uma fórmula do `FuelStrategyEngine`; (b) a mudança exigir apagar dados do usuário; ou (c) um teste da v2.1 que passava começar a falhar sem correção possível dentro do escopo. Nesses casos, registre em `docs/v3/bloqueios.md` e continue com o que não depende disso.
3. **TDD:** cada critério V3 vira teste **antes** do código. Nenhum agente reporta conclusão sem `dotnet test` verde no próprio worktree.
4. **Posse de arquivos:** cada agente só altera os arquivos da sua frente (§11.3). Precisou mudar o contrato? Abra uma nota em `docs/v3/contratos-pendentes.md`. O integrador decide.
5. **Commits** pequenos, no padrão Conventional Commits (`feat(core): …`, `test(display): …`), com as linhas de atribuição do ambiente.
6. **Partes que só rodam no Windows** (renderer, WPF): devem **compilar** no Linux (`EnableWindowsTargeting=true`). Os testes de comportamento delas ficam marcados `[Trait("Platform","Windows")]` e são pulados fora do Windows, sem bloquear.
7. **Não verificar à toa:** nada de abrir browser, instalar ferramentas fora do que o repositório já usa ou rodar o iRacing. A verificação é feita por testes, benchmark e preview SVG.

### 11.2 Ondas

```
Onda 0  (sequencial, 1 agente)        Contratos
          │
Onda 1  (paralela, 4 agentes em worktrees)
          ├─ A · Core/Display: GlanceViewBuilder + NumberFormat
          ├─ B · Core/Layout + Preview: GlanceLayout + tools/FuelPace.Preview
          ├─ C · Agent/Renderer: OverlayRenderer reescrito
          └─ D · Config + Settings: OverlaySettings, migração, UI WPF
          │
Onda 2  (sequencial, 1 agente)        Integração
          │
Onda 3  (1 agente com contexto limpo) Revisão independente → correções (até 2 ciclos)
          │
        PR único para o Allan
```

### 11.3 Frentes

| Onda | Agente | Entrega | Arquivos que pode alterar | Critérios |
|---|---|---|---|---|
| 0 | **Contratos** | branch `feat/v3-glance`; `GlanceView` + enums (§8.1); assinaturas vazias de `GlanceViewBuilder.Build`, `GlanceLayout.Compute` e `NumberFormat`; novos campos de `OverlaySettings` com defaults; `docs/v3/` criado (com este PRD copiado para `docs/PRD-v3-leitura-rapida.md`). Tudo compila e os testes antigos continuam verdes. | qualquer um, só para criar contratos e esqueletos | build verde |
| 1 | **A · Display** | builder completo (§5), formatação sem alocação, remoção do gerador de insight do Core | `Core/Display/GlanceViewBuilder.cs`, `Core/Display/NumberFormat.cs`, código de insight antigo, `Tests/Display/*` | V3-1 a V3-6, V3-8 (alocação) |
| 1 | **B · Layout + Preview** | geometria por estado e escala (§4, §6); CLI que gera SVG | `Core/Display/GlanceLayout.cs`, `tools/FuelPace.Preview/**`, `Tests/Layout/*` | V3-7, V3-10 (geração) |
| 1 | **C · Renderer** | renderer desenhando `GlanceView` com a geometria do `GlanceLayout`; cache de `IDWriteTextLayout` por campo; selos, barra (com triângulo de saturação), pílulas; Inter 800 embutida; dirty flag por valor | `Agent/Overlay/**`, `Agent/Resources/Fonts/**` | V3-8 (PERF-06, teste Windows), compilação no Linux |
| 1 | **D · Config + Settings** | `schemaVersion 2` + migração (§9); seção "Layout" no Settings (§7); campos novos no `SettingsChanged` do IPC | `Core/Config/**`, `Core/Ipc/Messages.cs` (só campos de settings), `Settings/**`, `Tests/Config/*` | V3-9 |
| 2 | **Integração** | merge das 4 frentes na `feat/v3-glance`; resolver `contratos-pendentes.md`; ligar builder → renderer no Agent; rodar a suíte completa, o `FuelPace.Bench` (comparar o custo por tick com a v2.1, sem regressão > 10%) e o preview; atualizar README e changelog | qualquer um | suíte verde, bench registrado em `docs/perf/v3-bench.md` |
| 3 | **Revisor** (contexto limpo: não viu o desenvolvimento) | lê só este PRD, a v2.1, o diff e os SVGs do preview; confere cada critério V3, as regras da §10.0 da v2.1 (hot path sem LINQ, string, boxing ou `async`) e a fidelidade visual com as telas 9–12; escreve `docs/v3/revisao.md` com itens bloqueantes e não bloqueantes | só `docs/v3/revisao.md` | — |
| 3 | **Correção** | corrige os itens bloqueantes da revisão; o revisor relê. No máximo 2 ciclos. Depois disso, o que sobrar vai para a descrição do PR como pendência. | conforme os itens | — |

**Paralelismo:** A e B dependem só dos contratos. C depende de A e B só pelos contratos, então pode começar com um `GlanceView` fixo de teste. D é independente. Use um **git worktree por agente**. Se o plugin *superpowers* estiver disponível, siga `superpowers:using-git-worktrees` e `superpowers:subagent-driven-development`.

### 11.4 Verificação visual sem Windows
O agente B gera os 4 SVGs com os dados do V3-3 em `docs/v3/preview/`. O revisor os compara com as telas 9–12 do [canvas de mocks](https://claude.ai/artifact/Q6er7raafCpiD56Z1LC96Y) (se tiver acesso) ou, se não tiver, com as imagens em `docs/v3/mocks/` (quando existirem no repositório) e com as descrições das §4 e §6 deste PRD, que são a fonte da verdade em caso de dúvida. Se a ferramenta de leitura de imagens estiver disponível, o revisor converte cada SVG para PNG (por exemplo, com o Chromium/Playwright já instalado no ambiente) e inspeciona. Diferenças de pixel são aceitáveis. Diferenças de hierarquia, ordem, cor de estado ou elemento faltando são bloqueantes.

### 11.5 Entrega final
- **Um PR** de `feat/v3-glance` para a branch principal. A descrição traz: resumo, tabela critério V3 → teste correspondente (ou "manual Windows"), números do bench antes e depois, links dos 4 SVGs, `decisoes.md` resumido e pendências.
- O roteiro do teste V3-11 vai no PR como checklist para o Allan.

### 11.6 Prompt para iniciar (colar no Claude Code, na raiz do repositório)

```
Implemente o PRD diferencial docs/PRD-v3-leitura-rapida.md sobre o app atual (que segue o PRD v2.1).
Trabalhe de forma autônoma e sem me perguntar nada, seguindo à risca a §11:
1) Onda 0: crie a branch feat/v3-glance e os contratos (§8.1, §11.3) você mesmo.
2) Onda 1: dispare 4 subagentes em paralelo (A, B, C, D), cada um no próprio git worktree, com TDD e
   restritos aos arquivos da sua frente.
3) Onda 2: integre, rode testes, bench e preview.
4) Onda 3: dispare um subagente revisor com contexto limpo; corrija os bloqueantes (máx. 2 ciclos).
Registre decisões em docs/v3/decisoes.md em vez de perguntar. Pare apenas nos casos da §11.1 item 2.
No fim, abra um único PR com a descrição pedida na §11.5.
```

## 12. Decisões em aberto e riscos (delta da §15 da v2.1)

| # | Tema | Proposta |
|---|---|---|
| V3-D1 | Escala padrão da barra (±0,20) | Calibrar no teste V3-11. Em carros de consumo baixo (~1,5 L/v), ±0,20 é ~13%, talvez largo demais. Alternativa futura: escala em % do alvo. |
| V3-D2 | Folga do azul usando `ref` (volta atual) em vez de `MÉD` | `ref`, por coerência com o estado e com o `ECONOMIZE`. O número oscila um pouco durante a volta. Se incomodar, trocar por `MÉD` (uma linha no builder). |
| V3-D3 | Mostrar alvo e decisão sem baseline (alvo não depende da baseline, só a factibilidade depende) | Fora da v3: mantém o comportamento da v2.1 (SEM BASELINE = compacto). Candidato para a v3.1. |
| V3-D4 | `faltam` como inteiro (`ceil`) | Adotado por legibilidade. O cálculo usa o valor fracionado. |
| V3-R1 | Autonomia diferente do widget nativo do iRacing (25,2 × 26,1 v no teste de Motegi) | Esperado: o FuelPace desconta a reserva de 0,5 L e usa `MÉD` do stint. Logar os dois valores numa sessão longa para confirmar. Não é bloqueante. |
| V3-R2 | Painel 10 px mais alto que na v2.1 pode sobrepor algo já posicionado pelo piloto | A posição salva é o canto superior esquerdo. O painel cresce para baixo, e o piloto reposiciona no modo edição. |
