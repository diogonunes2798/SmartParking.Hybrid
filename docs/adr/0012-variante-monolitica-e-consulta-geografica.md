# ADR-0012 — Variante monolítica (só PostgreSQL) e consulta geográfica com PostGIS

- **Estado:** aceite
- **Data:** 16-11-2026
- **Fase:** 8 (variante monolítica)
- **Relaciona-se com:** ESPECIFICACAO.md §7 (baseline), §4 (UC-01, UC-03, UC-05), §6 (RNF-01, RNF-04); ADR-0001 (camadas), ADR-0003 (localização em duas colunas), ADR-0006 (bus), ADR-0007 (log append-only), ADR-0008 (read model), ADR-0010 (analítica), ADR-0011 (`payload` extensível)

> Nota de numeração: o guião da Fase 8 pedia o «ADR 0010». Esse número já pertence às tabelas de resumo (Fase 6), por isso este registo é o 0012. É a sexta fase seguida com este desencontro.

## Contexto

A secção 6.5 compara a arquitetura híbrida com uma variante só relacional. A comparação só é justa se a variante monolítica estiver bem feita, como a faria quem escolhesse um único PostgreSQL, e não propositadamente fraca. A §7 fixa três regras: a mesma API corre com `Persistence:Mode=Monolithic`, os eventos ficam numa tabela particionada por tempo, a disponibilidade é obtida por consulta, sem Redis, e o UC-05 faz-se com uma migração de esquema.

Antes do código, o Diogo decidiu quatro pontos, todos com a opção recomendada no plano:

| Ponto | Escolha | Alternativa recusada |
|---|---|---|
| Consulta geográfica do UC-03 | **PostGIS**, com índice espacial | fórmula de Haversine em SQL com pré-filtro por caixa |
| O que faz o `IEventBus` em PostgreSQL | **escrita síncrona**: publicar é gravar, numa transação, antes do 202 | fila em PostgreSQL com consumidores por *polling* |
| Como se calcula a disponibilidade | **tabela `spot_state`** mantida na mesma transação do evento | último evento por lugar calculado do log a cada pedido |
| Forma da tabela de eventos (UC-05) | **só colunas tipadas**, sem `jsonb` | coluna `payload jsonb` e migração que promove os campos |

## Decisão

1. **Só muda a infraestrutura.** O modo lê-se de `Persistence:Mode` (`Hybrid` por omissão; um valor desconhecido falha no arranque). Os dois *composition roots* (`Api/Program.cs` e `Workers/Program.cs`) escolhem as implementações:
   - **híbrido:** `AddDocumentPersistence` (MongoDB), `AddCacheStore` (Redis) e `AddEventBus` (Redis Streams), como até à Fase 7;
   - **monolítico:** `AddMonolithicPersistence` (`Infrastructure.Relational/Monolithic/`), com `PostgresEventBus` (`IEventBus`), `MonolithicOccupancyStore` (`IOccupancyEventLog`) e `PostgresAvailabilityQueries` (`IAvailabilityQueries`).

   O domínio, a aplicação e os *endpoints* não mudam. No modo monolítico, o MongoDB, o Redis, os consumidores de *streams*, o `ReadModelInitializer`, os projetores e o comando `rebuild-read-model` não são registados. A *readiness* verifica só o PostgreSQL.

2. **Ingestão síncrona (UC-01).** `PostgresEventBus.PublishAsync` grava o lote numa só instrução SQL (um CTE), portanto numa só transação:
   - insere os eventos em `monolith.occupancy_events` com `ON CONFLICT DO NOTHING`;
   - leva cada lugar **registado** ao seu evento mais recente em `monolith.spot_state`, só se for mais recente do que o guardado, pela ordem `("timestamp", event_id)`, a mesma do read model Redis (ADR-0008). Um evento de um lugar desconhecido fica no log, mas não dá estado ao lugar, como o `UnknownSpot` do projetor Redis (revisão da Fase 8, achado L2 da arquitetura).

   A API responde 202 depois do *commit*. Os outros tópicos (eventos de configuração e de domínio da outbox) não têm subscritores neste modo. O *relay* continua a correr e marca as linhas como processadas, para que o custo de escrita do UC-02 e do RF-06 (a linha da outbox na mesma transação) seja igual nas duas variantes. Um erro transitório do PostgreSQL (ligação, `lock_timeout`, `deadlock` depois de 3 tentativas) dá `EventBusUnavailableException`, ou seja, 503, como quando o Redis está em baixo.

3. **Tabela particionada (§7).** `monolith.occupancy_events` usa `PARTITION BY RANGE ("timestamp")`, com uma partição por mês UTC e uma partição `DEFAULT`, criadas por SQL na migração `AddMonolithicSchema`:
   - **Chave primária `(event_id, "timestamp")`.** O PostgreSQL obriga a que qualquer chave única de uma tabela particionada contenha a chave de partição. Um evento repetido (reentrega, pedido repetido) traz o mesmo `timestamp`, portanto, na prática, a idempotência continua a ser por `event_id`. Um `event_id` reutilizado com outro `timestamp` ficaria duas vezes. No MongoDB, o índice único é só `eventId`.
   - **Índices iguais aos do log MongoDB** (ADR-0007 e ADR-0010): `(facility_id, "timestamp")` e `(facility_id, spot_id, "timestamp" DESC, event_id DESC)`. As duas variantes pagam o mesmo número de índices em cada inserção.
   - **Partições criadas com antecedência** por `monolith.ensure_occupancy_partitions(from_month, months)`. A migração chama-a do mês anterior até três meses à frente, e os Workers chamam-na no arranque e uma vez por dia (`PartitionMaintenanceService`). O PostgreSQL não cria partições sozinho, e o `pg_partman` não está na imagem. Um mês que já tenha linhas na `DEFAULT` não pode receber partição e é saltado com um `WARNING`.
   - **A criação de partições tem `lock_timeout = '2s'`.** `CREATE TABLE … PARTITION OF` pede um `ACCESS EXCLUSIVE` sobre a tabela-mãe e percorre a `DEFAULT`. Se houvesse uma leitura longa à frente, todas as inserções ficariam na fila atrás dele. Com o limite, o mês é saltado com um `WARNING` e volta a ser tentado no dia seguinte. A partição `DEFAULT` deve ficar vazia: os Workers registam um aviso quando não está, e o script do ensaio regista a sua contagem (revisão da Fase 8, achados 2 e 3).
   - **Estado de cada lugar antes de um instante (UC-04).** O PostgreSQL 16 não tem *skip scan*: um `DISTINCT ON (spot_id)` leria todos os eventos do parque desde o início do histórico, ao passo que o MongoDB lê cerca de uma entrada de índice por lugar (`DISTINCT_SCAN`). A consulta `LatestPerSpotSql` emula o *skip scan* com um CTE recursivo: salta de lugar em lugar no índice `(facility_id, spot_id, …)` e faz uma descida lateral por lugar. Sem isto, a variante monolítica ficaria em desvantagem por causa da consulta, e não do modelo relacional (revisão da Fase 8, achado 1, alto).
   - **Sem coluna `payload`.** O esquema é explícito: a §7 pede que o UC-05 se faça com uma migração.

4. **Disponibilidade por consulta (UC-03).** Uma instrução SQL:
   - filtra com `ST_DWithin` sobre `geography`, ordena pela distância e aplica `LIMIT`;
   - junta os lugares do parque (filtrados pelo tipo de viatura, se pedido);
   - conta os livres com `COUNT(*) FILTER`. Um lugar sem `spot_state` conta como livre, como no Redis.

   As regras de resposta são as do Redis: a distância é calculada numa esfera, e o limite é aplicado antes de se retirarem os parques sem lugares do tipo pedido.

   As contagens têm **consistência forte**: incluem todos os eventos que já tiveram 202. No híbrido, a consistência é eventual (janela medida no ADR-0009).

5. **PostGIS** (escolha do Diogo).
   - **Imagem:** `postgis/postgis:16-3.5-alpine`, fixada por digest (PostgreSQL 16.15 e PostGIS 3.5.7), **nas duas variantes**, para que a comparação corra no mesmo motor.
   - **Extensão:** fica no seu próprio *schema* (`postgis`), e todas as chamadas são qualificadas (`postgis.ST_DWithin`). Não é preciso `search_path`, e a tabela `spatial_ref_sys` não se mistura com as tabelas da aplicação em `public`.
   - **Script de arranque da imagem:** criaria `postgis`, `topology` e `tiger` em `public`. Fica escondido por um `tmpfs` vazio em `/docker-entrypoint-initdb.d`, tanto no `docker compose` como nos testes.
   - **Índice espacial:** é um índice GiST de **expressão** sobre as duas colunas `double precision` do ADR-0003 (`postgis.ST_SetSRID(postgis.ST_MakePoint(longitude, latitude), 4326)::postgis.geography`). A tabela `parking_facilities` não muda. A consulta repete exatamente essa expressão (`MonolithicSchema.FacilityGeography`), porque o planeador só usa um índice de expressão quando a expressão coincide.

6. **UC-05 com migração** (`AddEnvironmentalColumns`): `ALTER TABLE monolith.occupancy_events ADD COLUMN temperature, air_quality, noise double precision`, colunas que aceitam nulos e sem valor por omissão.
   - Desde o PostgreSQL 11, isto só altera o catálogo e não reescreve linhas.
   - Mesmo assim, precisa de um `ACCESS EXCLUSIVE` sobre a tabela-mãe e sobre todas as partições. Por isso, espera pelas inserções em curso e, enquanto espera, bloqueia as novas, que ficam na fila atrás dele.
   - `SET LOCAL lock_timeout = '5s'` limita essa paragem: se o *lock* não for obtido a tempo, a migração falha e pode ser repetida.
   - A API não precisa de reinício: `EnvironmentalColumnsProbe` verifica a cada 5 s se as colunas existem e, a partir daí, o *writer* preenche-as com os valores numéricos do `payload`. Um valor não numérico fica `NULL`, o que significa «sensor sem essa medição» (ADR-0011).
   - Antes da migração, as leituras da geração 2 são aceites, mas perdem-se: o esquema explícito não tem onde as guardar.

7. **As mesmas métricas, com as mesmas regras de amostragem.** O `PostgresEventBus` regista dois instrumentos, os mesmos do pipeline híbrido, para que a secção 6.5 compare as mesmas métricas:
   - `smartparking.occupancy_log.lag`, para cada evento novo, como o *writer* do log;
   - `smartparking.ingestion.e2e_latency` (sensor → estado visível, D7), só para os eventos que passaram a ser o estado de um lugar registado, como o projetor Redis faz com `Applied` e `Unchanged`. Um evento atrasado ou de um lugar desconhecido não entra, porque inflacionaria o p95 (revisão da Fase 8, achado M1 da arquitetura).

   Fica uma diferença: num lote com vários eventos do mesmo lugar, só o mais recente conta. No monolítico, as métricas são emitidas pela API e não pelos Workers.

## Alternativas consideradas

- **Fórmula de distância em SQL simples.** Não exige extensão, mas também não tem índice espacial: com um pré-filtro por caixa sobre `(latitude, longitude)` funciona, mas não é o que um sistema relacional usaria. Seria uma baseline mais fraca do que o necessário.
- **Fila em PostgreSQL** (tabela-fila com `FOR UPDATE SKIP LOCKED`, ou `LISTEN/NOTIFY`) e consumidores assíncronos. Manteria a forma do pipeline híbrido, mas seria construir um *broker* dentro da base de dados. O desenho clássico de um monólito é escrever na própria transação do pedido. Fica como variante possível para a Fase 9, se o orientador a pedir.
- **Disponibilidade calculada só a partir do log** (`DISTINCT ON (spot_id)` sobre a tabela de eventos a cada pedido). É fiel à letra de «por consulta», mas degrada com o volume (D8: cerca de 1,4 milhões de eventos por dia) e seria propositadamente fraca.
- **`payload jsonb` e migração que promove os campos.** Seria mais justo para o PostgreSQL moderno, que também tem esquema flexível. Mas enfraqueceria o contraste do RNF-04 que a §7 pede, e misturaria duas questões: o motor e o modelo de dados. **Ameaça à validade a declarar no Cap. 6.5:** um PostgreSQL com `jsonb` dispensaria a migração do UC-05, tal como o MongoDB. A diferença medida aqui é entre esquema explícito e esquema extensível, não só entre motores.
- **Partições com `pg_partman`.** Não está na imagem oficial. Uma função de 30 linhas chamada pelos Workers chega para o protótipo.
- **Projetos separados (`Infrastructure.Monolithic`).** Seria uma separação mais visível, mas as classes usam o mesmo `NpgsqlDataSource`, as mesmas migrações e o mesmo `DbContext`. Uma pasta dentro de `Infrastructure.Relational` mantém um só histórico de migrações.

## Consequências

- **Positivas:**
  - os testes funcionais (contrato HTTP, RF-06, UC-02, UC-03, UC-04) correm nos dois modos (`Persistence__Mode=Hybrid|Monolithic dotnet test`). No monolítico, MongoDB e Redis nem arrancam, e os seus *connection strings* não são passados. Se algum serviço híbrido ainda estivesse registado, a API falharia no arranque em vez de os usar em silêncio;
  - o teste `The_container_has_no_hybrid_service_and_readiness_checks_only_postgres` verifica que nenhum serviço registado vem dos *assemblies* Cache, Documents, Messaging, StackExchange.Redis ou MongoDB;
  - o plano da consulta usa o índice GiST, e uma consulta por intervalo lê só a partição do mês (testes de plano);
  - o UC-05 tem os dois termos de comparação: nenhuma migração no híbrido, e no monolítico uma migração cujo custo sob carga foi medido no ensaio piloto (diário da Fase 8).
- **Negativas:**
  - as tabelas `monolith.*` e o índice GiST existem, vazios, também no modo híbrido (um só histórico de migrações). O índice GiST custa um pouco em cada escrita de parques no RF-06, nas duas variantes;
  - `CREATE EXTENSION postgis` exige um superutilizador (o dos contentores de desenvolvimento e de teste). O `Down()` remove o PostGIS mesmo que ele já existisse antes do `Up()`, o que não acontece nos nossos contentores;
  - o conjunto inicial de partições depende da data em que a migração é aplicada (mês anterior até três meses à frente). Os Workers completam o resto, e cada ensaio regista a data em que correu;
  - a idempotência é por `(event_id, "timestamp")` (ponto 3), mais fraca do que o índice único `eventId` do MongoDB. Ter a mesma garantia exigiria uma tabela não particionada `event_ids` e mais um índice por inserção. Fica declarado na comparação do RNF;
  - ainda pode haver *deadlocks* entre lotes sobrepostos, porque o SQL não garante a ordem de inserção de um `INSERT … SELECT … ORDER BY`. O `PostgresEventBus` repete até 3 vezes, e cada repetição é registada em *log*;
  - **o log monolítico guarda menos do que o MongoDB.** Sem `payload`, perde tudo o que não tem coluna: por exemplo, o `sensorGeneration` que o simulador envia sempre (ADR-0011, ponto 4), e as leituras da geração 2 antes da migração. Isto favorece o monolítico na comparação do espaço ocupado e do custo por inserção (RNF-05), e não deixa contar eventos por geração no log. Tem de ser declarado no Cap. 6.5. `TODO(Diogo):` decidir se a tabela deve ter uma coluna `sensor_generation` desde o início (revisão da Fase 8, achado M2 da arquitetura);
  - a ordem dos eventos de um lugar usa microssegundos no PostgreSQL e milissegundos (mais o `eventId` para desempatar) no Redis e no MongoDB. Dois eventos do mesmo lugar dentro do mesmo milissegundo podem deixar estados diferentes nas duas variantes;
  - nada impede que a API e os Workers corram em modos diferentes. O CSV de cada ensaio regista a variante, e a *readiness* mostra quais motores estão a ser usados;
  - as distâncias diferem cerca de 0,03 % entre as variantes (raio da esfera: PostGIS 6 371 008,8 m, Redis 6 372 797,6 m). Os testes comparam com uma tolerância de 1 %;
  - a imagem PostGIS traz o PostgreSQL 16.15, e não o 16.16 da Fase 7. Os dois variantes usam a mesma imagem, mas os resultados das Fases 1 a 7 foram obtidos com o 16.16;
  - no monolítico, toda a ingestão usa o *pool* do PostgreSQL da API. O risco de esgotar as `max_connections` (revisão da Fase 2b, ponto 1) aparece mais cedo e tem de ser tratado antes da Fase 9.
