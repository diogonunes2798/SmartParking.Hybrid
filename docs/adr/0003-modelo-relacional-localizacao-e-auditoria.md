# ADR-0003 — Modelo relacional, localização dos parques e auditoria

- **Estado:** aceite
- **Data:** 14-11-2026
- **Fase:** 2a (Domínio, EF Core e RF-06)
- **Relaciona-se com:** ESPECIFICACAO.md §3 (domínio e mapeamento), §4 (RF-06, UC-02, UC-03), §6 (RNF-06); D1; ADR-0001

## Contexto

A Fase 2a cria o esquema relacional: configuração (parques, lugares, sensores, tarifários), utilizadores, veículos, sessões, pagamentos e auditoria. Quatro escolhas têm consequências para fases seguintes e para a tese.

1. **Localização.** A §3 diz «localização (GeoJSON)». Na Fase 4, o UC-03 vai ler a disponibilidade do Redis com comandos GEO, por isso as coordenadas têm de poder ser carregadas com `GEOADD` sem conversões nem rejeições.
2. **Regras de tarifário.** A especificação só diz «regras de cálculo». O modelo definitivo ainda não existe (`TODO(Diogo)`).
3. **Concorrência e identidade.** O UC-02 exige concorrência otimista (§4).
4. **Auditoria (RNF-06).** É preciso registar as alterações sem perder o registo e sem guardar dados pessoais em claro (RGPD).

## Decisão

1. **Localização: o value object `GeoPoint` em duas colunas `double precision` (`latitude` e `longitude`).**
   - Na API, a localização é um GeoJSON `Point` (RFC 7946), `{"type":"Point","coordinates":[lon, lat]}`. A conversão é feita em `GeoJsonPoint` (Application).
   - O domínio e as restrições `CHECK` limitam a latitude a ±85,05112878°. É o limite do Redis GEO (projeção Web Mercator), mais restrito do que os ±90° do WGS 84. Assim, nenhuma localização guardada é rejeitada pelo `GEOADD`.
   - A ordem longitude-latitude do GeoJSON é a mesma do `GEOADD key lon lat member`. Um teste de integração protege essa ordem.
2. **Regras de tarifário: documento `jsonb` tipado** (*complex type* do EF Core 10 com `ToJson`).
   - O modelo provisório tem preço por hora, minutos grátis e teto diário, em EUR.
   - Quando o modelo do Cap. 3 for definido, a mudança fica no domínio e no JSON, sem migração relacional.
3. **Chaves e concorrência:**
   - os identificadores são UUIDv7, gerados na aplicação (`Guid.CreateVersion7()`). Não precisam de ida à base de dados e são ordenáveis por criação **ao milissegundo**: o `Guid.CreateVersion7()` do .NET não tem contador dentro do mesmo milissegundo, por isso ids criados no mesmo milissegundo (por exemplo, as linhas de auditoria de um `SaveChanges`) ficam em ordem aleatória entre si. A paginação continua estável (a ordem é total), e os índices B-tree continuam sem fragmentação significativa (correção após a revisão independente, achado 7);
   - o token de concorrência é a coluna de sistema `xmin` do PostgreSQL. Não acrescenta nenhuma coluna. A API expõe-na como `version`, e um PUT com uma versão desatualizada devolve 409;
   - os enums são guardados como texto;
   - todas as chaves estrangeiras usam `ON DELETE RESTRICT`. Eliminar um recurso com dependentes devolve 409, e nada desaparece em cascata.
4. **Auditoria:**
   - um `SaveChangesInterceptor` acrescenta uma linha em `audit_log` por entidade criada, alterada ou eliminada, na **mesma transação** da alteração;
   - o campo `changes` (`jsonb`) guarda:
     - os valores novos, numa criação;
     - os valores antigos, numa eliminação;
     - o par `{old, new}` só dos campos alterados, numa alteração;
   - os campos marcados `[PersonalData]` no domínio (nome e e-mail do utilizador, matrícula) ficam mascarados como `***`;
   - o ator vem de `ICurrentActor`. Enquanto não houver autenticação, é `anonymous`; é o ponto de extensão para o RBAC.
5. **D1 (sessões):** valor por omissão, relacional, atrás de `IParkingSessionRepository`. Um índice único parcial (`spot_id WHERE status = 'Open'`) garante na base de dados que cada lugar tem no máximo uma sessão aberta.
   - **Limite do ponto de extensão (revisão independente, achado 4).** O contrato atual pressupõe o *change tracking* do EF Core:
     - `IRepository<T>` não tem `Update` nem gravação própria;
     - os casos de uso alteram o agregado carregado e chamam `IUnitOfWork.SaveChangesAsync`, que só grava o contexto EF.
   - Uma implementação em MongoDB não veria o `Close()` nem o `MarkPaid()`, e não seria atómica com a outbox relacional.
   - Por isso, trocar o D1 para NoSQL exige mais do que uma nova classe:
     - acrescentar ao contrato uma gravação explícita (por exemplo, `UpdateAsync`);
     - decidir como garantir a atomicidade com a outbox (por exemplo, uma outbox no próprio MongoDB).
   - Não se alterou o contrato agora, porque o D1 está «EM ABERTO» e o valor por omissão não precisa disso. `TODO(Diogo):` rever este ponto se o orientador escolher sessões em NoSQL.

## Alternativas consideradas

- **GeoJSON guardado em `jsonb`.**
  - Vantagem: é fiel ao texto da §3.
  - Foi rejeitada por três razões:
    - obriga a fazer *parsing* para cada `GEOADD`;
    - não permite validar com um `CHECK` simples;
    - torna as consultas por coordenadas desajeitadas na variante monolítica (§7).
- **PostGIS (`geography(Point, 4326)`).**
  - Vantagens: tem índices espaciais e polígonos.
  - Foi rejeitada para já:
    - obriga a mudar a imagem fixada por digest no compose e nos Testcontainers;
    - acrescenta o NetTopologySuite;
    - nesta arquitetura, as consultas espaciais são feitas no Redis (UC-03).
  - Fica como trabalho futuro, se os parques passarem a ser polígonos ou se a variante monolítica precisar de consultas espaciais eficientes.
- **Coluna `version` explícita**, em vez de `xmin`. Seria portável para outros motores, mas acrescenta uma coluna e lógica de incremento. O protótipo é só para PostgreSQL.
- **Auditoria por *triggers* em PostgreSQL.**
  - Vantagem: é independente da aplicação.
  - Foi rejeitada porque:
    - não conhece o ator nem as marcas `[PersonalData]` do domínio;
    - duplicaria o mapeamento em SQL.
- **Tabelas temporais ou *event sourcing* da configuração.** São desproporcionados para RF-06.
- **Eliminação lógica (*soft delete*).** Foi rejeitada (decisão P3 da Fase 2a). A auditoria já guarda os valores eliminados, e a eliminação física com `RESTRICT` protege as referências.

## Consequências

- **Positivas:**
  - O carregamento do *read model* GEO (Fase 4) é direto.
  - Os invariantes críticos ficam na base de dados, e não só no código:
    - coordenadas;
    - capacidade;
    - unicidade do código do lugar por parque e de um sensor por lugar;
    - uma única sessão aberta por lugar.
  - A auditoria é atómica com a alteração.
- **Negativas:**
  - A localização é só um ponto; não há polígonos.
  - O `audit_log` cresce sem limite. A retenção fica para a Fase 10 (RNF-05), e o índice em `occurred_at` prepara a purga.
  - A auditoria só cobre as escritas feitas através do EF Core. SQL direto (por exemplo, em migrações de dados) não fica auditado.
  - A tabela não está protegida contra `UPDATE`/`DELETE` ao nível de permissões da base de dados (ver o diário, «pontos em aberto»).
- **A rever:**
  - o contrato de `IParkingSessionRepository`, se o D1 mudar para NoSQL (ponto 5);
  - o modelo de regras do tarifário (`TODO(Diogo)`);
  - a lista de `VehicleType` (`TODO(Diogo)`);
  - a emissão de eventos de configuração para o *read model* (§4, RF-06), que depende da outbox (Fase 2b/5).
