# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/). As versões só ganham data quando a tag `v…`
for publicada.

## [Não lançado] v3: overlay de leitura rápida

Especificação: [`docs/PRD-v3-leitura-rapida.md`](docs/PRD-v3-leitura-rapida.md). Decisões de implementação:
[`docs/v3/decisoes.md`](docs/v3/decisoes.md). Prévias: [`docs/v3/preview/`](docs/v3/preview/).

### Mudou
- O overlay foi redesenhado para leitura em menos de um segundo, em quatro faixas: **bloco de decisão** na cor do estado
  (`ECONOMIZE 0.08 −3%`, `OK FOLGA 2.7 L`, `PIT FALTAM 2.3 L`), **barra de delta** com a volta atual e a última em relação
  ao alvo, **apoio** (`VOLTA`, `ÚLT`, `MÉD`) e **autonomia** (`74.0 L · dá 31 v · faltam 32` com `ALVO`, `ATÉ` ou `alvo · MÍN`).
- Sem baseline (e no treino sem limite), o painel fica compacto: combustível, `dá N v · M min` e o apoio.
- REC, BoP diferente e alvo estimado (caution) viram pílulas embaixo do painel.
- A prévia do Settings mostra o painel novo e segue as opções na hora.
- O `settings.json` passa para o esquema 2; o arquivo da v2.1 é migrado sozinho na primeira abertura, sem perder nada (o log
  registra a migração; um arquivo ilegível continua indo para `settings.json.corrupt`).

### Adicionado
- Seção **LAYOUT** em Configurações › Overlay e cálculo: barra de delta (liga/desliga), escala da barra (±0,05 a ±0,50 L/v,
  padrão ±0,20), complemento do vermelho em `%` ou `s/v`, e `MÉD` no apoio (liga/desliga).

### Removido
- A linha de insight (e a opção que a escondia): o que ela dizia está no bloco de decisão e nas pílulas.

### Sem mudança
- As fórmulas do motor de estratégia, a histerese de 1,5 s, a captura e os perfis.
- Orçamento de desempenho: zero alocação por tick e no máximo 10 redesenhos por segundo (nenhum com o carro parado).
