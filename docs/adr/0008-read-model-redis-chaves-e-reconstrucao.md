# ADR-0008 — *Read model* de disponibilidade em Redis: modelo de chaves, atualização atómica e reconstrução

- **Estado:** aceite
- **Data:** 15-11-2026
- **Fase:** 4 (*read model*, UC-03)
- **Relaciona-se com:** ESPECIFICACAO.md §3 (estado do lugar «só no *read model*», mapeamento), §4 (UC-01 passo 4, UC-03, RF-06), §5 (CQRS, idempotência), §6 (RNF-01, RNF-02), §9 (Fase 4); ADR-0003 (GeoJSON e limites do Redis GEO), ADR-0004 (outbox), ADR-0006 (D2, Redis Streams), ADR-0007 (D3, log no MongoDB)

> Nota de numeração: o pedido da Fase 4 referia «ADR 0006», mas esse número já estava atribuído à D2 (Redis Streams). Este registo é o 0008.

## Contexto

O UC-03 (`GET /api/availability?lat=&lon=&radius=&type=`) tem de responder em menos de 200 ms no p95 (RNF-01) e ler **apenas** do Redis (§4). O estado atual de cada lugar não existe no relacional (`ParkingSpot` não o tem, por decisão da Fase 2a): existe só no *read model*. Este é alimentado por três fontes:

1. os eventos de ocupação do UC-01, num segundo *consumer group* sobre o mesmo stream da Fase 3 (ADR-0006);
2. as alterações de configuração do RF-06 (novo parque, novo lugar), que passam pela outbox (ADR-0004);
3. uma carga completa a partir do PostgreSQL e do log do MongoDB, no arranque e para os *benchmarks*.

As restrições vêm do *bus*:
- a entrega é «pelo menos uma vez», por isso o mesmo evento pode chegar duas vezes;
- com vários consumidores (RNF-02), não há ordem garantida entre eventos do mesmo lugar;
- os sensores podem enviar eventos atrasados.

Os contadores por parque, por zona e por tipo de viatura não se podem desviar do estado real dos lugares.

## Decisão

### 1. Modelo de chaves

Todas as chaves começam por `sp:rm:` («*read model*»). Assim, uma reconstrução apaga exatamente o *read model* (`SCAN MATCH sp:rm:*`) e nada mais no mesmo Redis: os streams do *bus* (`smartparking:*`) ficam intactos.

| Chave | Tipo | Conteúdo |
|---|---|---|
| `sp:rm:spot:{spotId}` | hash | `facilityId`, `vehicleType`, `state` (`FREE`/`OCCUPIED`), `ts` (ms do último evento aplicado), `eventId`, `changedAt` (µs da última alteração de configuração) |
| `sp:rm:facility:{facilityId}` | hash | `id`, `name`, `lat`, `lon`, `capacity`, `type`, `zone`, `changedAt`; contadores `spots`, `free`, `spots:{Tipo}`, `free:{Tipo}` |
| `sp:rm:facility:{facilityId}:spots` | set | ids dos lugares do parque (para remoções e diagnóstico) |
| `sp:rm:zone:{zona}` | hash | `spots`, `free`, `spots:{Tipo}`, `free:{Tipo}` |
| `sp:rm:geo:facilities` | GEO | membro = `facilityId`. O `GEOADD` recebe a **longitude primeiro** |
| `sp:rm:meta` | hash | `loadedAt` (marca uma carga completa) |
| `sp:rebuild-lock` | string | *token* da reconstrução que detém o *read model*, com expiração de 2 min renovada durante a reconstrução. Fica **fora** do prefixo para que a limpeza não o apague |
| canal `sp:availability` | pub/sub | uma mensagem JSON por mudança de estado: `facilityId`, `spotId`, `vehicleType`, `state`, `free`, `freeOfType`, `timestamp` |

Quando um lugar ou um parque é removido, a chave fica como **marca de remoção** (*tombstone*): só com `deleted` e `changedAt`, e uma expiração de 24 h. Explicação no ponto 2.

Os contadores ficam no próprio hash do parque. A consulta lê assim um único hash por parque (`HMGET`), sem agregações no momento da leitura.

### 2. Atualização atómica: um *script* Lua por alteração

Cada evento de ocupação é aplicado por um *script* Lua (`ReadModelScripts.ApplyOccupancy`). Numa única execução, o *script*:
1. lê o hash do lugar;
2. decide se o evento é mais recente;
3. atualiza o lugar e os contadores do parque, da zona e do tipo;
4. faz `PUBLISH` no canal.

O Redis executa o *script* inteiro sem intercalar outros comandos. Por isso, o estado do lugar e os contadores nunca divergem, mesmo com vários projetores a escrever ao mesmo tempo.

**Porque não `MULTI`/`EXEC`.** A decisão (é mais recente? o estado mudou?) depende de valores lidos no mesmo passo. Com `MULTI`, isso obriga a usar `WATCH` e a repetir a transação do lado do cliente sempre que há conflito, precisamente no cenário com vários consumidores. O Lua dá a mesma atomicidade sem repetições.

**Ordem e idempotência.** Os eventos de um lugar ordenam-se por `(timestamp, eventId)`:

| Situação | Resultado |
|---|---|
| `ts` menor do que o guardado | `stale`: ignorado (evento atrasado) |
| mesmo `ts` e mesmo `eventId` | `duplicate`: nada muda (reentrega) |
| mesmo `ts` e `eventId` menor | `stale` (desempate determinista: qualquer ordem de entrega dá o mesmo estado final) |
| mais recente, mesmo estado | `unchanged`: só o `ts` avança, sem mexer nos contadores nem publicar |
| mais recente, outro estado | `applied`: contadores ±1 e `PUBLISH` |
| lugar inexistente no *read model* | `unknown_spot`: confirmado e contado numa métrica; a reconstrução corrige |

O `timestamp` é truncado ao milissegundo, a mesma precisão do log no MongoDB (ADR-0007). Assim, a reconstrução e o caminho incremental comparam os mesmos valores.

**Configuração (RF-06).** Os *scripts* `UpsertFacility`, `UpsertSpot`, `DeleteFacility` e `DeleteSpot` seguem o mesmo princípio:
- ordenam pelo `OccurredAt` do evento de domínio, em microssegundos, o que é exato num número Lua (um *double*) até ao ano 2255;
- cada evento leva o estado completo e não uma diferença, por isso basta aplicar o mais recente;
- numa remoção, a remoção ganha os empates;
- uma mudança de zona move os contadores do parque da zona antiga para a nova;
- se os lugares chegam antes do parque, os contadores entram na zona quando o parque chega;
- **marcas de remoção** (revisão da Fase 4, achado 5): uma remoção deixa a chave apenas com `deleted` e `changedAt`, com uma expiração de 24 h. Assim, um `SpotChanged` ou `FacilityChanged` anterior à remoção, tratado mais tarde por outro consumidor, é reconhecido como antigo e não recria o lugar ou o parque. Um lugar novo num parque removido também é ignorado. As marcas não guardam contadores, por isso a expiração não os pode desviar;
- **validação antes do *script*** (achado 7): o Redis não desfaz um *script* que falha a meio, e as escritas anteriores ao erro ficam gravadas. Por isso, o `ConfigurationProjector` aplica as regras do domínio (limites do GEO, capacidade, nomes) antes de chamar o *script*, e um evento inválido vai para a *dead letter*. Além disso, o `GEOADD`, o único comando que pode recusar um valor, passa a ser a primeira escrita do `UpsertFacility`.

### 3. Estado inicial de um lugar: **livre**

Um lugar novo começa `FREE`, com `ts = 0`, até ao primeiro evento (decisão do Diogo no plano da Fase 4). A alternativa era um estado `UNKNOWN` que não contasse como livre. Seria mais conservadora, mas exigiria um terceiro contador e mudaria o significado de «lugares livres» na resposta. `TODO(Diogo):` confirmar no Cap. 3 que «sem leitura do sensor» conta como livre.

### 4. RF-06 → *read model* pela outbox

Os agregados `ParkingFacility` e `ParkingSpot` passam a levantar eventos de domínio:
- `FacilityChanged` e `SpotChanged` em `Create`/`Update`;
- `FacilityDeleted` e `SpotDeleted` em `MarkDeleted`, chamado antes de `Remove`.

O `OutboxSaveChangesInterceptor` (ADR-0004) grava-os na mesma transação. Os eventos só têm identificadores, a localização, a zona, a capacidade e o tipo: não têm o operador nem dados pessoais.

O consumidor `ConfigurationProjector` lê o tópico `smartparking:configuration-events`, no grupo `availability`. O formato de cada mensagem (`ConfigurationEventMessage`) é a linha da outbox tal como está: `id`, `type`, `aggregateId`, `occurredAt` e `payload`.

**Limite assumido:** o *relay* da outbox só existe na Fase 5. Até lá, os eventos ficam na outbox com `processed_at` vazio, e os testes publicam-nos diretamente no tópico. Um parque criado pela API só aparece no *read model* depois de uma reconstrução (ver o diário). Rejeitaram-se duas alternativas:
- antecipar o *relay*, o que misturaria as Fases 4 e 5;
- escrever no Redis a partir da API depois do commit, uma escrita dupla sem atomicidade, contra o ADR-0004.

### 5. Consulta (UC-03)

A consulta faz `GEOSEARCH sp:rm:geo:facilities FROMLONLAT <lon> <lat> BYRADIUS <r> m ASC COUNT <limit> WITHDIST`, seguido de um `HMGET` por parque, todos enviados de uma vez (*pipelining*). A validação fica no `AvailabilitySearch`:
- `lat` e `lon` são obrigatórios e respeitam os limites do Redis GEO (ADR-0003);
- `radius` vale 1 000 m por omissão e tem um máximo provisório de 50 km (`TODO(Diogo)`);
- `limit` vale 50 por omissão e tem um máximo de 200.

Com `type`, os contadores devolvidos são os desse tipo e os parques sem lugares desse tipo ficam de fora. O `COUNT` aplica-se **antes** desse filtro, por isso uma consulta com `type` pode devolver menos do que `limit` parques.

### 6. Reconstrução do zero (`ReadModelRebuilder`)

A reconstrução tem cinco passos:
1. trinco (ver abaixo), posição do log e `SCAN MATCH sp:rm:*` + `UNLINK` em páginas de 1 000 (nunca `KEYS`, que bloqueia o servidor);
2. parques do PostgreSQL;
3. lugares do PostgreSQL;
4. último evento de cada lugar no MongoDB, com `$group` + `$top` (MongoDB 5.2+), aplicado pelo **mesmo** *script* Lua do caminho incremental;
5. `sp:rm:meta.loadedAt`.

A agregação é um *collection scan* sem índice novo. Um índice em `spotId` pesaria em cada uma das cerca de 1,4 milhões de inserções diárias (D8), para uma operação que só corre em reconstruções. O custo é medido na Fase 9 (métrica `smartparking.read_model.rebuild.duration`).

A configuração lida é marcada com o instante de início **menos 30 s** (`SnapshotMargin`). Uma alteração cujo evento é anterior ao início, mas cuja transação só fica gravada depois da leitura, seria tomada como antiga e perdida. Voltar a aplicar um evento de estado completo que já está no *snapshot* é inofensivo.

**Trinco e barreira** (revisão da Fase 4, achado 1, reproduzido pelo revisor). A primeira versão confiava na regra «correr com os Workers parados». Mas nada a impunha, e o problema aparecia também com dois Workers a arrancar sem `sp:rm:meta`, o que o RNF-02 faz com 2 e 4 instâncias. Durante o `SCAN`, o `HINCRBY` de um projetor recriava o hash de um parque com `free = -1`, e esse hash sobrevivia à limpeza: os contadores ficavam errados para sempre. Agora funciona assim:
- a reconstrução começa por `SET sp:rebuild-lock <token> NX PX 120000`. Se o trinco já existir, falha com `RebuildInProgressException`;
- todos os *scripts* recebem a chave do trinco e um *token*. Enquanto o trinco existir, só o detentor do *token* escreve; os projetores recebem o código 6, transformado em `ReadModelRebuildingException`. Essa exceção é tratada como uma falha transitória: nada é confirmado (`XACK`) e o *bus* volta a entregar a mensagem depois do *back-off*;
- a reconstrução renova o trinco no máximo de 30 em 30 s e liberta-o no fim, com uma comparação do *token*. Se o processo morrer, os projetores retomam ao fim de 2 min, no máximo.

**Alinhamento com o log** (achado 2). Antes de ler o MongoDB, a reconstrução regista a posição até à qual o grupo `occupancy-log` confirmou tudo: a entrada anterior à sua pendente mais antiga, ou o seu `last-delivered-id` se não houver pendentes. No fim, move o grupo `availability` para essa posição (`XGROUP SETID`, ou `CREATE … MKSTREAM` se o grupo ou o stream ainda não existirem). Assim, um evento que o projetor já tinha aplicado e confirmado, mas que ainda não estava no log, volta a ser entregue. Como a aplicação é idempotente e ordenada, isto é seguro. Isto também resolve um efeito do primeiro arranque: o grupo `availability` deixa de reler o stream inteiro.

Há duas formas de reconstruir:
- **No arranque dos Workers** (`ReadModelInitializer`): se não houver `sp:rm:meta`, reconstrói. Com vários Workers, um reconstrói e os outros esperam pelo trinco e voltam a verificar. Os projetores esperam num `ReadModelGate` até à carga terminar, e as mensagens ficam por confirmar no stream, sem perda.
- **Por comando**, para os *benchmarks*: `dotnet run --project src/SmartParking.Workers -- rebuild-read-model`. Pode correr com os Workers ligados. Se outra reconstrução estiver em curso, termina com o código 1.

## Alternativas consideradas

- **Contadores calculados na leitura** (por exemplo, `HGETALL` de todos os lugares de um parque, ou um `SET` de lugares livres por parque com `SCARD`). Dispensam a manutenção atómica dos contadores, mas a leitura passa a crescer com o número de lugares. O UC-03 é o caminho mais frequente e o que tem o alvo mais apertado (RNF-01). O `SET` de livres por parque continua a ser uma variante a medir na Fase 9, se os contadores mostrarem problemas.
- **`MULTI`/`EXEC` com `WATCH`**: ver o ponto 2.
- **RedisJSON ou RediSearch**: são módulos fora do Redis base e exigiriam outra imagem. GEO e hashes chegam para o UC-03.
- **Carga inicial com `FLUSHDB`**: apagaria também os streams do *bus* (ADR-0006), que estão no mesmo Redis.
- **Estado `UNKNOWN` para lugares sem eventos**: ver o ponto 3.

## Consequências

- **Positivas:**
  - consistência dos contadores garantida pelo Redis, sem coordenação entre consumidores;
  - projetor idempotente e insensível à ordem de entrega;
  - reconstrução determinista, que reutiliza o mesmo código;
  - a consulta lê uma chave GEO mais um hash por parque.
- **Redis Cluster:** os *scripts* obtêm as chaves do parque e da zona a partir do hash do lugar, e por isso tocam em chaves que não estão em `KEYS`. Funciona num único nó, a configuração do protótipo (D2), mas não em Redis Cluster. Lá, as chaves de um *script* têm de estar declaradas e no mesmo *slot*. Uma variante em *cluster* precisaria de *hash tags* por parque (`{facilityId}`) e de contadores por zona calculados fora do *script*.
- **Pub/sub é «dispara e esquece».** Um subscritor desligado perde as mensagens; o estado autoritativo está nos hashes. Um cliente que precise de garantias lê o *read model*, não o canal.
- **Persistência e memória:** o Redis do compose usa AOF (`appendonly yes`) e `--maxmemory-policy noeviction`, agora explícito (achado 6 da revisão da cache). Uma política com despejo (`allkeys-lru`) corromperia os contadores em silêncio e perderia eventos dos streams. Com `noeviction` e um limite de memória, as escritas falham de forma visível: o revisor verificou que o *script* é recusado logo na primeira escrita, sem estado parcial. O *read model* de D8 ocupa cerca de 2 MB (medido pelo revisor); o que cresce é o stream (Fase 10).
- **Indisponibilidade da consulta durante uma reconstrução:** o UC-03 responde 200 com resultados vazios ou parciais enquanto as chaves são recarregadas (achado 5 da revisão da cache, não corrigido). A janela, cerca de 1,5 s com os dados de desenvolvimento, é medida na Fase 9.
- **Timestamps no futuro** (achado 3 da revisão da cache): como o *read model* guarda só o evento mais recente de cada lugar, um `timestamp` muito adiantado congelaria o lugar, e a reconstrução voltaria a escolhê-lo. O RF-01 passa a recusar `timestamp` > receção + `Ingestion:MaxClockSkew` (5 min por omissão; `TODO(Diogo)`).
- **Histogramas:** a latência D7 só se pode calcular com *buckets* abaixo de 1 s. O `ServiceDefaults` define *buckets* explícitos (5 ms a 60 s) para `smartparking.read_model.lag`, `smartparking.read_model.bus_lag` e `smartparking.occupancy_log.lag`, e outros para a duração da reconstrução (achado 1 da revisão de código).
- **Ordem entre eventos de configuração do mesmo lugar:** resolvida pelas marcas de remoção (ponto 2) enquanto estas não expiram (24 h). Um evento atrasado mais de 24 h em relação à remoção voltaria a recriar o lugar; a reconstrução corrige-o.
- **Relógios:** a ordem da configuração usa o `OccurredAt` da API e a margem de 30 s usa o relógio dos Workers. Com várias instâncias da API com relógios muito desacertados, esta ordem pode falhar (achado informativo das duas revisões).
- **D7:** o histograma `smartparking.read_model.lag` (renomeado `smartparking.ingestion.e2e_latency` na Fase 5, ADR-0009) mede do `timestamp` do sensor até à atualização no Redis, ou seja, a latência ponta a ponta do UC-01. O `smartparking.read_model.bus_lag` mede desde a receção na API. O *polling* de 100 ms do consumidor (ADR-0006) entra nas duas medições.
