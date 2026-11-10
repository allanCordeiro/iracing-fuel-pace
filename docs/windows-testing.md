# FuelPace: guia de teste no Windows

Este guia vem no zip como `LEIA-ME.md`. Leva uns 15 minutos para o primeiro teste.

## 1. Baixar

- No GitHub: aba **Actions**, último run verde da branch `feat/fuelpace`, seção **Artifacts**, baixar `fuelpace-windows` (um zip; descompactado, tem a pasta `out/` com os dois zips):
  - `FuelPace-<versão>-win-x64-debug.zip`: **use este para testar** (traz o modo debug ligado e o FakeSim).
  - `FuelPace-<versão>-win-x64.zip`: versão normal, sem debug.
- A página de **Releases** só passa a ter os zips quando uma versão (tag `v…`) for publicada.

## 2. Instalar

1. Descompacte numa pasta fixa, por exemplo `C:\FuelPace`.
2. Rode `FuelPace.Agent.exe`. O Windows SmartScreen pode avisar, porque o exe não é assinado: clique em **Mais informações › Executar assim mesmo**.
3. O ícone aparece na bandeja (perto do relógio). Clique com o botão direito para o menu; duplo clique abre as configurações.

## 3. iRacing em janela sem borda

O overlay não aparece sobre o modo tela cheia exclusivo. No iRacing, use **janela sem borda** (borderless). O Settings avisa se detectar tela cheia.

## 4. Modo debug

O zip `-debug` já vem com o arquivo `FuelPace.debug` ao lado do exe, que liga o modo debug. Com o zip normal, ligue em **Configurações › Diagnóstico › Modo debug**. No modo debug o menu da bandeja ganha **Inserir marcador**, **Gerar pacote de diagnóstico** e **Abrir pasta de logs**, e a telemetria completa da sessão é gravada.

## 5. Primeira sessão (captura)

1. Abra o iRacing e entre na pista. Clique no ícone da bandeja › **Abrir configurações** › **Captura**.
2. Marque **Capturar voltas** e rode de 8 a 15 voltas.
3. Desmarque e confira o resumo (voltas válidas, consumo médio, baseline).

## 6. Corrida

Use o overlay numa corrida (ou treino com limite de voltas/tempo). Ele mostra ALVO, VOLTA, ÚLT, MÉD e o insight.

## 7. Durante o teste: marcadores

Aperte **Ctrl+Shift+M** sempre que algo parecer errado (aparece um aviso "Marcador N" na bandeja). Se puder, anote o número e o que viu.

Outros atalhos: **Ctrl+Shift+F** modo edição (arrastar o overlay), **Ctrl+Shift+H** mostrar/ocultar. Dá para mudar em Configurações › Overlay e cálculo.

## 8. Depois: gerar o pacote

Bandeja › **Gerar pacote de diagnóstico** (ou Configurações › Diagnóstico › **Gerar pacote**). Sai um `FuelPace-diag-AAAAMMDD-HHMMSS.zip` na Área de Trabalho.

**Privacidade:** as gravações contêm o YAML da sessão, que tem os **nomes dos outros pilotos**. Mande o pacote **em particular**, nunca em issue ou fórum público. Se preferir, desmarque **Incluir gravações** para gerar um pacote sem elas (mas aí fica mais difícil reproduzir o problema).

## 9. Enviar

Copie o zip para a máquina Linux em `~/Documentos/dev/github/iracing-fuel-pace/feedback/` (a pasta é ignorada pelo git) e passe o nome do arquivo e as anotações dos marcadores.

## 10. Checklist

Marque o que funcionou; anote o que não.

**Critério 5: travado**
- [ ] Em modo travado, clicar sobre o overlay clica no iRacing (o clique passa).
- [ ] O overlay não aparece no Alt+Tab nem na barra de tarefas.

**Critério 6: posição, escala, opacidade**
- [ ] Posição, escala e as duas opacidades continuam iguais depois de fechar e abrir o Agent.
- [ ] Funciona em um segundo monitor (escolha em Overlay e cálculo › Monitor).
- [ ] Ocultar/mostrar (Ctrl+Shift+H) e modo edição (Ctrl+Shift+F) funcionam.

**Critério 7: reconexão**
- [ ] Fechar o iRacing e abrir de novo com o Agent aberto: o overlay volta a mostrar dados sozinho.

**Critério 12: captura**
- [ ] Ligar **Capturar voltas**, fechar o Settings, completar voltas, reabrir: as voltas aparecem no resumo e a captura continua ativa.
- [ ] A caixa **Capturar voltas** só existe no Settings (não há outro lugar).

**Os 5 estados do overlay**
- [ ] AZUL (selo NO ALVO) e VERMELHO (selo ACIMA).
- [ ] NÃO FACTÍVEL (selo PIT, "alvo X · mín possível Y").
- [ ] SEM BASELINE (`ALVO —`): carro/pista sem captura.
- [ ] INATIVO (esmaecido, só COMB): no pit, garagem ou replay.

## 11. Roteiro PERF-01 (impacto nos fps)

Meta: o app não pode derrubar o desempenho do iRacing (fps médio ≥ 99,5% do fps sem o app, 1% low ≥ 99%, sem mais quadros > 2× o frametime médio).

1. Baixe você mesmo o PresentMon (console) em https://github.com/GameTechDev/PresentMon/releases. O script não baixa nada. Se o exe do iRacing tiver outro nome, use `-ProcessName <nome.exe>`.
2. Mesmo carro, pista e clima, replay ou test drive, iRacing em 120 fps. Faça **3 rodadas de 5 min sem o app** e **3 com o app** (Agent aberto, overlay visível).
3. Num PowerShell como administrador, com o iRacing na pista (o script está na pasta do app):
   `.\presentmon.ps1 -PresentMon C:\Caminho\PresentMon.exe -Label sem` e depois `-Label com`. Os CSVs ficam em `%APPDATA%\FuelPace\perf\` e entram no pacote de diagnóstico.
4. Compare cada par: `.\FuelPace.Replay.exe presentmon <com.csv> <sem.csv>`. A última linha diz `PERF-01: APROVADO` ou `REPROVADO`.

## 12. Testar sem o iRacing (FakeSim, só no zip debug)

Com o Agent aberto (ou abra depois), em outro terminal na pasta do app:

`.\FuelPace.FakeSim.exe --synthetic-race 20 --speed 1`

Simula uma corrida de 20 voltas com 20 carros. Outras opções: `--speed 10|max`, `--loop`, `--cars N`, `--disconnect-after <s>` (testa a reconexão) ou um arquivo `.ibt` no lugar de `--synthetic-race`.
