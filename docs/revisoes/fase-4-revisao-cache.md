# Revisão do *read model* em Redis — Fase 4 (UC-03, UC-01 passo 4)

- **Data:** 15-11-2026
- **Âmbito:** o código da Fase 4, ainda por *commitar* em `main` (ver `git status`), com foco em:
  - `src/SmartParking.Infrastructure.Cache/Availability/ReadModelKeys.cs`, `ReadModelScripts.cs` (Lua), `RedisAvailabilityReadModel.cs` e `Infrastructure.Cache/DependencyInjection.cs`;
  - `src/SmartParking.Application/Availability/*.cs` (projetores, `ReadModelRebuilder`, `ReadModelGate`, métricas, validação da consulta);
  - `src/SmartParking.Workers/Program.cs` e `ReadModel/ReadModelInitializer.cs`;
  - `src/SmartParking.Infrastructure.Documents/OccupancyEvents/MongoOccupancyEventLog.cs` (`GetLatestPerSpotAsync`);
  - `tests/SmartParking.Api.IntegrationTests/Availability/*.cs`, só para ver o que já está coberto.
- **Revisor:** um agente separado (Claude, método «caching-strategy-setup»), sem acesso ao raciocínio da sessão que escreveu o código. Revisão só de leitura: nenhum ficheiro em `src/` ou `tests/` foi editado e nada foi *commitado*. O único ficheiro escrito é este.
- **Adaptação acordada do método ao projeto:** o Redis **não** é aqui uma *cache* do tipo *cache-aside*. É o *read model* do CQRS: a única fonte das consultas do UC-03 (§4), alimentado por consumidores de streams (ADR-0006) e reconstruível a partir do PostgreSQL e do MongoDB (ADR-0008). Uma chave em falta não é um *miss* que se recalcula; é um dado perdido. A lista de verificação do método foi aplicada com esta leitura:

  | Item do método | Aplica-se? | Como foi lido aqui |
  |---|---|---|
  | Escolher a camada (`IMemoryCache`, `IDistributedCache`, `HybridCache`, *output caching*) | **Não** | Não há fábrica nem fonte a montante para a consulta: o §4 manda ler só do Redis. Um L1 em memória na API (`HybridCache`) serviria contadores antigos por nó e reintroduziria a invalidação que o CQRS evita. Só faria sentido como experiência medida da Fase 9. |
  | Proteção contra *stampede* | **Não**, na forma do método | Não há recálculo por *miss*. O equivalente é a **reconstrução**, que também não deve correr em paralelo consigo mesma nem com os projetores (achado 1). |
  | Chaves estáveis, com espaço de nomes, com todas as entradas | **Sim** | Verificado: prefixo `sp:rm:`, GUID na forma «D», um tipo de chave por entidade. |
  | Expiração («não guardar para sempre») | **Não** | Um TTL numa chave do *read model* tem o mesmo efeito que um despejo: corrompe os contadores. A única expiração legítima seria a de um estado «estacionado» que ainda não conta (proposta do achado 4). |
  | Invalidação | **Sim**, transformada | Substituída pela projeção de eventos (idempotente e ordenada) e pela reconstrução. Foi este o foco da revisão. |
  | Dados pessoais numa *cache* partilhada | **Sim** | Verificado: os eventos de configuração e o *read model* não têm operador nem dados pessoais (RNF-06); o canal pub/sub só tem ids, tipo, estado e contagens. |
  | Medir a taxa de acerto | **Não** | Por desenho, toda a consulta «acerta». As métricas equivalentes são `unknown_spot`, `stale` e o *lag* (D7). Falta uma verificação periódica do invariante dos contadores (proposta no achado 1). |
  | Política de despejo e memória | **Sim** (acrescentado) | `maxmemory-policy`, pegada de memória e persistência (achado 6). |

- **Método e provas:**
  - **Leitura** de todo o código do âmbito, do ADR-0006, do ADR-0008 e da ESPECIFICACAO (§2, §3, §4, §6).
  - **Contentor Redis descartável** (`redis:7.4.12-alpine`, o mesmo *digest* do `docker-compose.yml`, sem portas publicadas, `--appendonly yes`; apagado no fim com `docker rm -f`). Os cinco *scripts* foram extraídos **sem alterações** de `ReadModelScripts.cs` por um programa descartável na pasta temporária da sessão (fora do repositório) e executados com `EVAL`/`EVALSHA`. Observou-se:
    - **configuração por omissão:** `maxmemory = 0`, `maxmemory-policy = noeviction`, `appendfsync = everysec`;
    - **pegada do *read model*:** 50 parques × 100 lugares = 5 000 lugares (D8; a divisão em 50 parques e 4 zonas é uma **suposição** deste teste, porque a especificação não fixa o número de parques), carregados pelos *scripts* `UpsertFacility`, `UpsertSpot` e um `ApplyOccupancy` por lugar. `used_memory` passou de 1 036 696 para 3 139 520 bytes (≈ 2,1 MB) para 5 106 chaves. Por chave (`MEMORY USAGE`): lugar 288 B (`listpack`), parque com contadores de 3 tipos 416 B, conjunto de 100 lugares 4 200 B, GEO com 50 parques 2 624 B, zona 216 B;
    - **invariante dos contadores** depois da carga: um *script* de verificação descartável (soma dos lugares `FREE` por parque contra `free` do parque) não encontrou diferenças nos 50 parques;
    - **pegada do stream:** 20 000 `XADD` com o formato real (`key`, `body` JSON de `OccupancyEventMessage` com `payload` vazio, `traceparent`) acrescentaram 9 202 280 bytes, ≈ 460 B por entrada (`MEMORY USAGE` do stream: 9 886 304 B);
    - **reprodução do achado 1** (deriva permanente de um contador) e do **achado 3** (*timestamp* futuro congela o lugar), descritas nos achados;
    - **`noeviction` com `maxmemory` atingido:** `CONFIG SET maxmemory 1mb` e um `ApplyOccupancy`: o Redis recusou o *script* com `OOM … on @user_script:12`, ou seja, no primeiro comando de escrita (`HSET … ts`), e o lugar ficou inalterado. Um `XADD` foi recusado com o mesmo erro.
  - Os testes de integração **não** foram executados. Nenhum valor acima é um resultado para a tese: são de uma máquina local, sem carga, com um `payload` vazio.

## Resumo

O núcleo está bem feito: cada alteração é um *script* Lua atómico, idempotente e ordenado por `(ts, eventId)`, e o teste com 4 consumidores concorrentes e eventos baralhados confirma-o. A afirmação «os contadores nunca divergem» é verdadeira **entre projetores**, mas **não** entre a reconstrução e os projetores: foi reproduzida uma deriva permanente. Há ainda dois caminhos em que um lugar fica com o estado errado por tempo indeterminado.

| # | Gravidade | Achado | Quando |
|---|---|---|---|
| 1 | Alta | Uma reconstrução concorrente com projetores ativos (comando com os Workers a correr, ou 2+ Workers a arrancar sem `sp:rm:meta`) deixa contadores desviados para sempre (reproduzido) | Antes da Fase 9 (RNF-02 com 2 e 4 instâncias; *benchmarks* usam o comando) |
| 2 | Média | A reconstrução ignora eventos já confirmados pelo grupo `availability` mas ainda não gravados no MongoDB; e o primeiro arranque do grupo relê o stream inteiro | Antes da Fase 9 (cenário `failover` + reconstrução entre execuções) |
| 3 | Média | Um `timestamp` de sensor no futuro congela o lugar para sempre, e a reconstrução repete-o (reproduzido) | Já (validação RF-01) |
| 4 | Baixa | Eventos de um lugar ainda sem configuração são confirmados e perdidos (`unknown_spot`); só uma reconstrução manual corrige | Fase 5 (com o *relay* da outbox) |
| 5 | Baixa | Durante uma reconstrução, o UC-03 responde `200` com resultados vazios ou parciais | Fase 9 (medir) ou já (503) |
| 6 | Baixa | `noeviction` é implícito; a memória é dominada pelo stream (≈ 460 B/evento medidos, ≈ 2,1 MB para todo o *read model*), e um `OOM` para a ingestão e a projeção ao mesmo tempo | Antes da Fase 9 |
| 7 | Baixa | `ClearAsync` não faz nada, sem erro, se nenhum primário estiver ligado; o ADR diz `UNLINK`, o código chama `KeyDeleteAsync` (comando enviado não verificado) | Pequeno, já |
| 8 | Informativa | Ordem da configuração por relógios de máquinas diferentes (`OccurredAt` da API, margem de 30 s dos Workers) | Fase 5 / Fase 9 com várias instâncias |
| 9 | Informativa | Redis Cluster: chaves não declaradas nos *scripts* e `DEL` multi-chave (confirma o ADR-0008) | Só se a tese propuser *cluster* |
| 10 | Informativa | Pub/sub: rajada de mensagens na reconstrução; mensagens com contagens absolutas (bom) | Documentar |
| 11 | Informativa | Sem *tombstone* (já no ADR-0008); hashes de zona nunca removidos | Sem correção |

## Achados

### 1. Reconstrução concorrente com projetores ativos desvia os contadores para sempre (alta, reproduzido)

- **Local:** `RedisAvailabilityReadModel.cs:119-144` (`ClearAsync`: `SCAN` + apagamento por páginas, sem atomicidade), `ReadModelRebuilder.cs:45-82`, `ReadModelInitializer.cs:33-45`, `Workers/Program.cs:74-86` (comando `rebuild-read-model`), `ReadModelScripts.cs:42` (`HINCRBY` cria o hash do parque se ele não existir).
- **Problema:** a limpeza apaga as chaves em páginas, ao longo de várias idas ao servidor. Entre duas páginas, um projetor com o `ReadModelGate` já aberto pode aplicar um evento. O `HINCRBY` sobre um hash de parque já apagado **recria-o** só com `free = ±1` e `free:{Tipo} = ±1`. Se o cursor do `SCAN` já passou por essa chave, ela sobrevive à limpeza. Depois, `UpsertFacility` encontra `changedAt` vazio, aceita, e ainda move esse ±1 para a zona (`ReadModelScripts.cs:133`). Os `UpsertSpot` somam por cima. O desvio é permanente: os eventos seguintes movem ±1 a partir de uma base errada.
- **Reprodução (contentor descartável, *scripts* reais):** parque com 100 lugares (`free = 57`). Apagou-se o hash do parque e o da zona (simula uma página do `ClearAsync`), aplicou-se `ApplyOccupancy` FREE→OCCUPIED a um lugar ainda não apagado (resultado `1`; o hash do parque reapareceu com `free -1 free:Car -1`), apagou-se o resto, e recarregou-se o parque e 3 lugares livres. Resultado: `spots 3, free 2` no parque e na zona; o verificador de invariante deu «expected 3/3 got 3/2».
- **Quando acontece** (o ADR-0008 diz «com os Workers parados», mas nada o impõe):
  - o comando `rebuild-read-model` corre com os Workers ativos (provável nos *scripts* de *benchmark* da Fase 9, que querem um estado conhecido entre execuções);
  - **duas ou mais instâncias dos Workers** arrancam com `sp:rm:meta` ausente (RNF-02 mede 1, 2 e 4 consumidores; depois de um `FLUSHALL` ou de um Redis novo). Cada `ReadModelInitializer` vê `meta` ausente e reconstrói. A instância A termina e abre o seu *gate*; a instância B ainda está a limpar. Além da deriva, B apaga estados que os projetores de A já aplicaram e confirmaram (`XACK`), e os eventos que ainda não chegaram ao MongoDB perdem-se para o *read model* (ver achado 2).
- **Proposta:**
  1. **Barreira de reconstrução fora do prefixo** (para que o `SCAN MATCH sp:rm:*` não a apague), por exemplo `sp:rmctl:rebuild`:
     - a reconstrução adquire-a com `SET sp:rmctl:rebuild <id> NX PX <ttl>` (renovada durante a carga) e liberta-a depois do `MarkLoadedAsync`. Quem não a consegue não reconstrói: espera até haver `loadedAt`;
     - os cinco *scripts* começam com `if redis.call('EXISTS', KEYS[n]) == 1 then return 6 end` (chave declarada em `KEYS`), e o projetor trata o código 6 como falha transitória (lança, não há `XACK`, a mensagem volta). A própria reconstrução passa o seu `<id>` e é a única autorizada a escrever.
     
     Isto fecha os dois cenários, incluindo o comando lançado por engano com os Workers ativos.
  2. **Verificador do invariante** como ferramenta da Fase 9 (um *script* só de leitura, como o usado nesta revisão, corrido no fim de cada execução de *benchmark*): soma dos lugares `FREE` por parque, tipo e zona contra os contadores. Dá à tese uma prova de que os contadores não derivaram em cada cenário, em vez de só o argumento.
  3. Um teste de integração: duas reconstruções em paralelo com um projetor ativo, seguidas do verificador.

### 2. A reconstrução ignora o atraso do grupo `occupancy-log` (média, plausível)

- **Local:** `ReadModelRebuilder.cs:58-80` (lê só o MongoDB), `Workers/Program.cs:51-58` (os dois grupos sobre o mesmo stream avançam de forma independente).
- **Problema:** o grupo `availability` e o grupo `occupancy-log` consomem o mesmo stream ao seu ritmo. Se o `occupancy-log` estiver atrasado (MongoDB lento ou em baixo, precisamente o cenário `failover`), há eventos que o `availability` já aplicou e confirmou mas que ainda não estão no MongoDB. Uma reconstrução nesse momento apaga o Redis, lê o MongoDB (sem esses eventos) e repõe um estado **anterior**. O grupo `availability` não volta a entregar esses eventos, porque já os confirmou.
- **Cenário de falha:** durante o `failover`, o MongoDB para 2 min; o `availability` continua a atualizar o Redis. Entre execuções, o *script* de *benchmark* corre `rebuild-read-model` (com os Workers parados, como manda o ADR). Os lugares que mudaram nesses 2 min voltam ao estado anterior e ficam assim até ao próximo evento de cada um. Com 1 evento por lugar a cada 5 min em média (D8), a maioria corrige-se depressa, mas um lugar com um carro estacionado horas fica errado horas.
- **Agravante (informativo):** o grupo `availability` é criado com `XGROUP CREATE … 0` (ADR-0006). No primeiro arranque depois da Fase 4, relê **todo** o stream, que não é aparado até à Fase 10. Cada entrada é um `EVALSHA` que dá `stale` ou `duplicate`. O custo não foi medido; com D8 (≈ 1,4 milhões de eventos por dia) cresce com os dias de histórico.
- **Proposta (uma correção resolve as duas coisas):** no fim da reconstrução, posicionar o grupo `availability` no ponto em que o `occupancy-log` estava **antes** de a reconstrução ler o MongoDB: o menor id pendente desse grupo (`XPENDING … - + 1`) ou, sem pendentes, o seu `last-delivered-id` (`XINFO GROUPS`). Aplicar com `XGROUP SETID smartparking:occupancy-events availability <id>`. Reaplicar eventos é inofensivo (idempotentes e ordenados). Em alternativa mais simples: recusar a reconstrução enquanto o `occupancy-log` tiver `lag` ou `pending` maiores do que 0 (`XINFO GROUPS`, Redis 7) e registá-lo no *log*.

### 3. Um `timestamp` no futuro congela o lugar para sempre (média, reproduzido)

- **Local:** `OccupancyEventValidator.cs:93-111` (só exige ISO 8601 com *offset*, sem limite superior); `ReadModelScripts.cs:31` (`ts < curTs` → `stale`); `MongoOccupancyEventLog.GetLatestPerSpotAsync` (`$top` por `timestamp` decrescente).
- **Problema:** a ordem dos eventos de um lugar vem do relógio do sensor. Um único evento com o relógio adiantado (sensor sem NTP depois de um *reset*, erro do simulador, fuso mal convertido) passa a ser o «mais recente» do lugar. Todos os eventos reais seguintes dão `stale`. A reconstrução não corrige: o MongoDB devolve esse mesmo evento como o último.
- **Reprodução:** `ApplyOccupancy` com `ts = 253402300799000` (31-12-9999) → `1`; um evento real seguinte (`ts = 1763400000000`, FREE) → `4` (`stale`); o lugar ficou `OCCUPIED`.
- **Porque importa:** o lugar fica errado sem limite de tempo e sem sinal, a não ser a métrica `stale`. O mesmo evento distorce a métrica D7 (`lag` negativo).
- **Proposta:**
  - **já, na validação do RF-01:** recusar (400 por evento, como os outros erros de esquema) um `timestamp` posterior a `receivedAt + tolerância`. `TODO(Diogo):` fixar a tolerância (o valor depende dos sensores reais; não foi inventado aqui);
  - opcionalmente, também um limite inferior (o lugar novo começa com `ts = 0`; eventos anteriores a 1970 dariam `stale` sempre);
  - expor `stale` por lugar num *log* de aviso quando a diferença for grande, para diagnóstico na Fase 9.

### 4. Eventos de lugares ainda sem configuração são confirmados e perdidos (baixa, confirmado por leitura)

- **Local:** `ReadModelScripts.cs:28` (`return 5`), `AvailabilityProjector.cs:56-66` (o resultado `UnknownSpot` é contado e a mensagem confirmada).
- **Problema:** a configuração chega por outro stream (outbox → *relay* da Fase 5), com o seu próprio atraso. Um lugar criado e logo a seguir ocupado pode ter o primeiro evento de ocupação a chegar antes do `SpotChanged`. O evento é confirmado sem efeito; quando o lugar chega, começa `FREE` (ADR-0008, ponto 3), mesmo estando ocupado. O ADR diz «a reconstrução corrige», mas a reconstrução só é automática quando `sp:rm:meta` falta.
- **Proposta:** «estacionar» o evento no próprio hash do lugar. Em `ApplyOccupancy`, se o lugar não tem `facilityId`, gravar só `ts`, `eventId` e `state`, sem contadores, e devolver 5 na mesma. Em `UpsertSpot`, na criação, usar o `state`, `ts` e `eventId` já existentes em vez de `FREE`/`0`, e contar `free` conforme esse estado. Um hash «estacionado» pode ter TTL (é o único caso em que um TTL é seguro, porque ainda não entra em nenhum contador), para não acumular lixo de eventos tardios de lugares apagados. O ADR-0008 tem de dizer que um TTL só pode existir nesses hashes.

### 5. O UC-03 responde `200` com dados vazios ou parciais durante uma reconstrução (baixa, confirmado por leitura)

- **Local:** `RedisAvailabilityReadModel.cs:162-221`; `AvailabilityEndpoints.cs:15-22`. O `ReadModelGate` só retém os projetores dos Workers; a API não sabe que há uma reconstrução.
- **Problema:** entre o `ClearAsync` e o fim do carregamento, o `GEOSEARCH` devolve menos parques ou nenhum, e os contadores estão a meio. O cliente recebe `200 []` ou contagens baixas, indistinguíveis de «não há lugares». No arranque normal (`meta` presente) isto não acontece; acontece na carga inicial e em cada `rebuild-read-model`.
- **Proposta:**
  - **mínima:** na consulta, ler `sp:rm:meta` em *pipeline* com o `GEOSEARCH` (sem ida extra ao servidor) e responder `503` se `loadedAt` faltar; ou incluir esse teste no `/health/ready` da API;
  - **completa (só se a Fase 9 o justificar):** reconstruir para um prefixo com geração (`sp:rm:g{n}:`) e trocar um ponteiro no fim. Duplica a memória durante a carga (≈ 2 MB, achado 6) e complica os *scripts*;
  - em qualquer caso, a Fase 9 deve medir a duração (`smartparking.read_model.rebuild.duration`) e registar que é uma janela de indisponibilidade do UC-03.

### 6. Política de memória implícita e pegada dominada pelo stream (baixa, confirmado)

- **Local:** `docker-compose.yml:47-48` (`redis-server --appendonly yes`, sem `maxmemory` nem `maxmemory-policy`).
- **Verificado:** no contentor com a mesma imagem, `maxmemory = 0` e `maxmemory-policy = noeviction`. O ADR-0008 já o diz corretamente: hoje não há despejo. O risco é alguém acrescentar `--maxmemory 256mb --maxmemory-policy allkeys-lru` (a receita habitual de uma *cache*). Um lugar despejado dá `unknown_spot` (eventos perdidos, achado 4). Um parque despejado volta a nascer com `HINCRBY` a partir de 0 (o mecanismo do achado 1). Uma zona despejada também. Tudo sem erro. E o `sp:rm:meta` pode sobreviver, por isso não há reconstrução automática.
- **Pegada medida** (ver «Método»): todo o *read model* de D8 ≈ 2,1 MB; o stream ≈ 460 B por evento com `payload` vazio. Por extrapolação aritmética, **não medida**: 1,4 milhões de eventos por dia × ≈ 460 B ≈ 0,6 GB por dia de stream. O *read model* é desprezável ao lado do stream (ADR-0006: sem `MAXLEN` até à Fase 10). O `payload` da UC-05 (`temperature`, `airQuality`, `noise`) aumenta o tamanho por entrada; não foi medido.
- **Consequência com um limite:** com `noeviction`, atingir o limite recusa o `XADD` (a API responde `503`) **e** os *scripts* (o projetor repete). Verificou-se que o *script* é recusado no primeiro comando de escrita (`OOM … @user_script:12`), sem estado parcial. Isso preserva a consistência, mas um stream sem aparar torna-se a causa de indisponibilidade de todo o sistema.
- **Proposta:**
  - **já:** tornar a política explícita no compose (`--maxmemory-policy noeviction`, e um `--maxmemory` deliberado, a fixar pelo Diogo para a máquina dos *benchmarks*) e documentá-la no ADR-0008 como requisito de correção, não de desempenho;
  - **já ou na Fase 9:** fazer o `RedisHealthCheck` (ou o `ReadModelInitializer`) ler `maxmemory_policy` com `INFO memory` e recusar arrancar, ou marcar a prontidão como degradada, se não for `noeviction`;
  - **antes das execuções longas da Fase 9** (e não só na Fase 10): aparar o stream com `XTRIM … MINID` até ao menor ponto ainda não confirmado pelos **dois** grupos. Com `MAXLEN`, pode apagar entradas que um grupo lento ainda não leu (ADR-0006).

### 7. `ClearAsync` pode não apagar nada, sem erro (baixa, confirmado por leitura)

- **Local:** `RedisAvailabilityReadModel.cs:125` e `:134`, `:141`.
- **Problema:**
  - o ciclo só percorre servidores com `IsConnected && !IsReplica`. Se a ligação cair entre o `GetAsync` e o `GetServers()`, a lista fica vazia, o método retorna sem apagar nada e a reconstrução escreve por cima do *read model* antigo. Os parques apagados no PostgreSQL continuam no GEO, porque nada os remove (as cargas de configuração com `changedAt` antigo dão `stale`, sem efeito);
  - o ADR-0008 (ponto 6) e a `RASTREABILIDADE` dizem `UNLINK`; o código chama `KeyDeleteAsync`. **Não verifiquei** que comando o StackExchange.Redis envia (`DEL` ou `UNLINK`). Com 5 000 chaves pequenas, a diferença é desprezável, mas o texto da tese deve corresponder ao que corre.
- **Proposta:** lançar uma exceção se não houver nenhum primário ligado (o `ReadModelInitializer` já repete com *back-off*); confirmar o comando com `INFO commandstats` depois de uma reconstrução e acertar o ADR, ou chamar `UNLINK` explicitamente com `ExecuteAsync("UNLINK", …)`.

### 8. Ordem da configuração depende de relógios de máquinas diferentes (informativa)

- **Local:** `ReadModelScripts.cs:74`, `:129` (comparação por `changedAt`); `ReadModelRebuilder.cs:37`, `:43` (`SnapshotMargin` de 30 s, com o relógio dos Workers); o `OccurredAt` dos eventos vem do relógio da API (`ParkingSpot.Update(…, occurredAt)`).
- **Problema:** com uma única API, a concorrência otimista garante que duas alterações do mesmo agregado têm `OccurredAt` pela ordem do *commit*. Com várias instâncias da API, um desvio de relógio entre elas pode inverter a ordem: a alteração gravada por último fica com o `OccurredAt` menor, é tomada como `stale`, e o *read model* fica diferente do PostgreSQL até à próxima reconstrução. A margem de 30 s da reconstrução assume também que o relógio dos Workers não está mais de 30 s à frente do da API.
- **Proposta:** registar o pressuposto no ADR-0008 («uma instância da API, ou relógios sincronizados com desvio ≪ 30 s»). Se a Fase 9 usar várias APIs, ordenar por um número monótono por agregado (por exemplo, uma versão incrementada pela aplicação e gravada no evento), em vez do relógio.

### 9. Redis Cluster (informativa, confirma o ADR-0008)

- `ApplyOccupancy`, `UpsertSpot` e `DeleteSpot` declaram só a chave do lugar e escrevem no parque e na zona; `DeleteFacility` apaga lugares que não estão em `KEYS`. Num nó único funciona (verificado no contentor). Em *cluster*, além do que o ADR já diz (*hash tags* por parque, zonas fora do *script*), o `KeyDeleteAsync` com uma página de 1 000 chaves (`RedisAvailabilityReadModel.cs:134`) falharia com `CROSSSLOT`. Nada a corrigir enquanto a tese não propuser *cluster*.

### 10. Pub/sub (informativa)

- **Bom:** o `PUBLISH` está dentro do *script*, por isso cada mensagem sai depois do estado e dos contadores, e pela mesma ordem. As mensagens levam contagens **absolutas** (`free`, `freeOfType`): um subscritor que perca uma mensagem (pub/sub é «dispara e esquece», como o ADR diz) fica certo na seguinte do mesmo parque. Um `unchanged`, `duplicate` ou `stale` não publica (coberto pelo teste `Same_event_applied_twice_changes_nothing_and_publishes_once`).
- **A notar:** a reconstrução aplica o último evento de cada lugar com o mesmo *script*, por isso publica uma mensagem por cada lugar cujo estado difere de `FREE`, até 5 000 de seguida. Um cliente vê-as como mudanças em tempo real que não o são. Proposta: passar ao *script* um argumento «não publicar» quando chamado pela reconstrução, ou documentar.

### 11. *Tombstone* e hashes de zona (informativa)

- A falta de *tombstone* (um `SpotChanged` ou `FacilityChanged` reentregue depois da remoção recria a entidade) está documentada no ADR-0008. Com um parque apagado, um `SpotChanged` tardio cria um hash de parque só com contadores, que não está no GEO e por isso não aparece nas consultas: lixo invisível, sem efeito nas respostas.
- Os hashes `sp:rm:zone:{zona}` nunca são removidos, mesmo com todos os contadores a 0 (216 B cada, medido). Sem correção.

## Verificado sem problemas

- **Espaço de nomes:** todas as chaves do *read model* começam por `sp:rm:` e os streams por `smartparking:`, por isso o `SCAN MATCH sp:rm:*` nunca apanha o *bus*. A recusa do `FLUSHDB` (ADR-0008) é a decisão certa. Ids na forma «D» minúscula, iguais em todos os pontos (`ReadModelKeys.Id`).
- **Estruturas de dados:** hashes pequenos ficam em `listpack` (verificado: lugar e parque); o conjunto de lugares por parque fica em `listpack` com 100 membros; o GEO tem um membro por parque, não por lugar, o que mantém o `GEOSEARCH` proporcional ao número de parques. Os contadores no próprio hash do parque permitem responder com um `HMGET` por parque.
- **Atomicidade e ausência de deriva entre projetores:** cada *script* lê, decide e escreve num só passo. O invariante `free` = lugares `FREE` verificou-se nos 50 parques depois da carga com os *scripts* reais. O teste `Concurrent_out_of_order_application_keeps_counters_equal_to_spot_states` (4 consumidores, eventos baralhados) cobre o caso concorrente.
- **Ordem `(ts, eventId)`:** `ts` em ms (≈ 1,8 × 10¹² na data atual) e `changedAt` em µs (≈ 1,8 × 10¹⁵) são exatos num *double* de Lua (< 2⁵³). Os valores são guardados como o texto original do `ARGV`, não como número convertido. O desempate por `eventId` é coerente com o MongoDB: o `$top` ordena por `eventId` em `BinData` subtipo 4 (`GuidRepresentation.Standard`), cujos bytes seguem a mesma ordem do texto hexadecimal «D». Os GUID têm hífenes em posições fixas e só `[0-9a-f]`, por isso a comparação de cadeias do Lua dá a mesma ordem que `StringComparer.Ordinal` do teste.
- **Idempotência:** mesmo `(ts, eventId)` → `duplicate`, sem escrita; `unchanged` só avança `ts`/`eventId`, o que é necessário para a ordem dos seguintes.
- **Configuração:** `UpsertSpot` com `<=` e `DeleteSpot`/`DeleteFacility` com `<` («a remoção ganha os empates»); mudança de tipo move `spots:` e, se livre, `free:`; mudança de zona move todos os campos de contagem do parque com um só `HGETALL`; lugares que chegam antes do parque entram na zona quando o parque chega (`cur[1] == nil`). O invariante «zona = soma dos parques com essa zona» mantém-se em todos os caminhos lidos.
- **Falhas a meio de um lote:** `ApplyAsync` envia os *scripts* em *pipeline* e espera por todos; uma falha lança e o lote não é confirmado; os já aplicados são idempotentes na reentrega. Com `OOM`, o *script* é recusado no primeiro comando de escrita, sem estado parcial (verificado).
- **Persistência:** o *read model* e os `XACK` estão no mesmo AOF, pela ordem de execução, e o `XACK` só é enviado depois da resposta do *script*. Uma perda abrupta de até ≈ 1 s (`everysec`) perde um sufixo comum: se o efeito do *script* se perde, o `XACK` também, e o evento volta a ser entregue. Raciocínio sobre a ordem do AOF; **não reproduzido**. A janela do `XADD` é a do achado 1 da Fase 3.
- **Reconstrução:** a falha a meio deixa `sp:rm:meta` ausente, por isso o próximo arranque reconstrói de novo; o *gate* usa `RunContinuationsAsynchronously`; a margem de 30 s está justificada no ADR; os `XAUTOCLAIM` de entradas paradas atrás do *gate* são inofensivos (idempotência). Ler parques e lugares do PostgreSQL em duas consultas sem um *snapshot* comum é inofensivo aqui: um lugar novo entre as duas leituras cria um hash de parque só com contadores, que o `FacilityChanged` posterior completa.
- **Consulta:** `GEOSEARCH … FROMLONLAT` com a longitude primeiro (a `RASTREABILIDADE` regista a mutação lat/lon no `GEOADD`, que faz falhar 4 testes), `ASC COUNT n WITHDIST`, seguido de um `HMGET` por parque, todos em *pipeline* (uma ida ao servidor para ≤ 200 parques); um parque apagado entre as duas leituras é saltado; a latitude e a longitude devolvidas vêm do hash (valor exato, `"R"`) e não do *geohash* de 52 bits. O `COUNT` antes do filtro `type` está documentado no ADR-0008; se a Fase 9 mostrar respostas curtas com `type`, a alternativa é pedir `COUNT` maior (por exemplo `2 × limit`) e cortar depois do filtro.
- **Validação da consulta:** limites do Redis GEO, raio máximo provisório (`TODO(Diogo)` já presente) e `limit` ≤ 200.
- **Métricas D7:** o *lag* só é registado para `applied` e `unchanged`, o que evita a distorção apontada no achado 9 da Fase 3.
