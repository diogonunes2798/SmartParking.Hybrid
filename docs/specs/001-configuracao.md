# Spec 001: Domínio, subcamada relacional e configuração

**Status:** Approved (2026-11-24)
**Date:** 2026-11-24
**Fase:** F2 · **Secção da tese:** 5.1, 5.4, 5.9.2 · **Requisitos:** RF-06; UC-02 e UC-05 (configuração); RNF-06 (auditoria só por acréscimo) · **Depende de:** F1 (spec 000)

## Problem

As fases seguintes (sessões e pagamentos na F3, ingestão na F4, disponibilidade na F5, analítica na F7, modo monolítico na F9) precisam de um modelo relacional comum. Também precisam de uma infraestrutura configurada: operador, parques, zonas, lugares, sensores e tarifários. Hoje o Domain está vazio e não existe nenhuma tabela de negócio. O operador do parque não tem forma de registar a infraestrutura (RF-06). Sem esse registo, a ingestão não consegue validar `sensorId`/`spotId` e o UC-02 não tem um tarifário com que calcular o valor.

A tese defende que cada alteração de negócio produz, na mesma transação, um registo de auditoria imutável e um evento na *outbox* (5.4.1, 5.6.2). A F2 é a primeira fase em que isso se pode demonstrar e testar.

**Utilizador:** o operador do parque, por API. Na F2 não há autenticação (F11).

**Critério de sucesso:**
- a migração `Initial` cria o esquema completo da Figura 9;
- a configuração faz-se toda pela API;
- cada alteração deixa exatamente uma linha de auditoria e uma de *outbox* na mesma transação;
- a base de dados recusa alterar ou apagar a auditoria ao papel da aplicação.

## Scope

### In

- Modelo do domínio dos três agregados da Tabela 15, só com estrutura e invariantes:
  - utilizador: `User`, `Vehicle`, `PaymentMethod`;
  - infraestrutura: `Operator`, `ParkingFacility` (com horário de funcionamento), `Zone`, `ParkingSpot`, `Sensor`, `Tariff`;
  - sessão: `ParkingSession`, `Reservation`, `Payment`; o comportamento é da F3.
- Eventos de domínio de configuração (lista em «Integrations»).
- Subcamada relacional (`Infrastructure/Relational`):
  - o `DbContext` é a unidade de trabalho (implementa o `IUnitOfWork` da F1, D10);
  - uma configuração EF por entidade e todas as entidades acima na migração `Initial`;
  - PostGIS `geography(Point, 4326)` com índice GiST na localização dos parques (RF-04);
  - chaves estrangeiras, unicidade, `CHECK` e `EXCLUDE`;
  - `AuditEntry` e `OutboxMessage` sem chaves estrangeiras.
- Repositórios por raiz de agregado para a escrita e portas de leitura próprias para as consultas (D10, ADR 0003).
- Auditoria só por acréscimo garantida pela base de dados (RNF-06). Um script SQL versionado, aplicado pela `Initial`, cria o grupo `smartparking_app` e as suas permissões. O utilizador de *login* da Api é membro desse grupo.
- Configuração (RF-06): comandos e consultas de parques (com horário), zonas, lugares (individualmente e em lote), sensores e versões de tarifário. Cada alteração grava a `AuditEntry` e a `OutboxMessage` na mesma transação.
- Extensão da fundação (F1): `Error.PreconditionFailed` (412) e `Error.PreconditionRequired` (428), e a porta `ICurrentActor`.
- Api: um `IEndpointGroup` por recurso de configuração; `tests/http/configuration.http` com um pedido por *endpoint*.
- Exceção ao âmbito de pastas do pedido, autorizada pelo Diogo na ronda 4:
  - `scripts/db-app-user.sh` (novo);
  - uma variável nova em `.env.example` (`POSTGRES_APP_PASSWORD`);
  - as *connection strings* da Api.
- ADR: papel da aplicação e auditoria só por acréscimo; versões de tarifário com vigência.

### Out

- Estado atual dos lugares (livre/ocupado): é do Redis (F5) e não é guardado no PostgreSQL. O estado `Active`/`Inactive` do lugar é configuração, não ocupação.
- *Endpoints* de utilizadores, veículos, meios de pagamento, sessões, reservas e pagamentos (F3 e seguintes).
- *Endpoints* de operadores (F11, com RBAC).
- Leitor e publicação da *outbox* (F3/F4): na F2 as linhas ficam com `publishedAt = null`.
- Autenticação, autorização real e cifra em repouso (F11).
- Testes funcionais no modo `Monolithic` (F9).
- Dependências NuGet novas; alterações ao `compose.yaml`, `Documents/`, `Cache/`, `Messaging/`, `CLAUDE.md` e `.claude/`.

## Domain Model

Nomes de tabelas em `snake_case`, no plural. Identificadores `Guid` (UUID v7 gerado na aplicação). Valores monetários `numeric(10,2)`. Instantes `timestamptz`, obtidos do `TimeProvider`.

### Agregado de infraestrutura

| Entidade | Campos | Restrições |
|---|---|---|
| `Operator` | id, name | name único. Um operador de referência com `Guid` fixo entra por `HasData` na `Initial`. Sem *endpoints* na F2 |
| `ParkingFacility` | id, operatorId, name, address, location (`geography(Point,4326)`), timeZone (IANA), status (`Active`/`Inactive`), xmin | FK → operators (RESTRICT); (operatorId, name) único; índice GiST em location; latitude ∈ [−90, 90] e longitude ∈ [−180, 180]; timeZone válido |
| `FacilityOpeningHours` | facilityId, dayOfWeek (0–6), opensAt, closesAt | PK (facilityId, dayOfWeek); `CHECK opensAt < closesAt`; um intervalo por dia; sem linha = fechado. Faz parte do agregado do parque: `ON DELETE CASCADE` (a única exceção ao RESTRICT). Substitui-se por inteiro no PUT do parque |
| `Zone` | id, facilityId, name, xmin | FK → facilities (RESTRICT); (facilityId, name) único |
| `ParkingSpot` | id, facilityId, zoneId, code, type (`Standard`/`Accessible`/`ElectricVehicle`/`Motorcycle`), status (`Active`/`Inactive`), xmin | FK → facilities e FK composta (facilityId, zoneId) → zones(facilityId, id), ambas RESTRICT, para que a zona seja do mesmo parque; (facilityId, code) único; facilityId imutável; zoneId pode mudar dentro do parque |
| `Sensor` | id, facilityId, hardwareId, kind (`Occupancy`/`Environmental`), model, installedAt, status (`Active`/`Inactive`), spotId?, zoneId?, xmin | FK → facilities (RESTRICT); FK composta (facilityId, spotId) → spots (RESTRICT); FK composta (facilityId, zoneId) → zones (RESTRICT); hardwareId único global; spotId único quando não é nulo (índice parcial); `CHECK kind = 'Occupancy' OR spotId IS NULL` (só os sensores de ocupação ficam num lugar); a reatribuição só se faz dentro do parque |
| `Tariff` (versão) | id, facilityId, validFrom, validTo?, pricePerHour, billingIncrementMinutes, dailyMaximum?, freeMinutes, currency | FK → facilities (RESTRICT); `EXCLUDE USING gist (facility_id WITH =, tstzrange(valid_from, valid_to) WITH &&)` (no máximo uma versão vigente por parque); `CHECK validTo IS NULL OR validTo > validFrom`; pricePerHour ≥ 0; billingIncrementMinutes ∈ [1, 60]; dailyMaximum ≥ 0 quando existe; freeMinutes ≥ 0; currency com 3 letras maiúsculas (por omissão `EUR`) |

Regras das versões de tarifário:
- Uma versão nova tem `validFrom` ≥ instante atual e > `validFrom` da última versão do parque. Fecha a última versão (`validTo = validFrom` da nova).
- As versões com `validFrom` ≤ agora são imutáveis.
- Só se apaga uma versão futura, e só se for a última; apagá-la reabre a anterior (`validTo = null`).
- Uma versão referida por sessões nunca se apaga (FK RESTRICT).

### Agregado de utilizador (só estrutura e tabelas)

| Entidade | Campos | Restrições |
|---|---|---|
| `User` | id, name, email | email único, guardado em minúsculas. Dados pessoais: nunca em *logs* de nível `Information` ou inferior |
| `Vehicle` | id, userId, licensePlate | FK → users (RESTRICT); licensePlate normalizada (maiúsculas, sem espaços nem hífenes), única global |
| `PaymentMethod` | id, userId, type (`Card`/`MbWay`/…), providerToken, maskedNumber, expiresOn? | FK → users (RESTRICT). Nunca o PAN nem o CVV |

### Agregado de sessão (só estrutura e tabelas; comportamento na F3)

| Entidade | Campos | Restrições |
|---|---|---|
| `Reservation` | id, spotId, userId, vehicleId, startsAt, endsAt, status (`Active`/`Cancelled`/`Fulfilled`/`Expired`), xmin | FK RESTRICT; `CHECK endsAt > startsAt`; `EXCLUDE USING gist (spot_id WITH =, tstzrange(starts_at, ends_at) WITH &&) WHERE (status = 'Active')` |
| `ParkingSession` | id, spotId, vehicleId, tariffId, reservationId?, startedAt, endedAt?, status (`Open`/`Closed`/`Paid`), amount?, xmin | FK RESTRICT; `CHECK endedAt IS NULL OR endedAt >= startedAt`; `CHECK amount IS NULL OR amount >= 0` |
| `Payment` | id, sessionId, paymentMethodId, amount, status (`Pending`/`Succeeded`/`Failed`), createdAt | FK RESTRICT; `CHECK amount >= 0`; índice único parcial em sessionId `WHERE status = 'Succeeded'` |

### Auditoria e *outbox* (sem chaves estrangeiras)

| Tabela | Campos | Notas |
|---|---|---|
| `audit_entries` | id, occurredAt, actor, entityType, entityId, operation (`Created`/`Updated`/`Deleted`), before (`jsonb`?), after (`jsonb`?), traceId?, outboxMessageId | actor `NOT NULL`, vindo de `ICurrentActor` (`anonymous` na F2). Só por acréscimo |
| `outbox_messages` | id, type (nome e versão, por exemplo `FacilityCreated.v1`), payload (`jsonb`), occurredAt, traceId?, publishedAt?, attempts, lastError? | Índice parcial `WHERE published_at IS NULL`. Na F2 ninguém publica |

### Permissões do grupo `smartparking_app`

- `SELECT, INSERT, UPDATE, DELETE` nas tabelas de negócio.
- `SELECT, INSERT` em `audit_entries`: sem `UPDATE`, `DELETE` nem `TRUNCATE`.
- `SELECT, INSERT, UPDATE` em `outbox_messages`: sem `DELETE`, porque o leitor da F3/F4 marca as linhas como publicadas.
- Sem permissões de DDL. As migrações correm como dono das tabelas (`smartparking`); a Api liga com um utilizador `LOGIN` membro do grupo, criado por `scripts/db-app-user.sh` (desenvolvimento) e pelo `ApiFixture` (testes).

## API Contract

Todas as rotas estão sob `/api` e são `AllowAnonymous`. Erros em ProblemDetails (contrato da spec 000).

**Erros comuns:**
- 400: validação.
- 404: o recurso ou o parque não existe.
- 409: unicidade, integridade referencial ou regra de negócio.
- 412: `If-Match` desatualizado.
- 428: falta o `If-Match` num `PUT` ou `DELETE`.

**Leituras:** levam `ETag` (o `xmin`). As listas são paginadas com `page` (≥ 1) e `pageSize` (1–100, por omissão 20) e devolvem `{ items, page, pageSize, totalCount }`.

| Verbo | Rota | Pedido | Resposta | Erros específicos |
|---|---|---|---|---|
| POST | `/facilities` | operatorId, name, address, latitude, longitude, timeZone, openingHours[] | 201 + Location + ETag | 409 nome repetido no operador |
| GET | `/facilities` | page, pageSize | 200 lista | 400 pageSize > 100 |
| GET | `/facilities/{id}` | — | 200 + ETag | 404 |
| PUT | `/facilities/{id}` | os mesmos campos de POST exceto operatorId, mais status; If-Match | 200 + ETag | 412, 428, 409 |
| DELETE | `/facilities/{id}` | If-Match | 204 | 409 se tiver zonas, lugares, sensores ou tarifários |
| POST | `/facilities/{id}/zones` | name | 201 | 409 nome repetido |
| GET | `/facilities/{id}/zones` e `/facilities/{id}/zones/{zoneId}` | page, pageSize | 200 | 404 |
| PUT | `/facilities/{id}/zones/{zoneId}` | name; If-Match | 200 | 412, 428, 409 |
| DELETE | `/facilities/{id}/zones/{zoneId}` | If-Match | 204 | 409 se tiver lugares ou sensores |
| POST | `/facilities/{id}/spots` | zoneId, code, type | 201 | 409 código repetido; 400 zona de outro parque |
| POST | `/facilities/{id}/spots/batch` | spots[] (zoneId, code, type, sensor? {hardwareId, model, installedAt}), 1–1 000 | 201 com os ids criados | 400 acima de 1 000 ou códigos repetidos no pedido; 409 conflito com dados existentes; tudo ou nada |
| GET | `/facilities/{id}/spots` e `/facilities/{id}/spots/{spotId}` | page, pageSize, zoneId?, status? | 200 | 404 |
| PUT | `/facilities/{id}/spots/{spotId}` | zoneId, code, type, status; If-Match | 200 | 412, 428, 409; 400 zona de outro parque |
| DELETE | `/facilities/{id}/spots/{spotId}` | If-Match | 204 | 409 se tiver sensor, reservas ou sessões |
| POST | `/facilities/{id}/tariffs` | validFrom, pricePerHour, billingIncrementMinutes, dailyMaximum?, freeMinutes, currency? | 201 | 400 validFrom no passado; 409 validFrom ≤ início da última versão |
| GET | `/facilities/{id}/tariffs`, `/facilities/{id}/tariffs/current` e `/facilities/{id}/tariffs/{tariffId}` | page, pageSize | 200 | 404 (incluindo um parque sem versão vigente em `current`) |
| DELETE | `/facilities/{id}/tariffs/{tariffId}` | If-Match | 204 | 409 se estiver em vigor, for passada, não for a última ou tiver sessões |
| POST | `/sensors` | facilityId, hardwareId, kind, model, installedAt, spotId?, zoneId? | 201 | 409 hardwareId repetido ou lugar já com sensor; 400 lugar ou zona de outro parque, ou ambiental com lugar |
| GET | `/sensors` e `/sensors/{id}` | page, pageSize, facilityId?, kind? | 200 | 404 |
| PUT | `/sensors/{id}` | model, status, zoneId?; If-Match | 200 | 412, 428 |
| PUT | `/sensors/{id}/spot` | spotId; If-Match | 200 | 409 lugar já com sensor; 400 lugar de outro parque ou sensor ambiental |
| DELETE | `/sensors/{id}/spot` | If-Match | 200 | — |
| DELETE | `/sensors/{id}` | If-Match | 204 | 409 se ainda estiver num lugar (solta-se primeiro com `DELETE /sensors/{id}/spot`) |

## Authorization

- Todos os *endpoints* de configuração têm `AllowAnonymous` explícito no grupo. Fica registado como fora do âmbito (RNF-06, F11), não como esquecimento.
- Na F11: a política `ConfigurationAdmin` e a implementação autenticada de `ICurrentActor`.
- Na base de dados, a Api nunca liga como superutilizador; as permissões estão em «Domain Model».

## Edge Cases & Failure Modes

- **Atomicidade:** se a gravação da `AuditEntry` ou da `OutboxMessage` falhar, a alteração de negócio é revertida (uma só chamada a `SaveChanges`, numa só transação). Nunca 2PC.
- **Violações da base de dados** traduzidas pela Infrastructure para `Error`:
  - `23505` (unicidade) → `Conflict`;
  - `23503` (FK RESTRICT) → `Conflict`, com a dependência na mensagem;
  - `23P01` (`EXCLUDE`) → `Conflict`;
  - `23514` (`CHECK`) → `Validation`.

  A Application continua sem EF Core.
- **Concorrência:** um `xmin` desatualizado dá 412. Duas versões de tarifário criadas em simultâneo: a segunda falha no `EXCLUDE` ou na regra de `validFrom` e dá 409.
- **Lote:** o lote é atómico. Um código repetido dentro do pedido dá 400 antes de tocar na base de dados. Um conflito com dados existentes dá 409 e não cria nada.
- **Mudar a zona de um lugar:** o `SpotUpdated.v1` leva `previousZoneId` e `zoneId`.
- **Sensor:**
  - um sensor ambiental não pode ter lugar;
  - reatribuir para o lugar onde já está não faz nada (200, sem auditoria nem evento);
  - um sensor sem lugar continua registado; os eventos dele são rejeitados na F4.
- **Fuso horário:** um fuso inválido dá 400. O horário com `opensAt ≥ closesAt` ou com dias repetidos dá 400.
- **Pedidos repetidos:** um `POST` repetido não é idempotente. A unicidade dá 409 nos recursos com chave natural (código, nome, `hardwareId`).

## Non-Functional Requirements

- Sem alvo de desempenho na F2. A configuração tem pouca frequência e as medições são da F12. Nenhum valor de desempenho é escrito à mão.
- O lote de 1 000 lugares serve para preparar o cenário de referência (5 000 lugares, Tabela 9) em 5 pedidos.
- As consultas de configuração usam `AsNoTracking` e projeções. Ainda não há consultas quentes a compilar; a F4 compila a validação `sensorId`/`spotId` se precisar.
- Os dois modos (`Hybrid` e `Monolithic`) registam as mesmas implementações relacionais.

## Integrations

- **Eventos na *outbox*:** *records* `sealed` no Domain; `type` com o nome e a versão; *payload* com o estado novo:
  - `FacilityCreated.v1`, `FacilityUpdated.v1`, `FacilityDeleted.v1`: id, operatorId, name, latitude, longitude, timeZone e status; a F5 usa-os no índice GEO;
  - `ZoneCreated.v1`, `ZoneUpdated.v1`, `ZoneDeleted.v1`;
  - `SpotCreated.v1`, `SpotUpdated.v1` (com `previousZoneId`), `SpotDeleted.v1`; a ativação e a desativação são `SpotUpdated` com o status;
  - `SensorRegistered.v1`, `SensorUpdated.v1`, `SensorReassigned.v1` (com `previousSpotId`), `SensorUnassigned.v1`, `SensorRemoved.v1`;
  - `TariffVersionCreated.v1`, `TariffVersionDeleted.v1`.
- Uma operação gera exatamente um evento e uma `AuditEntry` por entidade alterada. No lote, um par por lugar e outro por sensor criado.
- PostgreSQL+PostGIS (extensões `postgis` e `btree_gist`, criadas pela `Initial`). Não toca no MongoDB nem no Redis.

## Acceptance Criteria

Cada AC tem um teste com o seu número no nome (`…_ACn`), exceto os marcados como documentais. Os testes de integração correm no modo `Hybrid`, com o PostgreSQL do Testcontainers e a Api ligada pelo utilizador da aplicação.

- **AC-1 (migração).** Dada uma base vazia, quando se aplicam as migrações como dono, então a `Initial` aplica-se sem erro e o esquema contém:
  - as tabelas de «Domain Model»;
  - as extensões `postgis` e `btree_gist`;
  - o grupo `smartparking_app`;
  - o operador de referência.
  Também documental: `dotnet ef migrations add Initial` e `dotnet ef database update` correm sem erro contra o `compose.yaml`.
- **AC-2 (PostGIS).** Dado o esquema migrado, quando se inspeciona `parking_facilities`, então `location` é `geography(Point,4326)` com um índice GiST. Um parque criado com latitude e longitude devolve as mesmas coordenadas no GET.
- **AC-3 (sem estado de ocupação).** Dado o esquema migrado, quando se inspecionam as colunas de `parking_spots`, então não existe nenhuma coluna de ocupação. Só existe `status` com os valores `Active`/`Inactive`.
- **AC-4 (criar com auditoria e *outbox*).** Dado um operador, quando se cria um parque, então:
  - a resposta é 201 com `Location` e `ETag`;
  - existe exatamente uma `AuditEntry` (`Created`, `after` preenchido, `actor = anonymous`) e uma `OutboxMessage` `FacilityCreated.v1`, ligadas por `outboxMessageId`.
- **AC-5 (apagar parque com lugares).** Dado um parque com lugares, quando se pede `DELETE /api/facilities/{id}` com o `If-Match` certo, então:
  - a resposta é 409 com ProblemDetails;
  - o parque continua a existir;
  - não se cria nenhuma `AuditEntry` nem `OutboxMessage`.
- **AC-6 (apagar parque vazio).** Dado um parque sem filhos e com horário, quando é apagado, então:
  - a resposta é 204;
  - o horário desaparece em cascata;
  - ficam uma `AuditEntry` `Deleted` (com `before`) e um `FacilityDeleted.v1`.
- **AC-7 (alteração de tarifário: uma auditoria, um evento, uma transação).** Dado um parque com uma versão de tarifário vigente, quando se cria uma versão nova, então:
  - a versão anterior fica com `validTo` igual ao `validFrom` da nova;
  - existe exatamente uma `AuditEntry` nova e uma `OutboxMessage` `TariffVersionCreated.v1` nova;
  - o `xmin` das linhas novas e da versão alterada é o mesmo, o que prova que é a mesma transação.
- **AC-8 (atomicidade).** Dada uma falha forçada na inserção em `outbox_messages` (um *trigger* criado só no teste), quando se cria uma versão de tarifário, então:
  - a resposta é 500;
  - não fica nenhuma versão nova nem nenhuma `AuditEntry` nova;
  - a versão anterior continua aberta.
- **AC-9 (regras das versões).** Dado um parque com tarifário:
  - uma versão com `validFrom` no passado dá 400;
  - apagar a versão em vigor dá 409;
  - apagar a última versão futura dá 204 e reabre a anterior (`validTo = null`).
- **AC-10 (auditoria só por acréscimo).** Ligado como o utilizador da aplicação:
  - `INSERT` e `SELECT` em `audit_entries` funcionam;
  - `UPDATE`, `DELETE` e `TRUNCATE` falham com `42501` (permissão negada);
  - `DELETE` em `outbox_messages` também falha.
- **AC-11 (unicidade).** Dado um lugar `A-001` num parque e um sensor com `hardwareId` X:
  - criar outro `A-001` no mesmo parque dá 409;
  - criar `A-001` noutro parque dá 201;
  - criar outro sensor X dá 409.
- **AC-12 (concorrência otimista).** Dado um parque:
  - um `PUT` com um `If-Match` antigo dá 412;
  - um `PUT` sem `If-Match` dá 428;
  - um `PUT` com o `ETag` atual dá 200 e um `ETag` novo.
- **AC-13 (apagar filhos com dependentes).** Apagar uma zona com lugares dá 409; apagar um lugar com sensor dá 409.
- **AC-14 (sensores).** Dado um sensor de ocupação no lugar L1:
  - reatribuí-lo para o lugar livre L2 do mesmo parque dá 200 e um `SensorReassigned.v1` com `previousSpotId = L1`;
  - reatribuí-lo para um lugar com sensor dá 409;
  - reatribuí-lo para um lugar de outro parque dá 400;
  - `DELETE /api/sensors/{id}/spot` deixa `spotId = null` e emite `SensorUnassigned.v1`;
  - um sensor ambiental com `spotId` dá 400.
- **AC-15 (lote).** Dado um parque com uma zona:
  - um lote de 1 000 lugares com sensores dá 201 e cria 1 000 lugares, 1 000 sensores, 2 000 `AuditEntry` e 2 000 `OutboxMessage`;
  - um lote de 1 001 dá 400;
  - um lote com um código já existente dá 409 e não cria nada.
- **AC-16 (mudar de zona).** Dado um lugar na zona Z1:
  - mudá-lo para a zona Z2 do mesmo parque dá 200 e um `SpotUpdated.v1` com `previousZoneId = Z1` e `zoneId = Z2`;
  - mudá-lo para uma zona de outro parque dá 400.
- **AC-17 (validação).** Pedidos com latitude fora de [−90, 90], fuso desconhecido, horário com `opensAt ≥ closesAt`, `pricePerHour` negativo ou `billingIncrementMinutes = 0` dão 400 com `ValidationProblemDetails`, e o *handler* não é chamado.
- **AC-18 (leitura e paginação).** Dados 25 lugares:
  - `GET …/spots?page=2&pageSize=10` devolve 10 itens e `totalCount = 25`;
  - `pageSize = 101` dá 400;
  - um parque inexistente dá 404.
- **AC-19 (restrições do agregado de sessão).** Ligado como o utilizador da aplicação, diretamente na base de dados:
  - duas reservas `Active` sobrepostas no mesmo lugar falham com `23P01`;
  - dois pagamentos `Succeeded` da mesma sessão falham com `23505`;
  - apagar uma versão de tarifário com sessões falha com `23503`.
- **AC-20 (novos erros).** Dados `Error.PreconditionFailed` e `Error.PreconditionRequired`, quando são convertidos em resposta HTTP, então dão 412 e 428 com ProblemDetails.
- **AC-21 (arquitetura).** Dados os *assemblies*, quando os testes de arquitetura correm, então:
  - não existe nenhum tipo genérico `IRepository<T>`;
  - as interfaces dos repositórios estão em `Application/Abstractions` e não devolvem `IQueryable`;
  - os eventos de domínio são `sealed record` no Domain;
  - os AC-1 a AC-3 da spec 000 continuam a passar.
- **AC-22 (modos).** Dada a Api em `Hybrid` e em `Monolithic`, quando se resolvem as portas de configuração (repositórios, portas de leitura, `IUnitOfWork`, `ICurrentActor`), então todas são resolvidas.
- **AC-23 (documental: `.http` e ADR).**
  - `tests/http/configuration.http` tem um pedido por *endpoint* de «API Contract», a começar no operador de referência;
  - os ADR do papel da aplicação e das versões de tarifário existem e estão no índice;
  - `docs/RASTREABILIDADE.md` liga RF-06 aos AC.

## Deferred Decisions

- D1–D10: ver a spec 000. D10 é aplicada como no ADR 0003. A F2 usa o valor por omissão de D1 (sessões em PostgreSQL) e de D2 (*outbox*; o leitor é da F3/F4).
- Um intervalo de funcionamento por dia. Reabre-se se a demonstração (F13) precisar de pausas; o recurso é uma migração para várias linhas por dia.

## Implementação em duas partes

A fase passa o limite de cerca de 15 passos do plano (princípio 2 do índice). Por isso, a especificação é uma só e a implementação divide-se em duas sessões (Diogo, 2026-11-24):

- **F2a — domínio, esquema e parques:** AC-1, AC-2, AC-3, AC-4, AC-5, AC-6, AC-10, AC-12, AC-17, AC-18, AC-19, AC-20, AC-21 e AC-22. Inclui todas as entidades e a migração `Initial`, o papel da aplicação, a auditoria e a *outbox*, os *endpoints* de parques e a extensão da fundação.
- **F2b — zonas, lugares, sensores e tarifários:** AC-7, AC-8, AC-9, AC-11, AC-13, AC-14, AC-15, AC-16 e AC-23. Sem migrações novas, exceto se a F2a revelar um erro no esquema; nesse caso, a especificação volta a «In Review».

## Open Questions

Nenhuma.
