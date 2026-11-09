# PRD — Overlay de Pace & Combustível para iRacing

> **Nome de trabalho:** FuelPace (provisório)
> **Autor:** Allan · **Data:** 2026-11-09 · **Status:** v2.1, pronto para implementação no Claude Code
>
> **v2 (2026-11-09):** stack decidida (C#/.NET 10, Agent sem WPF); orçamento de desempenho para 120 fps; captura só na janela de configurações; corrida por tempo pelo ritmo do líder.
> **v2.1 (2026-11-09):** layout visual aprovado a partir dos mocks (§7.1): a linha do ALVO ganha fundo colorido + selo com texto, no lugar da barra lateral; não factível usa selo branco `PIT`.
>
> **Mocks aprovados:** https://claude.ai/artifact/Q6er7raafCpiD56Z1LC96Y (canvas privado; os 8 estados/telas citados neste PRD)
> **Plataforma:** Windows 10/11, rodando sobre o iRacing (modo janela sem borda)

---

## 1. Contexto e objetivo

Em corridas e stints longos, o piloto precisa saber, volta a volta, **se o consumo atual de combustível fecha a conta** até o fim da corrida (ou do stint) e **quanto precisa economizar** se não fechar. Os overlays de mercado (ex.: Kapps, cujo desenvolvimento está suspenso) mostram números brutos, mas não comparam o consumo com o **histórico real do próprio piloto naquele carro e naquela pista**.

O produto tem duas partes:

1. **Captura** — o piloto liga uma caixa "Capturar" e o app registra as voltas (as mesmas variáveis que o iRacing grava no `.ibt`). Com um volume de voltas válidas, ele monta uma **baseline** por *carro + pista*: consumo por volta, tempo médio de volta, dispersão, autonomia do tanque etc.
2. **Overlay** — durante a pilotagem, mostra o combustível, o consumo atual e médio, o **alvo de L/volta** necessário para terminar, um **sinalizador azul/vermelho** e um **insight curto** baseado na baseline. Se o alvo estiver abaixo do que é possível fazer, mostra **NÃO FACTÍVEL**.

**Princípio de design:** simples, conciso, sem poluição visual. Cada número precisa ser legível de relance a 250 km/h. Se uma informação não muda decisão, ela não entra na tela.

## 2. Escopo

### No MVP
- Leitura da telemetria ao vivo via iRacing SDK (memória compartilhada).
- Captura de voltas com validação automática e persistência local por carro+pista.
- Cálculo de baseline e estratégia de combustível (alvo, autonomia, factibilidade).
- Overlay único de combustível, transparente, sempre no topo, com posição, tamanho e opacidade configuráveis.
- Janela de configurações (onde fica a captura) + ícone na bandeja do sistema.
- Corrida por tempo calculada pelo **ritmo do líder** (§7.3).
- Orçamento de desempenho rígido: **zero impacto perceptível em 120 fps** (§11).
- **Modo replay**: alimentar o motor com um arquivo `.ibt` como se fosse ao vivo (para desenvolvimento e testes sem o iRacing aberto).

### Fase 2 (fora do MVP)
- Importar arquivos `.ibt` antigos para formar baseline (mesmo pipeline do replay).
- Mapear ações (travar/ocultar overlay) para botões do volante (DirectInput).
- Overlay secundário de pace (deltas por setor).
- Exportar baseline (CSV/JSON) e compartilhar entre pilotos de um time.

### Fora de escopo
- Standings, relative, radar, mapa da pista.
- Qualquer comunicação com servidores (tudo local e offline).
- Carros elétricos (unidade kWh) — o app deve detectar e avisar que não há suporte.

## 3. Usuário e cenário de uso

**Usuário:** piloto de iRacing (GT3, GTP, protótipos), corridas por tempo e por voltas, incluindo endurance com stints.

**Fluxo típico:**
1. No treino, ainda no box, o piloto abre a **janela de configurações** (Alt+Tab, segundo monitor ou ícone da bandeja), marca **Capturar** e volta ao jogo. A captura continua ativa com a janela fechada ou minimizada.
2. Faz 8–15 voltas no ritmo de corrida. O overlay mostra apenas `● REC n/m`.
3. De volta à janela de configurações, desmarca **Capturar**. O app mostra o resumo: voltas válidas, consumo médio, tempo médio, autonomia do tanque.
4. Na corrida, o overlay usa essa baseline para dizer se o consumo atual fecha a conta até o fim, e quanto economizar se não fechar.

## 4. Conceitos e definições

| Termo | Definição |
|---|---|
| **Perfil** | Chave `CarPath + TrackName` (ex.: `ligierjsp320` + `zolder alt`). O `TrackName` do iRacing já inclui a configuração da pista. |
| **Volta válida** | Volta completa que passa em todos os critérios da §6.3. Só voltas válidas entram na baseline. |
| **Baseline** | Agregado das voltas válidas das capturas ativas de um perfil (média, mediana, desvio, mínimo de L/volta e de tempo). |
| **Consumo da volta** | `FuelLevel` na passagem pela linha de chegada menos `FuelLevel` na passagem seguinte. |
| **Voltas a percorrer (`L_rest`)** | Quantas voltas (fracionadas) ainda faltam até a bandeirada ou até o fim do stint (§7.3). |
| **Alvo (L/v)** | Consumo médio máximo por volta que permite terminar sem parar: `(combustível − reserva) / L_rest`. |
| **Limite factível** | Menor consumo por volta que o piloto consegue sustentar de forma realista (§7.4). |

## 5. Fonte de dados

### 5.1 Telemetria ao vivo
O iRacing publica a telemetria a 60 Hz num arquivo mapeado em memória (`Local\IRSDKMemMapFileName`), com um evento de "dados prontos". As variáveis e o YAML de sessão são **os mesmos** que vão para o `.ibt`. Por isso a captura não depende de o piloto ligar a gravação de telemetria (Alt+L): o app lê ao vivo e grava a própria versão resumida.

### 5.2 Variáveis usadas (validadas no `.ibt` anexo)

**Telemetria (por amostra):**

| Variável | Unidade | Uso |
|---|---|---|
| `SessionTime` | s | relógio base, interpolação de passagem na linha |
| `Lap`, `LapCompleted` | — | detecção de virada de volta |
| `LapDistPct` | 0–1 | posição na volta, projeção do consumo da volta atual |
| `LapLastLapTime` | s | tempo oficial da volta (ver nota de latência §6.2) |
| `FuelLevel` | L | combustível no tanque |
| `FuelLevelPct` | 0–1 | conferência |
| `FuelUsePerHour` | kg/h | consumo instantâneo (opcional, para suavizar a projeção) |
| `OnPitRoad`, `PlayerCarInPitStall` | bool | invalidar out-lap/in-lap |
| `PlayerTrackSurface` | enum TrkLoc | fora da pista (`0 = OffTrack`) |
| `PlayerCarMyIncidentCount` | int | incidentes na volta |
| `SessionFlags` | bitfield | amarela/caution invalida a volta |
| `IsOnTrack`, `PlayerCarTowTime` | — | reset/reboque invalida a volta |
| `SessionTimeRemain` | s | corrida por tempo |
| `SessionLapsRemainEx` | int | corrida por voltas (`32767` = ilimitado) |
| `SessionState` | enum | ignorar formação/warmup/cooldown |
| `Throttle` | 0–1 | % de pé embaixo por volta (indicador de economia) |
| `TrackTempCrew`, `AirTemp`, `TrackWetness` | °C / enum | condições da captura |
| `PlayerCarPowerAdjust`, `PlayerCarWeightPenalty` | % / kg | BoP no momento da captura |

**Telemetria por carro (arrays `[64]` indexados por `CarIdx`, só ao vivo, usados para o ritmo do líder):**

| Variável | Uso |
|---|---|
| `CarIdxPosition` | posição geral; o líder é quem tem `== 1` |
| `CarIdxLapCompleted`, `CarIdxLapDistPct` | distância percorrida por cada carro |
| `CarIdxLastLapTime` | tempo da última volta de cada carro (buffer do ritmo) |
| `CarIdxOnPitRoad`, `CarIdxTrackSurface` | descartar voltas de pit e de carro fora do mundo (`-1`) |
| `CarIdxClass` | informativo (multiclasse) |

> ⚠️ O `.ibt` **não grava** as variáveis `CarIdx*`: o arquivo anexo não tem nenhuma. A lógica do líder precisa de testes com dados sintéticos (§13) e de validação ao vivo.

**YAML de sessão (`SessionInfo`):**

| Campo | Exemplo no `.ibt` | Uso |
|---|---|---|
| `DriverInfo.Drivers[DriverCarIdx].CarPath` | `ligierjsp320` | chave do perfil |
| `CarScreenName` | `Ligier JS P320` | exibição |
| `WeekendInfo.TrackName` | `zolder alt` | chave do perfil |
| `TrackDisplayName` / `TrackConfigName` | `Zolder Circuit` / `Alternate` | exibição |
| `TrackLength` | `3.9342 km` | informativo |
| `DriverCarFuelMaxLtr` × `DriverCarMaxFuelPct` | `100.0 × 1.000` | **capacidade útil do tanque** |
| `DriverCarFuelKgPerLtr` | `0.750` | converter `FuelUsePerHour` (kg/h → L/h) |
| `DriverCarEstLapTime` | `78.9567` | fallback de tempo de volta sem baseline |
| `SessionInfo.Sessions[n].SessionType` / `SessionLaps` / `SessionTime` | `Practice` / `unlimited` / `7200 sec` | tipo e limite da sessão |

## 6. Módulo 1 — Captura

### 6.1 Requisitos funcionais

| ID | Requisito | Prioridade |
|---|---|---|
| CAP-01 | Caixa de seleção **Capturar** **apenas na janela de configurações** (fora do jogo). Nada de controle de captura no overlay nem atalho global. A captura roda em segundo plano, independentemente da janela estar aberta. | MVP |
| CAP-02 | Com a captura ligada, o overlay mostra um indicador discreto `● REC` e o contador `n válidas / m total`. A janela de configurações, se aberta, mostra as voltas entrando em tempo real. | MVP |
| CAP-03 | A captura só registra voltas **completas** dentro do intervalo ligado. A volta em andamento quando a captura é ligada é descartada. A volta em andamento quando é desligada também é descartada. | MVP |
| CAP-04 | Cada volta é validada automaticamente (§6.3) e guardada **com o motivo** de invalidação, se houver. | MVP |
| CAP-05 | Ao desligar, mostrar um resumo na janela de configurações: tabela de voltas (✓/✗ + motivo), consumo médio/mediana/mínimo, tempo médio/mediana, autonomia do tanque em voltas e em minutos. | MVP |
| CAP-06 | O piloto pode **excluir ou reincluir manualmente** uma volta do resumo. | MVP |
| CAP-07 | Captura vale para qualquer tipo de sessão (treino, quali, corrida). | MVP |
| CAP-08 | Persistir por perfil (§9). Várias capturas por perfil; o piloto pode ativar ou desativar cada captura para a baseline e apagar capturas. | MVP |
| CAP-09 | Guardar as condições da captura: temperatura da pista e do ar, pista molhada, BoP (power adjust, peso), capacidade do tanque. Se a sessão atual tiver BoP ou tanque diferentes, o overlay avisa `baseline com BoP diferente`. | MVP |
| CAP-10 | Guardar o **perfil de consumo ao longo da volta**: a fração acumulada do consumo em 100 pontos de `LapDistPct`, como média das voltas válidas. É usado na projeção da volta atual (§7.2). | MVP |
| CAP-11 | Importar `.ibt` (pasta `Documentos\iRacing\telemetry`), passando pelo mesmo pipeline de detecção e validação. | Fase 2 |

### 6.2 Detecção de volta e medição
- **Virada de volta:** quando `Lap` incrementa, ou quando `LapDistPct` cai de > 0,9 para < 0,1. Interpolar `SessionTime` e `FuelLevel` linearmente no ponto exato de `LapDistPct = 0`.
- **Consumo da volta** = `FuelLevel(início) − FuelLevel(fim)`, medido pela interpolação acima. O `FuelLevel` tem resolução bem fina (~0,0006 L por amostra), então não precisa suavizar.
- **Tempo da volta:** usar `LapLastLapTime`. **Atenção:** no `.ibt` analisado, esse valor só atualiza **1,3 a 2,0 s depois** de `Lap` incrementar. A implementação precisa esperar a atualização (com timeout de 5 s). Se ela não vier, usar o tempo medido por `SessionTime` interpolado. Valores `≤ 0` significam volta sem tempo (out-lap, por exemplo).
- **Reabastecimento:** qualquer aumento de `FuelLevel` acima de 0,05 L dentro da volta marca a volta como inválida (`refuel`).

### 6.3 Critérios de volta válida
Uma volta é **inválida** se qualquer um destes ocorrer (o motivo fica registrado):

| Código | Regra |
|---|---|
| `pit` | `OnPitRoad` verdadeiro em algum momento da volta (pega out-lap e in-lap) |
| `incident` | `PlayerCarMyIncidentCount` aumentou durante a volta |
| `offtrack` | `PlayerTrackSurface == OffTrack` por mais de 0,5 s acumulados |
| `caution` | `SessionFlags` com yellow, caution ou caution waving durante a volta |
| `reset` | `IsOnTrack` falso, `PlayerCarTowTime > 0` ou salto de `LapDistPct` incompatível com a velocidade |
| `refuel` | aumento de combustível na volta |
| `incomplete` | volta não terminou dentro da captura |
| `outlier` | tempo > 107% da mediana das demais válidas (recalcular ao fim da captura) |
| `manual` | excluída pelo piloto |

Voltas de economia intencional (lift & coast) **são válidas**. Elas alimentam o limite factível (§7.4) e a estimativa de custo em tempo (§7.5).

### 6.4 Agregados da baseline
Calculados a partir das voltas válidas das capturas ativas:
- consumo: média, mediana, desvio-padrão, mínimo, P10 e P90 (L/volta)
- tempo de volta: média, mediana, desvio-padrão, melhor
- % de pé embaixo médio por volta
- autonomia: `capacidade / consumo médio` (voltas) e × tempo médio (minutos)
- relação consumo × tempo: regressão linear `tempo = a + b·consumo`, só se houver ≥ 8 voltas válidas, amplitude de consumo ≥ 2% e pelo menos 2 voltas abaixo da mediana − 1 desvio (voltas de economia de verdade). Senão, `null`.
- **confiança:** `baixa` (3–4 voltas), `média` (5–9), `alta` (≥ 10). Com menos de 3 voltas válidas não existe baseline.

## 7. Módulo 2 — Overlay de combustível

### 7.1 Layout (4 linhas, ~300 × 110 px na escala 100%)

```
┌───────────────────────────────────────┐
│ COMB  77.6 L            32.4 v · 43min│  ← combustível e autonomia na média atual
│ VOLTA 2.41   ÚLT 2.39   MÉD 2.38  L/v │  ← projeção da atual, última, média móvel
│ ALVO  2.31 L/v  faltam 34.0 v [ACIMA] │  ← linha com fundo colorido + selo
│ Economize 0.08 L/v (−3%) · ≈ +0.9 s/v │  ← um insight por vez
└───────────────────────────────────────┘
                                ● REC 6/8   ← só quando capturando
```

- **Sinalizador (aprovado nos mocks):** a **linha do ALVO** ganha fundo na cor do estado (~26% de opacidade) e um **selo com texto** à direita: `NO ALVO` (azul) ou `ACIMA` (vermelho). O texto garante leitura mesmo para quem confunde cores. O valor `VOLTA` usa a cor do estado em tom claro, e `ÚLT` usa um tom mais suave da mesma regra (azul ≤ alvo, vermelho > alvo). `COMB`, `MÉD` e rótulos ficam neutros.
- **Paleta:**

  | Uso | Cor |
  |---|---|
  | Fundo do overlay | `rgba(10,12,16, opacidade)`, padrão 60% |
  | Texto principal / rótulos | `#F2F4F7` / `#A9B2BD` |
  | Azul: selo / fundo da linha / número | `#1E6BD6` / `rgba(47,128,237,0.26)` / `#6AAEFF` (`ÚLT` `#9CC8FF`) |
  | Vermelho: selo / fundo da linha / número | `#C7362F` / `rgba(235,87,87,0.28)` / `#FF7A7A` (`ÚLT` `#FFA8A8`) |
  | Não factível: selo / fundo da linha | `#F2F4F7` com texto `#0E1013` / `rgba(255,255,255,0.12)` |
  | REC | ponto `#FF5A5A` |

- **Fontes:** JetBrains Mono (números) e Inter (rótulos e insight), embutidas no app. Proporções em 1×: valor de `COMB` ~21 px, demais valores ~16 px, `ALVO` ~18 px, rótulos ~9 px em caixa alta, insight ~11 px. A escala do usuário multiplica tudo.
- **REC:** pílula separada, logo abaixo do overlay e alinhada à direita, com o mesmo fundo: `● REC 4/5 válidas`.
- **Modo edição:** contorno tracejado branco, alças nos 4 cantos e rodapé com `X · Y · escala` e a dica do atalho para travar.
- Linha opcional de **pace** (desligada por padrão): `ÚLT 79.51 s · MÉD 80.14 · base 80.12`.
- Tipografia: fonte monoespaçada para números (sem "pulo" de layout); números com 2 casas em L/v e 1 casa em L.
- Redesenho só quando algum valor visível muda, no máximo 10×/s (§10.0). Detecção de volta a 60 Hz, estratégia a 10 Hz.

### 7.2 Valores exibidos

| Campo | Cálculo |
|---|---|
| `COMB` | `FuelLevel` |
| Autonomia | `(FuelLevel − reserva) / MÉD` em voltas; × tempo médio em minutos |
| `VOLTA` (projeção) | `usado_na_volta / perfil_ref(LapDistPct)`, onde `perfil_ref` é a curva acumulada da baseline (CAP-10). Sem baseline, usar a fração linear `LapDistPct`. Exibir `—` enquanto `LapDistPct < 0,15` (projeção instável) e na volta de pit. |
| `ÚLT` | consumo da última volta completa (mesmo que inválida, com marcador `*` se inválida) |
| `MÉD` | média móvel das últimas N voltas válidas do stint atual (N configurável, padrão 5). Antes de ter 2 voltas no stint, usar a média da baseline, em itálico. |
| `ALVO` | `(FuelLevel − reserva) / L_rest` |
| `faltam` | `L_rest` |

> **Por que a projeção usa o perfil e não regra de três:** no `.ibt` analisado, o consumo não é uniforme ao longo da volta (Zolder Alt). Em 50% da volta só foram gastos ~46% do combustível, e em 60%, ~54%. Na metade da volta, a regra de três projeta **~2,22 L para uma volta real de ~2,39 L (−7%)**, o que daria **AZUL falso** justamente quando o piloto está acima do alvo. Com o perfil médio das outras voltas, o erro cai para **±0,01–0,03 L**.

### 7.3 Voltas a percorrer (`L_rest`)
Modo **automático**, conforme a sessão:
- **Por voltas** (`SessionLapsRemainEx < 32767`): `L_rest = SessionLapsRemainEx − LapDistPct + 1`. ⚠️ Validar a semântica exata de `SessionLapsRemainEx` (se inclui ou não a volta atual) numa corrida real antes de fechar a fórmula.
- **Por tempo** (`SessionTimeRemain` válido): **o critério é o ritmo do líder geral.** Quando o tempo zera, o líder completa a volta em que está e recebe a bandeirada. Cada carro termina ao cruzar a linha depois disso. Em multiclasse, quem define o fim é o líder **geral** (`CarIdxPosition == 1`), não o da sua classe.

  Variáveis: `p_L` = `CarIdxLapDistPct` do líder, `t_L` = ritmo do líder, `p_me` = seu `LapDistPct` e `t_me` = seu ritmo (média móvel do stint; fallback: baseline).

  1. Voltas que o líder ainda completa: `N_L = ceil(p_L + T_rem / t_L) − p_L`.
  2. Tempo até a bandeirada do líder: `T_fin = N_L × t_L`.
  3. Suas voltas a percorrer: `L_rest = ceil(p_me + T_fin / t_me) − p_me`.

  Se você é o líder, isso se reduz à fórmula com o seu próprio ritmo.

  **Ritmo do líder (`t_L`):**
  - O app mantém um buffer circular de tempos de volta de **todos os carros** (64 × N, alocado uma única vez) alimentado por `CarIdxLastLapTime` a cada `CarIdxLapCompleted` novo. Assim, uma troca de líder já tem histórico disponível.
  - Volta descartada se o carro passou pelo pit (`CarIdxOnPitRoad`), se o tempo for ≤ 0 ou se passar de 107% da mediana do próprio carro.
  - `t_L` = média das últimas N voltas válidas do líder atual (N = mesmo da média móvel, padrão 5).
  - Fallbacks em ordem: (a) com menos de 2 voltas válidas do líder, usar a melhor volta dele × 1,02; (b) líder no pit ou sem dados, manter o último `t_L` válido; (c) sem nenhum dado (largada), usar o próprio ritmo.
  - Em bandeira amarela ou safety car (`SessionFlags` caution), **congelar** `t_L` (voltas de caution não entram no buffer). O app mostra `ALVO` com `~` para indicar estimativa.
- **Voltas extras de segurança** (configurável, padrão 0): margem manual para cenários que o modelo não pega, como um líder que acelera no fim.
- **Treino** (laps `unlimited`): usar o tempo restante, ou mostrar `—` se o piloto preferir.

Modo **manual / stint** (configurável no painel e por atalho): o piloto define quantas voltas quer completar com o combustível atual (ex.: stint de 25 voltas no endurance). O overlay mostra `STINT 25` no lugar de `faltam`.

**Reserva** (configurável, padrão 0,5 L): margem que nunca entra na conta.

### 7.4 Estados do sinalizador

| Estado | Condição | Visual |
|---|---|---|
| **AZUL** | `consumo_ref ≤ ALVO` | linha do ALVO com fundo azul + selo `NO ALVO`; `VOLTA` azul |
| **VERMELHO** | `consumo_ref > ALVO` | linha do ALVO com fundo vermelho + selo `ACIMA`; `VOLTA` vermelho |
| **NÃO FACTÍVEL** | `ALVO < limite_factível` | linha com fundo cinza claro; `NÃO FACTÍVEL` em destaque no lugar de `ALVO`, com `alvo X · mín possível Y` abaixo; selo branco `PIT`; `VOLTA`/`ÚLT` neutros; insight mostra quanto falta |
| **SEM BASELINE** | perfil sem baseline | linha neutra com `ALVO —`; insight `Sem baseline para <carro> · <pista>` |
| **INATIVO** | no pit, garagem, replay, sessão sem limite e modo auto sem tempo | overlay esmaecido, só `COMB` |

- `consumo_ref` = projeção da volta atual (`VOLTA`) quando disponível; senão `ÚLT`.
- A comparação é feita **nos valores arredondados a 0,01 L** (o que o piloto vê). Assim, "igual" é sempre azul, como pedido.
- **Histerese:** só troca de cor se o novo estado se mantiver por 1,5 s (evita piscar).
- `limite_factível = min( baseline_média × (1 − economia_max), baseline_mínimo )`. `economia_max` é configurável (padrão **6%**). O `baseline_mínimo` é a volta mais econômica realmente registrada. Assim, se o piloto já provou que consegue gastar menos, isso conta.

### 7.5 Insights (uma linha, por prioridade)
Só um insight aparece por vez: o de maior prioridade.

1. **Não factível:** `Faltam 3.4 L · parada necessária` (`L_rest × limite_factível − (FuelLevel − reserva)`).
2. **BoP/tanque diferente da baseline:** `Baseline com BoP diferente`.
3. **Vermelho:** `Economize 0.08 L/v (−3%)`. Se houver regressão consumo × tempo (§6.4), acrescentar `· ≈ +0.9 s/v`.
4. **Azul com folga** (alvo ≥ média + 1 desvio-padrão): `Folga 1.2 L · pode atacar`.
5. **Azul justo:** `No alvo · margem 0.3 L`.
6. **Sem baseline:** `Sem baseline — ative Capturar`.

## 8. Configurações do overlay

| ID | Requisito | Prioridade |
|---|---|---|
| CFG-01 | **Posição:** arrastar o overlay em modo edição. Campos X/Y numéricos na janela de configurações. Suporte a vários monitores (guardar o monitor + coordenadas relativas). | MVP |
| CFG-02 | **Tamanho:** escala de 50% a 250% (slider + campo), mantendo a proporção. Redimensionar pelos cantos em modo edição. | MVP |
| CFG-03 | **Opacidade:** dois controles separados, fundo (0–100%, padrão 60%) e conteúdo/texto (30–100%, padrão 100%). | MVP |
| CFG-04 | **Modo travado (padrão):** click-through, não rouba foco, não aparece na barra de tarefas. **Modo edição:** borda tracejada, arrastável e redimensionável (sem nenhum controle além disso). Atalho `Ctrl+Shift+F` alterna. | MVP |
| CFG-05 | Mostrar/ocultar overlay: `Ctrl+Shift+H`. Ocultar automaticamente quando o iRacing não está rodando ou o carro não está na pista (opcional). | MVP |
| CFG-06 | Parâmetros de cálculo: reserva (L), N da média móvel, economia máxima (%), voltas extras de segurança, modo auto/manual e voltas do stint. | MVP |
| CFG-07 | Linhas opcionais: ligar/desligar a linha de pace e a linha de insight. | MVP |
| CFG-08 | Todos os atalhos são reconfiguráveis. | MVP |
| CFG-09 | Ações em botões do volante (DirectInput). | Fase 2 |
| CFG-10 | Configurações persistem e são reaplicadas na abertura. | MVP |

**Observação para o usuário final:** o iRacing precisa estar em **janela sem borda (borderless)**. Overlays não aparecem sobre o modo tela cheia exclusivo. O app deve detectar isso quando possível e mostrar a dica na janela de configurações.

## 9. Persistência

Pasta: `%APPDATA%\FuelPace\`
- `settings.json`: posição, escala, opacidades, atalhos e parâmetros.
- `profiles\{carPath}__{trackName}.json`: um arquivo por perfil (espaços → `_`).

Esquema do perfil (resumido):

```json
{
  "schemaVersion": 1,
  "car": { "path": "ligierjsp320", "name": "Ligier JS P320", "tankCapacityL": 100.0, "fuelKgPerL": 0.75 },
  "track": { "name": "zolder alt", "display": "Zolder Circuit", "config": "Alternate", "lengthKm": 3.9342 },
  "captures": [
    {
      "id": "2026-10-29T20:03:40Z",
      "active": true,
      "sessionType": "Practice",
      "conditions": { "trackTempC": 21.7, "airTempC": 20.3, "wetness": 1, "powerAdjustPct": 0, "weightPenaltyKg": 0 },
      "laps": [
        { "lap": 16, "timeS": 80.897, "fuelL": 2.382, "fullThrottlePct": 63.6, "valid": true,  "reasons": [] },
        { "lap": 19, "timeS": 93.175, "fuelL": 2.388, "fullThrottlePct": 55.3, "valid": false, "reasons": ["offtrack", "incident"] }
      ],
      "fuelProfile": [0.0, 0.008, "... 101 pontos, fração acumulada por 1% de LapDistPct ..."]
    }
  ],
  "baseline": { "computedAt": "...", "validLaps": 6, "fuelMeanL": 2.373, "fuelMedianL": 2.386, "fuelMinL": 2.289,
                "fuelStdL": 0.043, "lapMeanS": 80.140, "lapMedianS": 80.118, "confidence": "media",
                "fuelVsTime": null }
}
```

A baseline é derivada e pode ser recalculada a partir das capturas a qualquer momento.

## 10. Arquitetura e stack (decidido: C#)

**Decisão:** C# / **.NET 10 (LTS)**. O .NET 8 sai de suporte em 10/11/2026, então não vale começar nele. Desempenho é requisito de primeira classe: o piloto roda o iRacing a **120 fps** e o app **não pode causar nenhuma queda** (orçamento na §11).

Para isso, o app é dividido em **dois processos**:

| Processo | O que faz | Tecnologia | Quando roda |
|---|---|---|---|
| **`FuelPace.Agent.exe`** | lê telemetria, detecta e valida voltas, captura, calcula estratégia, desenha o overlay | Win32 puro + **Direct2D/DirectWrite + DirectComposition** via `Vortice.Windows`; sem WPF | sempre (inicia com o Windows ou manualmente; ícone na bandeja) |
| **`FuelPace.Settings.exe`** | janela de configurações, caixa **Capturar**, resumo das capturas, gestão de perfis | WPF | só quando aberto pelo piloto; fecha por completo ao sair |

- Os dois se comunicam por **named pipe** (`\\.\pipe\FuelPace`), com mensagens pequenas em JSON: `SetCapture(on/off)`, `CaptureProgress`, `SettingsChanged`, `ProfileUpdated`. O Agent é a fonte da verdade. Se o Settings fechar, a captura continua.
- **Por que não WPF no overlay:** uma janela WPF transparente (`AllowsTransparency`) usa janela *layered* com cópia da GPU para a CPU a cada redesenho, e carrega o runtime WPF inteiro (~60–100 MB). Com DirectComposition + `WS_EX_NOREDIRECTIONBITMAP`, o DWM compõe o overlay direto na GPU, sem cópia, e o processo fica enxuto.
- **Por que não IRSDKSharper:** o leitor do SDK é pequeno e o layout é idêntico ao do `.ibt` (§10.1). Um leitor próprio garante **zero alocação por tick** e lê só as ~35 variáveis usadas. Ele reaproveita o mesmo código do parser de `.ibt`.

```
FuelPace.sln
├─ FuelPace.Core              (net10.0, SEM dependência de Windows — testável em Linux/CI)
│   ├─ Telemetry/TelemetryFrame.cs       ← struct blittable com só os campos usados (inclui arrays CarIdx[64])
│   ├─ Telemetry/VarLayout.cs            ← resolve offsets por nome 1x por conexão/arquivo
│   ├─ Telemetry/ITelemetrySource.cs     ← abstrai fonte (ao vivo | replay .ibt)
│   ├─ Telemetry/IbtReader.cs            ← parser binário do .ibt (§10.1) + IbtTelemetrySource (replay)
│   ├─ Telemetry/SessionInfoParser.cs    ← YAML (YamlDotNet), só quando sessionInfoUpdate muda
│   ├─ Laps/LapDetector.cs               ← virada de volta, interpolação, latência LapLastLapTime
│   ├─ Laps/LapValidator.cs              ← regras §6.3
│   ├─ Race/LeaderPaceTracker.cs         ← buffers circulares 64×N, ritmo do líder (§7.3)
│   ├─ Capture/CaptureSession.cs
│   ├─ Baseline/BaselineCalculator.cs    ← agregados §6.4 + fuelProfile
│   ├─ Strategy/FuelStrategyEngine.cs    ← L_rest, alvo, estado, insight (§7) — função pura
│   ├─ Storage/ProfileStore.cs           ← JSON em disco, escrita atômica, fora da thread de telemetria
│   └─ Ipc/Messages.cs                   ← contratos do named pipe
├─ FuelPace.Agent             (net10.0-windows)
│   ├─ Live/IRacingMemoryReader.cs       ← MemoryMappedFile + evento IRSDKDataValidEvent
│   ├─ Overlay/OverlayWindow.cs          ← Win32 + DirectComposition + Direct2D/DirectWrite
│   ├─ Overlay/OverlayRenderer.cs        ← desenha OverlayState; redesenha só se mudou
│   ├─ Tray/TrayIcon.cs, Hotkeys.cs, IpcServer.cs
├─ FuelPace.Settings          (net10.0-windows, WPF) ← configurações, captura, resumo, perfis
├─ FuelPace.Tests             (xUnit) ← Core com a fixture .ibt + testes de alocação
└─ FuelPace.Bench             (BenchmarkDotNet) ← custo por tick do pipeline completo
```

### 10.0 Regras de desempenho (obrigatórias no código)
**Thread de telemetria (hot path):**
- Thread dedicada que **espera** o evento `Local\IRSDKDataValidEvent` (`WaitOne` com timeout de 200 ms, que também serve para detectar desconexão). Nada de polling nem *spin*.
- Ponteiro para a view do `MemoryMappedFile` obtido uma única vez (`AcquirePointer`). A cada tick: escolher o `varBuf` com maior `tickCount`, copiar os campos usados para um `TelemetryFrame` pré-alocado e conferir se o `tickCount` não mudou durante a cópia (leitura rasgada → descarta).
- **Zero alocação por tick:** sem LINQ, sem `string`, sem boxing, sem closures, sem `async` no hot path. Buffers e arrays são alocados na conexão.
- O hot path faz só aritmética: detecção de volta, acumuladores e buffers do líder a 60 Hz. O `FuelStrategyEngine` roda a **10 Hz**.
- O YAML de sessão só é lido quando `sessionInfoUpdate` muda, e numa thread de fundo.

**Renderização:**
- Redesenhar **somente quando algum texto ou cor visível mudar** (dirty flag), no máximo **10 vezes/s**. Sem animações, sombras, blur ou gradientes.
- `IDWriteTextLayout` em cache por campo; recriar só o campo que mudou. Fonte monoespaçada carregada uma vez.
- Swap chain de composição com 2 buffers, `Present(0, 0)` só quando dirty. Não usar VSync próprio nem loop de render contínuo.
- Reassertar o *topmost* por evento (`SetWinEventHook` com `EVENT_SYSTEM_FOREGROUND`) e por timer de 2 s, nunca por frame.

**Processo:**
- Prioridade do processo `BelowNormal`. Não mexer em afinidade de CPU. **Nunca** chamar `timeBeginPeriod` (altera o timer do sistema inteiro).
- `GCSettings.LatencyMode = SustainedLowLatency`; GC de workstation; `InvariantGlobalization = true`; publicar com ReadyToRun. Native AOT do Agent é opcional: testar se `Vortice` e `YamlDotNet` são compatíveis e adotar se reduzir memória e tempo de partida.
- I/O de disco (gravar volta capturada, salvar perfil) via fila para uma thread de fundo, nunca na thread de telemetria.
- Log assíncrono com buffer; nível `Info` sem log por tick.

**Regras gerais:**
- Toda a lógica de cálculo vive no `Core` e é **determinística** (entrada: `TelemetryFrame` + baseline + config; saída: `OverlayState`, uma struct). O renderer só desenha `OverlayState`.
- `IbtTelemetrySource` reproduz um `.ibt` amostra por amostra, em velocidade 1×, 10× ou máxima, pelo mesmo caminho do ao vivo. Isso permite desenvolver e testar sem iRacing.
- Reconexão automática: se o iRacing fechar ou reabrir, o Agent volta a ler sem reiniciar.

### 10.1 Formato `.ibt` (validado no arquivo anexo)
Todos os valores são little-endian.
- **Cabeçalho (112 bytes):** `ver, status, tickRate, sessionInfoUpdate, sessionInfoLen, sessionInfoOffset, numVars, varHeaderOffset, numBuf, bufLen` (10 × int32), 2 × int32 de padding, e depois `varBuf[4]` de `{tickCount, bufOffset, pad[2]}`. No `.ibt`, os dados começam em `varBuf[0].bufOffset`.
- **Sub-cabeçalho de disco (offset 112):** `sessionStartDate (int64), sessionStartTime (double), sessionEndTime (double), sessionLapCount (int32), sessionRecordCount (int32)`.
- **Cabeçalhos de variável (144 bytes cada):** `type, offset, count (int32)`, `countAsTime (bool + 3 pad)`, `name[32]`, `desc[64]`, `unit[32]`. Tipos: `0 char, 1 bool, 2 int, 3 bitfield, 4 float, 5 double`.
- **YAML:** em `sessionInfoOffset`, com `sessionInfoLen` bytes, terminado em `\0`. Encoding latin-1.
- **Registros:** `sessionRecordCount` linhas de `bufLen` bytes cada.
- **Ao vivo (memória compartilhada `Local\IRSDKMemMapFileName`):** mesmo cabeçalho e mesmos cabeçalhos de variável, sem o sub-cabeçalho de disco. Há `numBuf` buffers rotativos (normalmente 3). Ler o de maior `tickCount` e conferir que ele não mudou após a cópia. `status & 1` indica conectado. O YAML é reescrito no lugar e `sessionInfoUpdate` incrementa a cada mudança.

## 11. Requisitos não-funcionais e orçamento de desempenho

**Meta do produto:** com o iRacing travado em 120 fps, **ligar o app não pode mudar o desempenho do jogo de forma mensurável.**

| ID | Requisito | Como medir |
|---|---|---|
| PERF-01 | **Impacto no jogo:** fps médio com o app ≥ 99,5% do fps sem o app; *1% low* ≥ 99%; nenhum aumento no nº de frames > 2× o frametime médio | PresentMon/CapFrameX, 3 corridas de 5 min com e sem o app (mesmo carro, pista e clima, replay ou test drive) |
| PERF-02 | CPU do Agent: média ≤ 0,5% de **um** núcleo; nenhum tick > 1 ms na thread de telemetria | contadores de desempenho do Windows + cronômetro interno (p99/máx por minuto, expostos no log) |
| PERF-03 | **0 bytes alocados por tick** no hot path | teste xUnit: `GC.GetAllocatedBytesForCurrentThread()` antes/depois de 10.000 ticks de replay da fixture = 0 |
| PERF-04 | Nenhuma coleta de GC gen0 em 30 min de pilotagem (captura desligada); ≤ 1 por volta capturada | `GC.CollectionCount(0)` no log |
| PERF-05 | Memória do Agent (working set) ≤ 60 MB, estável (sem crescimento) em 2 h | Gerenciador de Tarefas / log |
| PERF-06 | Overlay: ≤ 10 redesenhos/s; 0 redesenhos quando nada muda | contador interno |
| PERF-07 | Settings fechado = zero custo (processo encerrado) | lista de processos |
| NFR-01 | Latência entre telemetria e tela < 150 ms | replay com marca de tempo |
| NFR-02 | Nunca roubar foco do iRacing nem capturar input em modo travado | teste manual |
| NFR-03 | Funciona 100% offline; nenhum dado sai da máquina | — |
| NFR-04 | Per-monitor DPI aware (v2); texto nítido em 1080p, 1440p e 4K | teste manual |
| NFR-05 | Arquivos de perfil gravados de forma atômica (temp + rename) para não corromper em crash | teste |
| NFR-06 | Log local rotativo (`%APPDATA%\FuelPace\logs`), incluindo métricas PERF-02/04/05 a cada minuto | — |

## 12. Dados de referência (fixture de teste)

Arquivo: `ligierjsp320_zolder_alt_2026-10-29_20-03-40.ibt` (51,7 MB, 60 Hz, 47.106 amostras, ~13 min, treino de 2 h com laps ilimitados). Ligier JS P320 em Zolder Alternate (3,934 km), tanque de 100 L e saída com o tanque cheio.

| Volta | Tempo (s) | Consumo (L) | Válida | Motivo |
|---|---|---|---|---|
| 15 | — (out-lap) | 2.378 | ✗ | `pit`, `incident` (+1) |
| 16 | 80.897 | 2.382 | ✓ | |
| 17 | 80.272 | 2.404 | ✓ | |
| 18 | 79.964 | 2.373 | ✓ | |
| 19 | 93.175 | 2.388 | ✗ | `offtrack` (~4,6 s), `incident` (+2) |
| 20 | 79.133 | 2.386 | ✗ | `incident` (+1); fora da pista só ~0,12 s, abaixo do limiar |
| 21 | 79.514 | 2.403 | ✓ | |
| 22 | 79.134 | 2.389 | ✓ | |
| 23 | 81.062 | 2.289 | ✓ | (volta mais econômica: 58% de pé embaixo, contra 64–67% nas voltas de ataque) |
| 24 | — | 0.986 (parcial) | ✗ | `incomplete` |

**Baseline esperada (6 voltas válidas):** consumo médio **2.373 L/v**, mediana 2.386, mínimo 2.289, desvio 0.043. Tempo médio **80.140 s**, mediana 80.118. Autonomia de ~**42,1 voltas / ~56 min** com 100 L. Confiança **média**. `fuelVsTime = null` (só 6 voltas válidas e uma única volta de economia, abaixo do mínimo da §6.4).

**Insights que este arquivo já mostra (bom para validar o motor):**
- O consumo em ritmo de ataque é muito estável (2,37–2,40 L/v). Ficar fora da pista e tomar incidente quase não muda o consumo (volta 19: 2,388 L). Por isso o tempo de volta é um critério ruim para invalidar consumo, e a regra de `incident`/`offtrack` é a que importa.
- A volta 23 economizou **~0,10 L (−4%)** ao custo de **~+1,1 s** em relação à média das voltas de ataque. Esse é o tipo de relação que alimenta o insight `Economize X · ≈ +Y s/v`.
- O consumo não é linear na volta (ver §7.2), o que justifica o perfil de consumo.
- O `.ibt` começa no meio da sessão (`SessionTime` = 4264 s) e no meio de uma volta (`LapDistPct` = 0,067, saída do box). Não assumir que a primeira amostra é o início da sessão nem da volta.

> O `.ibt` tem 51 MB. Não versionar no git: colocar em `tests/fixtures/` com `.gitignore` (ou Git LFS). Os testes que dependem dele devem ser ignorados se o arquivo não existir.

## 13. Critérios de aceite (MVP)

1. Rodando o replay da fixture com a captura ligada do início ao fim, o resumo mostra **exatamente** as voltas válidas e os motivos da tabela §12, com consumo e tempo dentro de ±0,005 L e ±0,01 s.
2. A baseline gerada bate com os valores da §12 (±0,005).
3. Teste unitário do `FuelStrategyEngine` com a baseline da fixture, `FuelLevel = 50 L`, reserva 0,5 L e consumo de referência 2.38:
   - `L_rest = 19` → alvo **2.61 L/v**, estado **AZUL**.
   - `L_rest = 22` → alvo **2.25**, estado **VERMELHO**, insight `Economize 0.13 L/v (−5%)` (sem custo em segundos, porque `fuelVsTime = null`).
   - `L_rest = 23` → alvo 2.15 < limite factível `min(2.373 × 0,94; 2.289) = 2.23` → **NÃO FACTÍVEL**, `Faltam 1.8 L · parada necessária`.
4. `FuelLevel = 48.1`, reserva 0,5, `L_rest = 20` → alvo 2.38 e consumo 2.38 (ou 2.384, que arredonda para 2.38) → **AZUL**.
5. Em modo travado, cliques passam para o iRacing; o overlay não aparece no Alt+Tab nem na barra de tarefas.
6. Posição, escala e as duas opacidades persistem entre reinícios e funcionam em um segundo monitor.
7. Fechar e reabrir o iRacing com o app aberto: o overlay volta a mostrar dados sem intervenção.
8. Projeção da volta atual na fixture (leave-one-out: perfil das outras voltas válidas): em `LapDistPct = 0,5` e `0,6`, o erro absoluto é ≤ 0,07 L em todas as voltas válidas, enquanto a regra de três erra > 0,13 L.
9. **Ritmo do líder** (teste sintético do `LeaderPaceTracker` + `FuelStrategyEngine`). Entrada: corrida por tempo, `T_rem = 600 s`, líder em `p_L = 0,30` com `t_L = 100 s`, você em `p_me = 0,80` com `t_me = 102 s`. Esperado:
   - `N_L = 6,7` → `T_fin = 670 s` → `L_rest = ceil(0,80 + 6,569) − 0,80 = 7,2`. Com o próprio ritmo daria 6,2 (uma volta a menos).
   - Com `FuelLevel = 18 L` e reserva 0,5 → alvo **2,43 L/v**.
   - Se você é o líder (`p_L = p_me`, `t_L = t_me`), o resultado é idêntico ao cálculo pelo próprio ritmo.
10. **Troca de líder:** ao trocar o carro com `CarIdxPosition == 1`, `t_L` passa a usar o buffer do novo líder no mesmo tick, sem voltar ao fallback. Volta de pit do líder e voltas sob caution não entram no buffer.
11. **Desempenho:** PERF-01 a PERF-07 atendidos. PERF-03 e PERF-06 rodam como teste automatizado no CI. PERF-01 é medido pelo piloto no Windows com o roteiro da §11, e o resultado fica registrado no repositório (`docs/perf/`).
12. A caixa **Capturar** existe só no Settings. Ligar a captura, fechar o Settings, completar voltas e reabrir o Settings: as voltas aparecem no resumo e a captura continua ativa.

## 14. Plano de entrega (para o Claude Code)

Trabalhar com TDD a partir do `Core`, usando a fixture.

1. **Fase A — Core e dados:** `IbtReader` + `SessionInfoParser` + testes de leitura (variáveis, contagem de registros, campos do YAML).
2. **Fase B — Voltas:** `LapDetector` + `LapValidator`, contra a tabela §12 (critério de aceite 1).
3. **Fase C — Baseline e persistência:** `BaselineCalculator`, `fuelProfile`, `ProfileStore` (critério 2).
4. **Fase D — Estratégia:** `FuelStrategyEngine` como função pura, com testes de tabela (critérios 3, 4 e 8).
5. **Fase D2 — Líder:** `LeaderPaceTracker` + integração na fórmula por tempo (critérios 9 e 10), com dados sintéticos.
6. **Fase D3 — Desempenho do Core:** teste de alocação zero (PERF-03) e benchmark por tick no `FuelPace.Bench`. Isso entra **antes** da UI, para travar o padrão do hot path cedo.
7. **Fase E — Fonte ao vivo:** `IRacingMemoryReader` (MemoryMappedFile + evento) e reconexão (critério 7). Só testável no Windows com iRacing.
8. **Fase F — Overlay:** Agent com Win32 + DirectComposition + Direct2D (transparência, click-through, modo edição, escala, opacidades, dirty-redraw), tray e atalhos (critérios 5 e 6, PERF-06).
9. **Fase G — Settings + IPC:** app WPF, named pipe, caixa Capturar, resumo e gestão de perfis (critério 12).
10. **Fase H — Polimento e medição:** histerese, estados INATIVO/SEM BASELINE, aviso de BoP, logs com métricas e rodada de PresentMon (critério 11).

As fases A–D3 rodam em qualquer SO (inclusive no ambiente do Claude Code). As fases E–H exigem Windows.

## 15. Decisões em aberto e riscos

| # | Tema | Proposta atual |
|---|---|---|
| D1 | ~~Stack~~ | **Decidido:** C# / .NET 10. Agent em Win32 + Direct2D/DirectComposition; Settings em WPF num processo separado. |
| D2 | Semântica de `SessionLapsRemainEx` (inclui a volta atual?) | Validar numa corrida real por voltas e ajustar a fórmula da §7.3. |
| D3 | ~~Critério da corrida por tempo~~ | **Decidido:** ritmo do líder geral (§7.3), no MVP. |
| D6 | Ritmo do líder em stints com estratégia de pit diferente (ex.: líder vai parar e você não) | MVP ignora: o modelo projeta a partir do ritmo de pista do líder. A perda de tempo no pit do líder fica para a Fase 2. |
| D7 | Native AOT no Agent | Testar na Fase H; adotar se as dependências forem compatíveis e houver ganho de memória e partida. |
| D4 | Valor padrão de `economia_max` | 6%. Calibrar com mais capturas (a fixture mostra −4% com custo de ~1 s). |
| D5 | Baseline mistura ou separa capturas com condições diferentes (temperatura/BoP) | MVP: todas as capturas ativas; o piloto desativa as que não quer. |
| R1 | O iRacing em tela cheia exclusiva esconde o overlay | Documentar e detectar; orientar a usar borderless. |
| R2 | Carros híbridos (GTP): consumo afetado pelo modo de deploy | Guardar o modo/mapa (`dc*` disponíveis) na captura, numa fase futura; no MVP, apenas um aviso na doc. |
| R3 | Anticheat/termos do iRacing | Leitura do SDK é suportada oficialmente; o app não escreve nada no sim. |
| R4 | DirectComposition é mais trabalhoso que WPF | Escopo do overlay é pequeno (4 linhas de texto, 1 linha com fundo colorido e 1 selo). Isolar em `OverlayRenderer` para poder trocar a implementação sem tocar no Core. |
| R5 | Lógica do líder não testável com `.ibt` (sem `CarIdx*`) | Testes sintéticos + gravação própria de `CarIdx*` pelo Agent em modo debug, para gerar fixtures de corrida reais. |
