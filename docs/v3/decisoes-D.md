# Decisões da frente D (contexto → escolha → motivo)

- Branch do worktree saiu da `main` → reset para `feat/v3-glance` antes de começar → os contratos da Onda 0 e a fixture v1-real só existem lá.
- `schemaVersion` ausente no JSON desserializa como o padrão (2) → a versão é lida do texto (`PeekSchemaVersion`) antes de decidir migrar → senão um v1 sem o campo nunca migraria.
- `schemaVersion` com tipo errado (texto) → continua indo para `.corrupt` com padrões → é o comportamento do binder para qualquer tipo errado; não vale um caso especial.
- Falha ao regravar o arquivo migrado (IO/acesso) → engole, devolve as configurações migradas em memória e tenta migrar de novo na próxima leitura → o arquivo original continua intacto e nada é perdido.
- `schemaVersion` > 2 → lido como está (campos desconhecidos ignorados), nunca regravado, `SchemaVersion` mantém o valor lido → um app mais novo não perde dados por causa deste.
- `Load` expõe `LastLoadMigratedFrom` (int?) e `LastLoadWasCorrupt` → o Agent loga na integração; ambos zerados a cada `Load`.
- Complemento do vermelho na UI → dois botões de rádio (`%` / `s/v`) ligados a `RedComplementSeconds` → mesmo padrão do modo de voltas já existente.
- Escala da barra → o texto `±0.20 L/v` usa InvariantCulture (ponto) → igual ao resto dos números da página (ruling A25); o campo aceita vírgula ou ponto via `NumberInput`.
- `OverlayViewOptions.ShowInsightLine` permanece (arquivo da frente B/integração) → a prévia e o `AgentHost` passam `false`.
