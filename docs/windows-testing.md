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
3. O ícone aparece na bandeja (perto do relógio). Clique com o botão **direito** para o menu; **duplo clique** abre as configurações. Um clique simples com o botão esquerdo não faz nada.
4. O ponteiro sobre o ícone mostra `FuelPace <versão>` (ou `FuelPace (debug) <versão>` com o modo debug ligado).

## 3. iRacing em janela sem borda

O overlay não aparece sobre o modo tela cheia exclusivo. No iRacing, use **janela sem borda** (borderless). O Settings avisa se detectar tela cheia.

## 3a. Quando o overlay aparece (importante)

A opção **Ocultar fora da pista** (Configurações › Overlay e cálculo) vem **ligada**. Com ela, o overlay só aparece com o iRacing aberto **e** o estado ativo: o carro na pista, numa sessão com limite de voltas ou de tempo. Ele some:

- sem o iRacing (ou antes de o iRacing entrar numa sessão);
- depois de **mais de 3 s** em INATIVO: pit lane, box, garagem, fora do carro, **replay** e sessões **sem limite** (como Test Drive).

Ou seja: no primeiro teste, na garagem ou num Test Drive, só o ícone da bandeja aparece, e isso **não é defeito**. Para ver o overlay parado, aperte **Ctrl+Shift+F** (o modo edição sempre mostra) ou desmarque **Ocultar fora da pista**.

**Para o checklist (seção 10) e para o PERF-01 (seção 11):** desmarque **Ocultar fora da pista** e use uma sessão **com limite de voltas ou de tempo** (treino offline com tempo, ou corrida contra a IA). Assim o overlay fica na tela o tempo todo.

## 4. Modo debug

O zip `-debug` já vem com o arquivo `FuelPace.debug` ao lado do exe, que liga o modo debug. Nesse caso a caixa **Modo debug** do Settings aparece marcada e desabilitada, com o aviso "Ativado pelo arquivo FuelPace.debug" (para desligar, apague o arquivo e reabra o Agent). Com o zip normal, ligue em **Configurações › Diagnóstico › Modo debug**. No modo debug o menu da bandeja ganha **Inserir marcador**, **Gerar pacote de diagnóstico** e **Abrir pasta de logs**, e a telemetria completa da sessão é gravada.

**Espaço em disco**, tudo em `%APPDATA%\FuelPace\`:
- `logs\`: arquivos de 20 MB, **no máximo 200 MB** no total (os mais antigos são apagados).
- `recordings\` (só no modo debug): cerca de 450 MB por hora com 64 carros, **no máximo `MaxRecordingsGB`** (padrão **2 GB**, em `settings.json`, `debug.maxRecordingsGB`); as mais antigas são apagadas.
- Para liberar espaço, feche o Agent (Bandeja › Sair) e apague as pastas.

## 5. Primeira sessão (captura)

1. Abra o iRacing e entre na pista. Botão direito no ícone da bandeja › **Abrir configurações** (ou duplo clique no ícone) › **Captura**.
2. Marque **Capturar voltas** e rode de 8 a 15 voltas.
3. Desmarque e confira o resumo (voltas válidas, consumo médio, baseline).

## 6. Corrida

Use o overlay numa corrida (ou treino com limite de voltas/tempo). Ele mostra ALVO, VOLTA, ÚLT, MÉD e o insight.

## 7. Durante o teste: marcadores

Aperte **Ctrl+Shift+M** sempre que algo parecer errado (aparece um aviso "Marcador N" na bandeja). Se puder, anote o número e o que viu.

Outros atalhos: **Ctrl+Shift+F** modo edição (arrastar o overlay), **Ctrl+Shift+H** mostrar/ocultar. Dá para mudar em Configurações › Overlay e cálculo. Confira se nenhum deles é um atalho seu no iRacing: o atalho registrado pelo FuelPace não chega ao jogo.

## 8. Depois: gerar o pacote

Bandeja › **Gerar pacote de diagnóstico** (ou Configurações › Diagnóstico › **Gerar pacote**). Sai um `FuelPace-diag-AAAAMMDD-HHMMSS.zip` na Área de Trabalho.

**Privacidade:** as gravações contêm o YAML da sessão, com o **seu nome e o seu ID do iRacing** e os **nomes e IDs dos outros pilotos**. Mande o pacote **em particular**, nunca em issue ou fórum público. Se preferir, desmarque **Incluir gravações** (Configurações › Diagnóstico) para gerar um pacote sem elas (mas aí fica mais difícil reproduzir o problema). Os logs não têm nomes de pilotos.

## 9. Enviar

Copie o zip para a máquina Linux em `~/Documentos/dev/github/iracing-fuel-pace/feedback/` (a pasta é ignorada pelo git) e passe o nome do arquivo e as anotações dos marcadores.

## 10. Checklist

Antes: desmarque **Ocultar fora da pista** e use uma sessão com limite de voltas ou de tempo (seção 3a). Marque o que funcionou; anote o que não.

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
- [ ] INATIVO (esmaecido, só COMB): no pit, garagem ou replay (com **Ocultar fora da pista** desmarcado; marcado, ele some depois de 3 s).

**Ocultar fora da pista** (marque de novo para este item)
- [ ] Entrar no pit lane ou na garagem: o overlay fica esmaecido e some uns 3 s depois; volta sozinho na pista.

## 11. Roteiro PERF-01 (impacto nos fps)

Meta: o app não pode derrubar o desempenho do iRacing (fps médio ≥ 99,5% do fps sem o app, 1% low ≥ 99%, sem mais quadros > 2× o frametime médio). São 6 rodadas de 5 min: 3 **sem** o app e 3 **com** o app.

### 11.1 Preparar (uma vez)

1. Descompacte o **zip normal** (`FuelPace-<versão>-win-x64.zip`, **sem** `-debug`) numa pasta própria, por exemplo `C:\FuelPacePerf`. O modo debug grava a telemetria e registra muito mais, e não é o que se mede. Atenção: o `fuelpace-windows` baixado do GitHub traz os dois zips dentro de `out\`. O `presentmon.ps1` fica **dentro** do zip do app, não na pasta do download.
2. Baixe o PresentMon em https://github.com/GameTechDev/PresentMon/releases: o arquivo `PresentMon-<versão>-x64.exe` (a versão de console, não o instalador). **Coloque o exe na mesma pasta** (`C:\FuelPacePerf`). O script acha sozinho qualquer `PresentMon*.exe` nessa pasta (ou em Downloads).
3. Rode o `FuelPace.Agent.exe` dessa pasta e confira que o ponteiro sobre o ícone da bandeja mostra `FuelPace <versão>` **sem** "(debug)". Em Configurações, desmarque **Ocultar fora da pista** (seção 3a).

A pasta fica assim:

```
C:\FuelPacePerf\
  FuelPace.Agent.exe
  FuelPace.Replay.exe
  presentmon.ps1
  PresentMon-2.6.0-x64.exe
  ...
```

### 11.2 Abrir o PowerShell como administrador

Menu Iniciar › digite `powershell` › botão direito em **Windows PowerShell** › **Executar como administrador**. Nele, vá para a pasta (copie e cole):

```
cd C:\FuelPacePerf
```

### 11.3 Medir

Mesmo carro, pista e clima, numa sessão **com limite de voltas ou de tempo** (treino offline com tempo ou corrida contra a IA; **não** replay nem Test Drive), iRacing em 120 fps, em janela sem borda. Com o carro **já na pista**, cole o comando e continue pilotando; ele para sozinho depois de 5 min e diz `Pronto: <arquivo>.csv`.

- Rodada **sem** o app: feche o Agent antes (Bandeja › Sair) e use

  ```
  powershell -ExecutionPolicy Bypass -File .\presentmon.ps1 -Label sem
  ```

- Rodada **com** o app: abra o Agent (`FuelPace.Agent.exe`), confira o overlay na tela e use

  ```
  powershell -ExecutionPolicy Bypass -File .\presentmon.ps1 -Label com
  ```

  Se o overlay sumir durante a rodada, ela não vale.

Repita até ter 3 de cada. Os CSVs ficam em `%APPDATA%\FuelPace\perf\` e entram no pacote de diagnóstico.

Por que esse comando e não só `presentmon.ps1`: o PowerShell só roda um script da pasta atual com `.\` na frente, e o Windows bloqueia scripts por padrão. O `-ExecutionPolicy Bypass` libera **só esta execução**, sem mudar nada no sistema. Se o PresentMon estiver em outra pasta, acrescente `-PresentMon "C:\caminho\PresentMon-2.6.0-x64.exe"`.

### 11.4 Comparar

Cada par (um `com` e um `sem`):

```
.\FuelPace.Replay.exe presentmon "$env:APPDATA\FuelPace\perf\com-AAAAMMDD-HHMMSS.csv" "$env:APPDATA\FuelPace\perf\sem-AAAAMMDD-HHMMSS.csv"
```

A última linha diz `PERF-01: APROVADO` ou `REPROVADO`. Ou simplesmente gere o pacote de diagnóstico (seção 8) e mande: ele leva os CSVs. Anote que o zip medido foi o normal.

## 12. Testar sem o iRacing (FakeSim, só no zip debug)

**Feche o iRacing antes.** O FakeSim usa a mesma memória compartilhada do iRacing; com o iRacing aberto ele se recusa a rodar (mensagem "already exists… close iRacing first", código de saída 2) e não escreve nada. Também não abra o iRacing com o FakeSim rodando. Entre duas execuções do FakeSim, espere uns 3 s (o Agent precisa perceber que o anterior saiu).

Com o Agent aberto (ou abra depois), em outro terminal na pasta do app:

`.\FuelPace.FakeSim.exe --synthetic-race 20 --speed 1`

Simula uma corrida de 20 voltas com 20 carros. Outras opções: `--speed 10|max`, `--loop`, `--cars N`, `--disconnect-after <s>` (testa a reconexão) ou um arquivo `.ibt` no lugar de `--synthetic-race`.

## 13. O overlay não aparece

Vá na ordem:

1. **O Agent está aberto?** O ícone está na bandeja (talvez escondido na setinha `^`) e o ponteiro sobre ele mostra `FuelPace <versão>` ou `FuelPace (debug) <versão>`.
2. **Ocultar fora da pista** (seção 3a): sem o iRacing, na garagem, no pit, em replay ou em Test Drive o overlay some. Aperte **Ctrl+Shift+F**: no modo edição ele sempre aparece (aperte de novo para travar).
3. **Ocultado pelo atalho?** **Ctrl+Shift+H** (ou Bandeja › Mostrar/ocultar overlay) alterna.
4. **Tela cheia exclusiva:** se a bandeja avisou "tela cheia exclusiva" (ou a faixa de aviso no topo do Settings), mude o iRacing para janela sem borda.
5. **Outro monitor:** confira **Overlay e cálculo › Monitor**.
6. **Backend de desenho:** **Configurações › Diagnóstico** mostra o **BACKEND DO OVERLAY** (`composition` ou `layered`). Se o overlay continua invisível com o modo edição ligado: feche o Agent (Bandeja › Sair), abra `%APPDATA%\FuelPace\settings.json` no Bloco de Notas e, dentro de `"overlay": { … }`, troque o valor de `"renderBackend"` por `"layered"` (exatamente assim, minúsculo e entre aspas: `"renderBackend": "layered"`). Se o arquivo não existir (nenhuma configuração foi mudada ainda), crie-o só com `{ "overlay": { "renderBackend": "layered" } }`. Salve e abra o Agent de novo; o Diagnóstico deve mostrar `layered`. Um valor escrito errado volta para `"auto"` sem apagar o resto das configurações.
7. **Log:** `%APPDATA%\FuelPace\logs\fuelpace-AAAAMMDD-N.jsonl` (Configurações › Diagnóstico › **Abrir pasta de logs**, ou Bandeja › Abrir pasta de logs no modo debug). Gere o pacote (seção 8) e mande junto com o que você viu.
