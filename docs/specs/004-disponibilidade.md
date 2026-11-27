# Spec 004: Vista de disponibilidade e pesquisa geoespacial

**Status:** Approved (2026-11-27); revista a 2026-11-27 no plano da F5b (latitude do ponto de pesquisa de `/nearby` limitada a ±85,05112878, decisão do Diogo; ADR 0010)
**Date:** 2026-11-27
**Fase:** F5 · **Secção da tese:** 5.5.3, 5.7 · **Requisitos:** UC-03; RF-03, RF-04 · **Depende de:** F4 (spec 003, registo de eventos no MongoDB)

## Problem

A F4 grava os eventos de ocupação no MongoDB, mas ninguém consegue saber quantos lugares estão livres. O PostgreSQL não guarda ocupação (spec 001, AC-3) e a consulta ao registo de eventos não serve uma consulta de elevada frequência. Sem uma vista de leitura, o UC-03 não existe e o RF-03 e o RF-04 ficam por cumprir.

**Utilizadores:** o condutor, que procura parques com lugares livres perto de si, e o operador, que acompanha os lugares livres dos seus parques, por zona e por tipo de lugar. Na F5 não há autenticação (F11).

**Critério de sucesso:**
- os lugares livres por parque, por zona e por tipo de lugar leem-se só do Redis, sem abrir ligação ao PostgreSQL;
- a pesquisa por raio devolve os parques ordenados por distância ou por lugares livres;
- o mesmo evento aplicado duas vezes, ou fora de ordem, não altera o estado;
- apagar a vista e reconstruí-la a partir do MongoDB e do PostgreSQL repõe exatamente o mesmo estado (o Redis não é fonte de verdade).

## Scope

### In

- Vista de leitura em Redis (secção «Domain Model»), com o prefixo `avail:`.
- `OccupancyProjector` idempotente: aplica um evento de ocupação de cada vez à vista, por um script Lua atómico (ronda 1).
- Na F5 o projetor não tem transporte (ronda 0): os testes e o `ProjectionRebuilder` invocam-no diretamente. Fica em `Infrastructure/Cache`, registado no DI do Workers, mas o Workers não o invoca até à F6 e não corre nenhum serviço novo (ronda 2).
- `tools/ProjectionRebuilder`: apaga as chaves `avail:` e reconstrói a vista a partir da configuração no PostgreSQL e do último evento de cada lugar no MongoDB (ronda 2).
- Índice no MongoDB para o último evento por lugar, criado na inicialização de `Documents/` (ronda 2). É a única escrita da F5 em `Documents/`; o custo de escrita do índice fica atribuído à F5, como prevê a spec 003 (ronda 6 da 003).
- Consultas `GetAvailability` e `SearchNearby` em `Application/Features/Availability`, com uma porta de leitura própria (`IAvailabilityReader`), implementada sobre o Redis (D10).
- *Endpoints* `GET /api/availability` e `GET /api/availability/nearby` em `src/SmartParking.Api/Features/Availability` (convenção das fases anteriores).
- Canal pub/sub de alterações (ronda 4).
- Métricas próprias (ronda 5).
- Ficheiro `tests/http/availability.http` com exemplos executáveis.
- Atualização da regra `.claude/rules/persistencia.md` (estruturas Redis alargadas, ronda 1) e ADR com as decisões da F5.

### Out

- Transporte do projetor (Redis Streams, *outbox* ou *change streams*) e janela de consistência (RNF-01 do UC-01): F6 (ronda 0; spec 003, D2).
- Propagação das alterações de configuração (lugar ou parque criado, desativado, com nova zona ou novo tipo; parque com nova localização) para a vista: F6. Até lá, corre-se o `ProjectionRebuilder` depois de mudar a configuração (ronda 2).
- Correção da zona de um sensor que não acompanha o lugar (ponto em aberto da F3): a F5 usa a zona do lugar, não a do sensor; fica para a fase de configuração e no diário (ronda 5).
- Modo monolítico da disponibilidade: F9. Na F5 os *endpoints* devolvem 501 (ronda 3).
- Troca atómica da vista durante o rebuild (ronda 2).
- Alterações à ingestão (`Features/Ingestion`).
- Subscritores do canal pub/sub.
- Alvo de latência do UC-03 (< 200 ms no p95, RNF-01): mede-se na F12.
- Autenticação e RBAC: F11.

## Domain Model

### Estado de um lugar

- Dois estados na vista: `Free` e `Occupied`. Um lugar ativo sem eventos no MongoDB conta como `Free` (ronda 1).
- Só entram na vista os parques `Active` e, dentro deles, os lugares `Active` (configuração atual do PostgreSQL). Os outros não existem na vista nem contam nos totais.
- A zona e o tipo de um lugar são os da configuração lida no rebuild, guardados no hash do lugar. O `zoneId` gravado no evento serve a analítica histórica (F7) e não é usado pela vista (ronda 5).

### Ordem e idempotência (ronda 1)

- Por lugar guarda-se o par (`timestamp`, `eventId`) do último evento aplicado.
- Um evento só se aplica se o seu par for estritamente maior do que o guardado: primeiro pelo `timestamp` do sensor e, em empate, pelo `eventId` (comparação ordinal do texto do UUID em minúsculas).
- Resultados do projetor:
  - `applied`: par maior; atualiza o par guardado e, se o estado mudar, os contadores e o pub/sub;
  - `duplicate`: o `eventId` é o último aplicado; nada muda;
  - `stale`: par menor ou igual com outro `eventId`; nada muda;
  - `unknown_spot`: o lugar não está na vista; nada muda, regista em `Debug` (ronda 4).
- Os contadores só mudam numa transição `Free` ↔ `Occupied`. Um `Occupied` mais recente sobre um lugar já `Occupied` atualiza só o par guardado.
- Leitura, comparação e escrita correm num único script Lua, por isso dois eventos do mesmo lugar em paralelo não se perdem.

### Estruturas Redis (ronda 1, ronda 3)

Todas as chaves usam o prefixo `avail:` (ronda 5).

| Chave | Tipo | Conteúdo |
|---|---|---|
| `avail:spot:{spotId}` | hash | `facilityId`, `zoneId`, `type`, `state`, `lastTimestamp`, `lastEventId` |
| `avail:facility:{facilityId}` | hash | `name`, `operatorId`, `lat`, `lon`, `free`, `total`, `free:{SpotType}`, `total:{SpotType}` |
| `avail:facility:{facilityId}:zones` | set | ids das zonas do parque com lugares ativos |
| `avail:zone:{zoneId}` | hash | `facilityId`, `name`, `free`, `total`, `free:{SpotType}`, `total:{SpotType}` |
| `avail:operator:{operatorId}:facilities` | set | ids dos parques ativos do operador |
| `avail:geo:facilities` | GEO | localização dos parques ativos |

- Os totais (`total`, `total:{SpotType}`) contam os lugares ativos e só mudam no rebuild (ronda 3).
- Tudo se reconstrói a partir do PostgreSQL (configuração) e do MongoDB (eventos). Nada da vista é fonte de verdade.
- Sem retenção nem TTL: cada evento substitui o estado anterior (regra `persistencia.md`).

### Canal pub/sub (ronda 4)

- Canal `availability:changed`. Publica uma mensagem JSON por transição de estado: `spotId`, `facilityId`, `zoneId`, `spotType`, `state`, `facilityFree`, `zoneFree`, `timestamp`, `eventId`.
- Não publica em `duplicate`, `stale`, `unknown_spot` nem num `applied` sem transição. O rebuild não publica.

### Rebuild (ronda 2, ronda 5)

1. Apaga as chaves `avail:*` por `SCAN` + `UNLINK`; não toca noutras chaves.
2. Lê do PostgreSQL os parques, as zonas e os lugares ativos (porta de leitura própria, só no rebuild) e escreve os hashes, os conjuntos, os totais e o GEO, com todos os lugares `Free`.
3. Lê do MongoDB o último evento de cada lugar (ordenado por `timestamp` e `eventId` decrescentes) e aplica-o pelo mesmo `OccupancyProjector`, sem pub/sub.
- Corre *offline*, com o projetor parado; durante o rebuild a consulta pode ver um estado parcial (ronda 2). A duração mede-se (métricas) e é usada na F12 (RNF-03).
- «Mesmo estado» quer dizer: as mesmas chaves `avail:*` com os mesmos valores, incluindo o par do último evento por lugar.

### Índice no MongoDB (ronda 2)

- Coleção `occupancy_events`: índice `{ spotId: 1, timestamp: -1, _id: -1 }`, criado de forma idempotente na inicialização de `Documents/`.

## API Contract

| Verbo | Rota | Pedido | Resposta | Erros |
|---|---|---|---|---|
| GET | `/api/availability` | `facilityId?`, `operatorId?` (pelo menos um), `zoneId?`, `spotType?` | 200 `{ facilities[] }` | 400 validação; 404 parque não está na vista; 501 modo monolítico; 503 Redis indisponível |
| GET | `/api/availability/nearby` | `lat`, `lon`, `radiusMeters`, `sort?` (`distance`/`free`), `spotType?`, `limit?` | 200 `{ facilities[] }` | 400 validação; 501 modo monolítico; 503 Redis indisponível |

`GET /api/availability` (ronda 3):
- Cada parque: `facilityId`, `name`, `operatorId`, `free`, `total`, `zones[]`; cada zona: `zoneId`, `name`, `free`, `total`.
- Com `spotType`, todos os valores `free`/`total` (parque e zonas) são só desse tipo.
- Com `zoneId`, a lista `zones` tem só essa zona; os valores do parque continuam a ser do parque inteiro.
- Com `facilityId` e `operatorId`, o parque tem de ser desse operador; caso contrário, lista vazia.
- 404 só com `facilityId` de um parque que não está na vista; um `operatorId` sem parques dá lista vazia.

`GET /api/availability/nearby` (ronda 3):
- Limites fixos: `lat` ∈ [−85,05112878; 85,05112878] (o limite do GEO do Redis e do `GeoCoordinate` do domínio; revisão de 2026-11-27, plano da F5b); `lon` ∈ [−180, 180]; `radiusMeters` obrigatório, de 1 a 50 000; `sort` por omissão `distance`; `limit` de 1 a 100, por omissão 20; `spotType` opcional.
- Cada parque: `facilityId`, `name`, `operatorId`, `distanceMeters`, `free`, `total` (do tipo pedido, se houver `spotType`).
- `sort=distance`: distância crescente. `sort=free`: lugares livres decrescentes, com desempate pela distância.
- Entram os parques ativos dentro do raio, incluindo os que têm 0 lugares livres.

Comum:
- 400: `ValidationProblemDetails` (FluentValidation no filtro de *endpoint*).
- 503: ProblemDetails com `Retry-After` (ronda 4), como a ingestão.
- 501: ProblemDetails a indicar que a disponibilidade no modo monolítico chega na F9 (ronda 3).

## Authorization

- Os dois *endpoints* têm `AllowAnonymous` explícito, como nas F2 a F4. A identidade e o RBAC pertencem ao RNF-06 (F11).

## Edge Cases & Failure Modes

- **Evento repetido:** `duplicate`; nada muda (ronda 1).
- **Evento atrasado ou fora de ordem:** `stale` se o seu par for menor ou igual ao guardado; nada muda (ronda 1).
- **Dois eventos com o mesmo `timestamp`:** ganha o maior `eventId` (ronda 1).
- **Dois eventos do mesmo lugar em paralelo:** o script Lua serializa-os.
- **Lugar que não está na vista** (criado depois do rebuild): `unknown_spot`; o próximo rebuild (ou a F6) acerta (ronda 4).
- **Configuração alterada depois do rebuild:** a vista fica desatualizada até ao próximo rebuild (Deferred, ronda 2).
- **Lugar que mudou de zona:** os contadores usam a zona do hash do lugar, isto é, a da configuração lida no rebuild (ronda 5).
- **Redis indisponível:** as consultas devolvem 503 com `Retry-After`; o projetor propaga a exceção ao chamador (o transporte da F6 decide a repetição). Sem *fallback* para o PostgreSQL (ronda 4).
- **Rebuild a meio:** a vista pode estar vazia ou parcial; volta a correr-se o rebuild, que é idempotente (ronda 2).
- **Modo monolítico:** 501 sem ler nada (ronda 3).
- **Logs:** `unknown_spot` em `Debug`, com `spotId` e `eventId`; uma linha `Information` por rebuild, com as contagens e a duração. Não há dados pessoais.

## Non-Functional Requirements

- As consultas leem só do Redis. Nenhum caminho de consulta abre uma ligação ao PostgreSQL nem ao MongoDB (CQRS; prova no AC-19).
- Um script Lua por evento no projetor; um número fixo de idas ao Redis por consulta (sem uma ida por lugar).
- Alvos de desempenho (UC-03 < 200 ms no p95; RNF-03 tempo de rebuild) só se verificam na F12. Nenhum valor de desempenho é escrito à mão.
- Métricas (ronda 5), `Meter` `SmartParking.Availability`:
  - contador do projetor por resultado (`applied`, `duplicate`, `stale`, `unknown_spot`);
  - histograma da duração do rebuild.
  A latência das consultas vem da instrumentação ASP.NET Core do OpenTelemetry (F1). A janela de consistência é da F6.

## Integrations

- Redis: vista de leitura, GEO e canal pub/sub.
- PostgreSQL: só leitura da configuração, só no rebuild.
- MongoDB: só leitura do último evento por lugar, no rebuild; criação do índice na inicialização.
- Sem `IEventBus` e sem *outbox* na F5 (D2 decide-se na F6).

## Acceptance Criteria

Cada AC tem um teste com o seu número no nome. Os testes usam o PostgreSQL, o MongoDB e o Redis dos Testcontainers.

### Projetor

- **AC-1 (Occupied).** Dado um parque com uma zona e dois lugares `Standard` ativos, ambos `Free` depois do rebuild, quando o projetor aplica um `Occupied` ao primeiro lugar, então o resultado é `applied`, o hash do lugar fica `Occupied` com o par do evento, e `free` e `free:Standard` descem 1 no parque e na zona; os totais não mudam.
- **AC-2 (Freed).** Dado o estado do AC-1, quando o projetor aplica um `Freed` mais recente ao mesmo lugar, então os contadores voltam aos valores iniciais e o hash fica `Free`.
- **AC-3 (evento repetido).** Dado um evento já aplicado, quando o projetor o aplica outra vez, então o resultado é `duplicate` e as chaves `avail:*` ficam iguais às de antes.
- **AC-4 (evento atrasado e empate).** Dado um lugar com um evento aplicado em T, quando o projetor aplica um evento em T − 1 s e outro em T com `eventId` menor, então ambos são `stale` e nada muda; um evento em T com `eventId` maior é `applied`.
- **AC-5 (mesmo estado).** Dado um lugar `Occupied`, quando o projetor aplica um `Occupied` mais recente, então o resultado é `applied`, o par guardado muda e os contadores não.
- **AC-6 (lugar desconhecido).** Dado um evento de um lugar que não está na vista, quando o projetor o aplica, então o resultado é `unknown_spot` e nenhuma chave `avail:*` muda.
- **AC-7 (pub/sub).** Dado um subscritor de `availability:changed`, quando o projetor aplica uma transição, um repetido, um atrasado e um `Occupied` sobre `Occupied`, então chega exatamente uma mensagem, a da transição, com os campos da secção «Canal pub/sub».
- **AC-8 (paralelo).** Dados 50 lugares `Free`, quando se aplicam em paralelo um `Occupied` a cada um, então `free` do parque desce exatamente 50.

### Rebuild

- **AC-9 (estado inicial).** Dado um parque ativo com lugares ativos de dois tipos em duas zonas, um lugar `Inactive` e um parque `Inactive`, sem eventos, quando o `ProjectionRebuilder` corre, então todos os lugares ativos estão `Free`, `free` = `total` por parque, zona e tipo, o lugar inativo e o parque inativo não estão na vista, e o GEO e os conjuntos de zonas e de operador têm só os ativos.
- **AC-10 (reconstrução igual).** Dada uma sequência de eventos gravada no MongoDB e aplicada pelo projetor (com repetidos e fora de ordem), quando se apagam as chaves `avail:*` e o `ProjectionRebuilder` corre, então as chaves `avail:*` e os seus valores são iguais aos de antes.
- **AC-11 (só o prefixo).** Dada uma chave fora do prefixo `avail:`, quando o `ProjectionRebuilder` corre, então essa chave continua lá.
- **AC-12 (índice).** Dada a inicialização de `Documents/`, quando corre duas vezes, então a coleção `occupancy_events` tem o índice `{ spotId: 1, timestamp: -1, _id: -1 }` uma vez.

### Consultas

- **AC-13 (por parque).** Dado o estado do AC-9 com dois lugares ocupados, quando se pede `GET /api/availability?facilityId=…`, então a resposta é 200 com `free`/`total` do parque e de cada zona.
- **AC-14 (filtros).** Dado o mesmo estado, quando se pede com `spotType`, com `zoneId` e com `operatorId`, então os valores seguem as regras da secção «API Contract». Teste em tabela.
- **AC-15 (validação e 404).** Dado um pedido sem `facilityId` nem `operatorId`, com `spotType` inválido, ou com um `facilityId` que não está na vista, então a resposta é 400 com `ValidationProblemDetails` nos dois primeiros e 404 com ProblemDetails no terceiro.
- **AC-16 (raio por distância).** Dados quatro parques ativos a distâncias conhecidas de um ponto e um parque inativo dentro do raio, quando se pede `GET /api/availability/nearby` com um raio que apanha três ativos, então a resposta tem esses três, ordenados por distância crescente, com `distanceMeters`, e não tem o inativo nem o que está fora do raio.
- **AC-17 (raio por livres e limite).** Dados os mesmos parques com números de lugares livres diferentes e dois com o mesmo número, quando se pede `sort=free` e `limit=2`, então vêm os dois com mais lugares livres, com o empate resolvido pela distância.
- **AC-18 (validação do raio).** Dados `lat` (incluindo acima de 85,05112878 ou abaixo de −85,05112878), `lon`, `radiusMeters`, `sort` e `limit` fora dos limites, ou `radiusMeters` ausente, quando se pede, então a resposta é 400 com `ValidationProblemDetails`. Teste em tabela.
- **AC-19 (sem PostgreSQL).** Dada uma Api de teste com a ligação ao PostgreSQL apontada para uma porta fechada e a vista preenchida, quando se pedem os dois *endpoints*, então ambos respondem 200 com os dados do Redis.
- **AC-20 (Redis indisponível).** Dado o contentor do Redis parado, quando se pedem os dois *endpoints*, então a resposta é 503 com ProblemDetails e `Retry-After`; e o projetor lança a exceção ao chamador.
- **AC-21 (modo monolítico).** Dada a Api em `Persistence:Mode=Monolithic`, quando se pedem os dois *endpoints*, então a resposta é 501 com ProblemDetails.

### Transversais

- **AC-22 (métricas).** Dados um evento `applied`, um `duplicate`, um `stale` e um `unknown_spot`, quando o projetor os aplica, então o `Meter` `SmartParking.Availability` regista uma unidade em cada resultado; e um rebuild regista um valor no histograma da duração.
- **AC-23 (ficheiro `.http`).** Dado `tests/http/availability.http`, quando o teste de ficheiros `.http` o executa contra a Api de teste, então cada pedido devolve o estado indicado no ficheiro.

## Deferred Decisions

- D1–D10: ver a spec 000. A F5 usa o valor por omissão de D3 (registo de eventos no MongoDB como fonte do rebuild). Não toca em D2: o transporte do projetor decide-se na F6.
- Propagação das alterações de configuração para a vista: F6. *Fallback* até lá: correr o `ProjectionRebuilder` depois de mudar a configuração (ronda 2).
- Troca atómica da vista durante o rebuild: não se faz. Revê-se se as medições do RNF-03 na F12 mostrarem que o estado parcial é um problema (ronda 2).
- Zona do sensor que não acompanha o lugar: fora da F5 (ronda 5).

- Login do PostgreSQL do `ProjectionRebuilder`: reusa `smartparking_workers` (ronda 6). Um login só de leitura fica para a F11 (RNF-06) e é registado no diário.

## Implementação em duas partes

Ronda 6, critério do índice das especificações (plano acima de cerca de 15 passos):
- **F5a — vista e reconstrução:** estruturas Redis, `OccupancyProjector` (script Lua), métricas do projetor e do rebuild, porta de leitura da configuração, índice no MongoDB, `tools/ProjectionRebuilder`, atualização de `persistencia.md` e ADR (AC-1 a AC-12 e AC-22).
- **F5b — consultas:** `IAvailabilityReader` sobre o Redis, `GetAvailability`, `SearchNearby`, *endpoints*, validação, `availability.http` e testes (AC-13 a AC-21 e AC-23).

Cada parte tem o seu plano, a sua sessão e a sua entrada no diário, como as F3 e F4.

## Open Questions

Nenhuma.
