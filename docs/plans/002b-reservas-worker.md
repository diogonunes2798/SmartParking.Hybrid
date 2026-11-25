# Plano 002b — Reservas e *worker* de expiração (F3b)

- **Especificação:** [002-sessoes-pagamentos](../specs/002-sessoes-pagamentos.md) (aprovada a 2026-11-25), secção «Implementação em duas partes»
- **Estado:** Aprovado pelo Diogo (2026-11-25), tal como está, incluindo as recomendações dos pontos 1 a 3 (login próprio `smartparking_workers`; proteção do lugar à letra; ordem dos erros na criação) e as propostas 4 a 15. Implementado no ramo `feat/002-sessoes-pagamentos` (2026-11-25); Integrado na `main` com a F3a ([PR #3](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/3), *merge commit* `b1d7e6e`, 2026-11-25).
- **Requisitos:** UC-02 (reservar, cancelar e estacionar com reserva); RF-02 (atomicidade das transições de reserva, cumprimento na transação da abertura, concorrência por `xmin`); RNF-06 (auditoria só por acréscimo, com o actor `system` no *worker* e os Workers ligados como papel da aplicação)
- **Ramo:** `feat/002-sessoes-pagamentos`, o mesmo da F3a (último *commit* do plano 002a: `9f41f33`). O PR para a `main` sai no fim desta parte, com o OK do Diogo.
- **Critérios desta parte:** AC-22 a AC-30. No AC-21, as transições criar, cancelar, abrir com reserva e expirar.

## Contexto

A F3a deixou:
- a `Reservation` como raiz de agregado só com estrutura (`Create`, `Status`, `Version` = `xmin`), a tabela `reservations` com o `EXCLUDE` das reservas `Active` sobrepostas no mesmo lugar e o `ck_reservations_interval`;
- o `OpenSession` com um 409 temporário, `Session.ReservationNotSupported`, quando o pedido traz `reservationId` (decisão 2 do plano 002a);
- o `SessionOpened.v1` já com `reservationId?`;
- a auditoria por entidade alterada (ADR 0006): `Raise(evento, entidades alteradas)`, uma `OutboxMessage` por evento e uma `AuditEntry` por entidade; a raiz que levanta o evento tem sempre o `xmin` verificado;
- `ParkingFacility.IsOpenAt(instante)`, com `[opensAt, closesAt)` no fuso IANA do parque (decisão 4 do plano 002a);
- nos testes: o `FakeTimeProvider` escrito à mão em `tests/Common`, os *hosts* `ApiFixture.Sessions` e `ApiFixture.Gated`, a `ConcurrencyGate` com `GatedUnitOfWork`, e o `RaceAsync` privado em `SessionConcurrencyTests`;
- os Workers a ligar como dono (`Username=smartparking` no `appsettings.json`), ponto em aberto desde a F2, e sem nenhum *background service*.

A F3b junta o ciclo da reserva (`Active → Cancelled | Fulfilled | Expired`), substitui o 409 temporário pelo cumprimento, e cria o primeiro *worker* do projeto. Nada disto precisa de migrações: o esquema da F2 e da F3a já tem tudo (tabela, `EXCLUDE`, `xmin`).

## Pontos a decidir com o Diogo

Nenhum muda a especificação. São escolhas de implementação ou leituras literais de regras que a especificação não detalha; o plano segue a recomendação se o Diogo o aprovar tal como está.

1. **Login dos Workers.** A especificação diz que «a Api e os Workers ligam pelo utilizador `LOGIN` membro de `smartparking_app`». As duas leituras cumprem o AC-29:
   - **(recomendada) um login próprio, `smartparking_workers`**, membro do mesmo grupo. O `pg_stat_activity` e os registos do PostgreSQL distinguem a Api dos Workers, o que ajuda as medições da F12 (ligações por processo). Custo: o `scripts/db-app-user.sh` passa a criar os dois logins, o `.env.example` ganha `POSTGRES_WORKERS_PASSWORD`, e o `DatabaseContainers` cria um segundo login nos testes;
   - reutilizar o `smartparking_api`. Menos alterações, mas o nome engana e as duas aplicações ficam indistinguíveis na base.
2. **Proteção do lugar só sem `reservationId`** (leitura literal de «Ciclo da reserva → Proteção do lugar»):
   - uma abertura **com** `reservationId` só verifica as três condições do cumprimento; não verifica as janelas de outras reservas do mesmo lugar. O `EXCLUDE` impede reservas sobrepostas, mas as janelas de duas reservas seguidas podem cruzar-se (por exemplo, A das 09:00 às 09:20 e B das 09:20 às 10:00: às 09:10 as duas janelas estão abertas);
   - uma abertura **sem** `reservationId` do **mesmo** veículo, dentro da janela da sua própria reserva, não é bloqueada (a regra fala de «outro veículo»). A sessão abre sem cumprir a reserva, que continua `Active` e expira mais tarde.
3. **Ordem dos erros na criação da reserva:** 400 de forma (validador) → 404 (lugar, utilizador, veículo) → 400 das regras de tempo e horário → 409 (lugar ou parque inativos, veículo de outro utilizador) → 409 da sobreposição (`23P01`, na gravação). A regra do horário precisa do parque, por isso os 404 vêm antes dela.

## Propostas do plano, para aprovar com ele

Nenhuma muda a especificação nem as decisões D1–D10.

4. **Política da reserva no Domain.** Um `sealed record ReservationPolicy(TimeSpan MaxDuration, TimeSpan MaxAdvance, TimeSpan EarlyArrival, TimeSpan ExpiryGrace)` em `Domain/Sessions`, com:
   - `FulfilmentWindow(startsAt)` → `[startsAt − EarlyArrival, startsAt + ExpiryGrace]`, **fechada nos dois extremos** (a especificação escreve a janela com parênteses retos);
   - `IsDue(startsAt, now)` → `now > startsAt + ExpiryGrace` (expira estritamente depois do fim da janela, por isso o cumprimento e a expiração nunca se sobrepõem).

   O Domain não lê configuração: a política chega pelo *handler*.
5. **Configuração `Reservations`.** `ReservationOptions` em `src/SmartParking.Infrastructure/` (ao lado de `PersistenceOptions`), com as cinco chaves da especificação e os valores por omissão dela: `MaxDurationHours` 24, `MaxAdvanceDays` 7, `EarlyArrivalMinutes` 15, `ExpiryGraceMinutes` 15, `ExpiryScanSeconds` 60.
   - `AddInfrastructure` faz `Bind` e `Validate` (horas, dias e segundos > 0; minutos ≥ 0) com `ValidateOnStart`, nos dois modos, e regista um `ReservationPolicy` *singleton* feito a partir das opções;
   - a Application recebe o `ReservationPolicy`, não `IOptions`: o projeto não tem o pacote de opções e não ganha nenhum (sem pacotes novos);
   - os dois `appsettings.json` (Api e Workers) ganham a secção com os valores por omissão, para que a Api e os Workers usem a mesma tolerância. Uma tolerância diferente nos dois processos seria um erro de configuração (ver «Riscos»).
6. **`Reservation.Create` passa a `Reserve`**, que normaliza os instantes e levanta `ReservationCreated` (como `ParkingSession.Start` passou a `Open` na F3a). O `InvariantTests` da F2 passa a chamar `Reserve`, com o mesmo invariante (`endsAt > startsAt`).
7. **Cumprimento sem evento próprio.** `Reservation.Fulfil(…)` é *internal* ao Domain e só o `ParkingSession.Open` a chama. Não levanta evento: o `SessionOpened` é levantado pela sessão com as entidades alteradas `session, reservation` (ADR 0006). A reserva é outra raiz, carregada com *tracking* e `Modified`, por isso a gravação verifica também o `xmin` dela. É o que torna o AC-27 determinístico.
8. **Duas raízes numa transação.** A especificação exige que a reserva passe a `Fulfilled` na transação da abertura da sessão. É uma exceção consciente à regra de uma raiz por transação: fica registada no ADR 0007 (abaixo), com a alternativa (cumprimento assíncrono pela *outbox*) e a razão para não a seguir (a janela de cumprimento e a proteção do lugar teriam de lidar com uma reserva ainda `Active` depois de cumprida).
9. **Proteção do lugar numa consulta compilada.** É uma pré-condição de cada abertura sem reserva (caminho quente do UC-02), por isso fica no `IReservationRepository` como `IsSpotHeldForAnotherVehicleAsync(spotId, vehicleId, now, policy)`, com `EF.CompileAsyncQuery`, `AsNoTracking` e `Any`: existe uma reserva `Active` do lugar, de outro veículo, com `startsAt ∈ [now − ExpiryGrace, now + EarlyArrival]` (o mesmo que `now` dentro da janela). Segue o padrão do `GetOpenByVehicleAsync` da F3a.
10. ***Worker* em três peças**, para os testes correrem uma passagem sem temporizador:
    - **Application:** a consulta `ListDueReservations` (porta de leitura `IReservationReader.ListDueIdsAsync(cutoff)`, só os `id`, `AsNoTracking`, por `starts_at`) e o comando `ExpireReservation(reservationId)`. O comando relê a reserva com *tracking*; se já não estiver `Active` ou ainda não estiver vencida, devolve `Skipped` sem gravar; senão `Expire`, grava, e converte o 412 de um `xmin` desatualizado em 409 `Reservation.ConcurrentChange`;
    - **Workers, `ReservationExpiryPass`:** lista os vencidos num *scope* e corre cada `ExpireReservation` no seu próprio *scope* (um `DbContext` e uma transação por reserva). Um 409 conta como conflito e não para a passagem (AC-28). Devolve `ExpiryPassResult(Expired, Conflicts, Skipped)` e regista um resumo com *message template* (só contagens e ids);
    - **Workers, `ReservationExpiryWorker : BackgroundService`:** uma passagem ao arrancar e depois uma por período, com `new PeriodicTimer(TimeSpan.FromSeconds(ExpiryScanSeconds), timeProvider)`. Uma `DbException` numa passagem (por exemplo, a base em baixo) é registada como aviso e a passagem seguinte tenta de novo; não há `catch (Exception)`. Qualquer outra exceção para o *host* (comportamento por omissão do .NET), o que torna a falha visível.
11. **Actor `system`.** `SystemCurrentActor : ICurrentActor` em `src/SmartParking.Workers/Hosting/`, registado no `Program.cs` dos Workers **antes** do `AddInfrastructure`, que usa `TryAddSingleton` para o `anonymous`. A Api não muda.
12. **Relógio e passagem nos testes do *worker*.** O `WorkersFixture` ganha um `Clock` (`FakeTimeProvider`) e um *host* `Expiry` com o relógio e **sem** o `ReservationExpiryWorker` (o teste remove o descritor do `IHostedService` com esse tipo de implementação). Os testes fixam o instante, resolvem o `ReservationExpiryPass` dos serviços do *host* e chamam `RunAsync` uma vez. O `SmartParking.Workers.csproj` ganha `InternalsVisibleTo(SmartParking.Workers.Tests)`. Um teste à parte arranca o *worker* real (com `ExpiryScanSeconds = 1`) para provar a ligação ao *host*.
13. **Conflito de `xmin` determinístico no AC-28.** `tests/Common/BeforeSaveUnitOfWork.cs`: um decorador do `IUnitOfWork` (como o `GatedUnitOfWork`) que corre uma ação uma só vez antes da gravação de uma reserva escolhida. No teste, a ação altera essa linha como dono (`UPDATE reservations SET status = 'Cancelled' WHERE id = $1`), o que muda o `xmin` entre a leitura e a gravação do *worker*. A gravação afeta 0 linhas → 412 → 409 → conflito contado, e a passagem continua com as outras.
14. ***Fixtures* partilhadas entre os dois projetos de teste.**
    - O `DbProbe` passa de `Api.Tests/Fixtures` para `tests/Common` (namespace `SmartParking.Tests.Common`); o `Api.Tests.csproj` ganha `<Using Include="SmartParking.Tests.Common" />`, para não mexer em cada ficheiro;
    - `tests/Common/ReservationGraph.cs`: parque (Europe/Lisbon, das 08:00 às 20:00 todos os dias), zona, lugar, utilizador e veículo inseridos por SQL como login da aplicação, mais `InsertReservationAsync(status, startsAt, endsAt)`. Serve os testes do *worker*, que não podem chamar a Api;
    - o `RaceAsync` sai do `SessionConcurrencyTests` para uma extensão da `ConcurrencyGate` (`gate.RaceAsync(first, second)`), usada também pelo AC-27.
15. **ADR 0007 — Reservas: cumprimento na transação da abertura e expiração por *worker*** (RF-02, RNF-06; tese 5.4.1, 5.4.2): as duas raízes numa transação (ponto 8), uma transação por reserva no *worker* e o conflito de `xmin` ignorado, o actor `system`, o login dos Workers (ponto 1) e a ausência de índice para a varredura (ver «Riscos»).

## Cumprimento da reserva no `OpenSession`

O 409 `Session.ReservationNotSupported` e o teste `Open_WithReservationId_Returns409UntilF3b` saem. A ordem das verificações passa a ser:

1. 404: lugar; veículo; reserva, quando o pedido traz `reservationId`.
2. Com `reservationId`, 409 `Session.ReservationMismatch` se o lugar ou o veículo não forem os da reserva; 409 `Session.ReservationNotActive` se não estiver `Active`; 409 `Session.OutsideFulfilmentWindow` se `now` estiver fora de `policy.FulfilmentWindow(startsAt)`.
3. As pré-condições da F3a, sem alterações: lugar inativo, parque inativo, parque fechado, sem tarifário em vigor, veículo com sessão aberta.
4. Sem `reservationId`, 409 `Session.SpotReserved` se `IsSpotHeldForAnotherVehicleAsync` (ponto 9).
5. `ParkingSession.Open(…, reservation, now, policy)`:
   - sem reserva, igual à F3a;
   - com reserva, chama `reservation.Fulfil(spotId, vehicleId, now, policy)` (que volta a verificar as três condições e lança `InvalidOperationException` se falharem, porque o *handler* já as verificou) e levanta `SessionOpened(…, reservationId)` com as entidades alteradas `this, reservation`.
6. Uma gravação. O 412 de um `xmin` desatualizado (da sessão não pode ser, porque é nova; da reserva, sim) passa a 409 `Session.ConcurrentChange` pelo `SessionErrors.FromSave` que já existe.

Resultado de uma abertura com reserva: uma `OutboxMessage` (`SessionOpened.v1` com `reservationId`) e duas `AuditEntry` (sessão `Created`, reserva `Modified`) com o mesmo `outboxMessageId` e o mesmo `xmin` (AC-25).

| Transição | Raiz que levanta | Evento | Entidades alteradas (auditorias) |
|---|---|---|---|
| Criar reserva | `Reservation` | `ReservationCreated.v1` | reserva (1) |
| Cancelar | `Reservation` | `ReservationCancelled.v1` | reserva (1) |
| Abrir com reserva | `ParkingSession` | `SessionOpened.v1` (com `reservationId`) | sessão e reserva (2) |
| Expirar (*worker*) | `Reservation` | `ReservationExpired.v1` | reserva (1), actor `system` |

## Regras de criação e a regra «cabe num só intervalo de um dia»

- **Validador** (Api, 400): `spotId`, `userId`, `vehicleId` não vazios; `startsAt` e `endsAt` presentes; `endsAt > startsAt` (o mesmo que o `ck_reservations_interval`).
- ***Handler*** (`now` normalizado do `TimeProvider`), pela ordem do ponto 3:
  - 404 `Spot.NotFound`, `User.NotFound`, `Vehicle.NotFound`;
  - 400 `Reservation.StartsInPast` (`startsAt < now`), `Reservation.TooLong` (`endsAt − startsAt > MaxDuration`), `Reservation.TooFarAhead` (`startsAt > now + MaxAdvance`), `Reservation.OutsideOpeningHours`;
  - 409 `Reservation.SpotInactive`, `Reservation.FacilityInactive`, `Reservation.VehicleOfAnotherUser`;
  - gravação: o `23P01` do `EXCLUDE` chega como 409 `Database.ExclusionViolation` (tradutor da F2) e o *handler* troca-o por 409 `Reservation.Overlaps`, com uma mensagem que o condutor percebe.
- **`ParkingFacility.FitsInOneOpeningInterval(startsAt, endsAt)`** (Domain, novo), construído sobre o `IsOpenAt`:
  - `IsOpenAt(startsAt)`;
  - `IsOpenAt(endsAt − 1 µs)`, porque o intervalo da reserva é `[startsAt, endsAt)` e os instantes estão normalizados ao microssegundo (`Timestamps.Normalize`): uma reserva pode acabar às `closesAt` exatas;
  - a data local dos dois extremos, no fuso do parque, é a mesma.

  Como cada dia tem no máximo um intervalo `[opensAt, closesAt)` com `opensAt < closesAt`, as três condições juntas garantem que a reserva inteira cabe nesse intervalo. O fuso é resolvido uma vez para os dois extremos.

## Concorrência cancelar/cumprir (AC-27)

Usa a barreira da F3a sem alterações (`ApiFixture.Gated`):
1. arrange no *host* `Gated` com a barreira desarmada: parque, lugar, reserva do veículo A; o relógio dentro da janela;
2. `fixture.Gate.RaceAsync(cancelar, abrir com reservationId)`;
3. os dois leem a reserva com o mesmo `xmin` e chegam à barreira; os dois gravam. O segundo `UPDATE reservations … WHERE xmin = @lido` espera pelo bloqueio da linha e depois afeta 0 linhas → 412 → 409 (`Reservation.ConcurrentChange` ou `Session.ConcurrentChange`). Na abertura que perde, o `INSERT` da sessão é desfeito com o resto da transação;
4. asserções: um 2xx e um 409 com ProblemDetails; a reserva está `Cancelled` ou `Fulfilled`, de acordo com quem ganhou; há uma só mensagem da reserva na *outbox* (`ReservationCancelled.v1`, ou `SessionOpened.v1` com esse `reservationId`); se o cancelamento ganhou, não há nenhuma sessão do veículo A.

O teste corre duas vezes seguidas na verificação, como o AC-11 e o AC-16.

## Ficheiros a criar ou alterar

Nenhum projeto novo, nenhum pacote novo, nenhuma migração.

**Domain**
- `Sessions/Reservation.cs`: `Reserve` (em vez de `Create`), `Cancel(now)`, `Fulfil(…)` (*internal*), `Expire(now, policy)`. Cada transição só a partir de `Active`; quebrar a pré-condição lança `InvalidOperationException`.
- `Sessions/ReservationPolicy.cs` (novo; ponto 4).
- `Sessions/Events/`: `ReservationCreated` (`id`, `spotId`, `userId`, `vehicleId`, `startsAt`, `endsAt`), `ReservationCancelled` e `ReservationExpired` (`id`, `spotId`, `vehicleId`, `startsAt`). Só identificadores e instantes: nenhum dado pessoal.
- `Sessions/ParkingSession.cs`: `Open` com `Reservation?` e `ReservationPolicy`.
- `Facilities/ParkingFacility.cs`: `FitsInOneOpeningInterval`.

**Application**
- `Abstractions/Persistence/IReservationRepository.cs`: `GetByIdAsync` (com *tracking*), `Add`, `IsSpotHeldForAnotherVehicleAsync`.
- `Abstractions/Reading/IReservationReader.cs`: `GetAsync(id)` e `ListDueIdsAsync(cutoff)`; `Reading/ReservationView.cs`.
- `Features/Reservations/`: `CreateReservation`, `GetReservation`, `CancelReservation`, `ListDueReservations`, `ExpireReservation` (com `ExpiryOutcome`: `Expired`, `Skipped`), `ReservationErrors`.
- `Features/Sessions/OpenSession.cs` e `SessionErrors.cs`: o cumprimento e a proteção do lugar; sai o `ReservationNotSupported`.

**Infrastructure**
- `ReservationOptions.cs` (novo) e `DependencyInjection.cs` (*bind*, validação e `ReservationPolicy`, nos dois modos).
- `Relational/Repositories/ReservationRepository.cs` (o `GetByIdAsync` compilado e com *tracking*, como o da sessão; a consulta compilada da proteção).
- `Relational/Reading/ReservationReader.cs`.
- `Relational/RelationalRegistration.cs`: as duas portas novas, iguais nos dois modos.

**Api**
- `Features/Reservations/`: `ReservationEndpoints` (`/api/reservations`, `/{id}`, `/{id}/cancel`, `AllowAnonymous` no grupo), `CreateReservationRequest`, `CreateReservationRequestValidator`, `ReservationResponse`. O OpenAPI declara 400, 404 e 409.
- `appsettings.json`: a secção `Reservations`.

**Workers**
- `Program.cs`: `SystemCurrentActor` antes do `AddInfrastructure`; `AddReservationExpiry()`.
- `Hosting/SystemCurrentActor.cs`; `Reservations/ReservationExpiryPass.cs`, `ReservationExpiryWorker.cs`, `ReservationExpiryRegistration.cs`, `ExpiryPassResult.cs`.
- `appsettings.json`: `Username=smartparking_workers` (ou `smartparking_api`, conforme o ponto 1) e a secção `Reservations`.
- `SmartParking.Workers.csproj`: `InternalsVisibleTo(SmartParking.Workers.Tests)`; o comentário «*Background services arrive from F3/F4*» é atualizado.

**Scripts e ambiente** (só com a opção recomendada do ponto 1)
- `scripts/db-app-user.sh`: cria ou atualiza também o `smartparking_workers`, com a mesma técnica (palavra-passe pelo *stdin*, `log_statement` desligado).
- `.env.example`: `POSTGRES_WORKERS_PASSWORD`.

**Testes**
- `tests/Common/`: `DbProbe` (movido), `ReservationGraph`, `BeforeSaveUnitOfWork`; `DatabaseContainers` com `WorkersLogin` e `WorkersConnectionString` (ponto 1) e `Settings(mode, login)`.
- `tests/SmartParking.Api.Tests/`:
  - `Fixtures/ReservationApi.cs` (criar, cancelar, consultar; a reserva de referência às 11:00–12:00 de 2026-12-02, em dezembro e por isso UTC+0 em Lisboa) e a extensão `RaceAsync`;
  - `Features/Reservations/`: `CreateReservationTests`, `CancelReservationTests`, `GetReservationTests`, `ReservationConcurrencyTests`;
  - `Features/Sessions/`: `OpenSessionWithReservationTests` (AC-25, AC-26); `OpenSessionTests` perde o teste «até à F3b»; `TransitionOutboxTests` ganha três linhas;
  - `Features/HttpFiles/HttpFileTests.cs`: `sessions.http` passa a ter os prefixos `/api/users`, `/api/sessions` e `/api/reservations`.
- `tests/SmartParking.Workers.Tests/`:
  - `Fixtures/WorkersFixture.cs`: `Clock`, o *host* `Expiry` e `CreateFactory(…, configureServices)`;
  - `Features/Reservations/ReservationExpiryTests` (AC-28, AC-21 expirar) e `ReservationExpiryWorkerTests` (o *worker* real ligado ao *host*);
  - `Features/Configuration/WorkersAppLoginTests` (AC-29) e um caso de configuração inválida de `Reservations`.
- `tests/SmartParking.UnitTests/Domain/`: `ReservationTests`, `ReservationPolicyTests`, `OpeningHoursTests` (`FitsInOneOpeningInterval`), `ParkingSessionTests` (abrir com reserva); `InvariantTests` passa a `Reserve`.
- `tests/http/sessions.http`: criar, consultar e cancelar uma reserva, e abrir uma sessão com `reservationId`.

**Documentação**
- Este plano e `docs/plans/README.md`.
- `docs/adr/0007-reservas-cumprimento-e-expiracao.md` e o índice dos ADR.
- `docs/specs/README.md` (estado da 002), `docs/RASTREABILIDADE.md`, `docs/DIARIO.md`.

## (a) UC, RF, RNF e AC

- **UC-02:** reservar um lugar, cancelar, estacionar com a reserva (cumprimento) e a expiração automática.
- **RF-02:**
  - atomicidade de cada transição de reserva (uma gravação com o negócio, a auditoria e uma `OutboxMessage`): AC-22, AC-24, AC-28, AC-21;
  - cumprimento na mesma transação da abertura, com duas auditorias e um evento: AC-25;
  - concorrência por `xmin`: AC-27 (cancelar/cumprir) e AC-28 (*worker* e outro pedido);
  - regras do ciclo: AC-23, AC-24, AC-26, AC-30.
- **RNF-06:** a auditoria do *worker* com actor `system` (AC-28); os Workers deixam de ligar como dono e a auditoria fica só por acréscimo também para eles (AC-29).
- **RNF-01:** sem alvo nesta fase. Nenhum valor de desempenho é escrito.
- **AC:** AC-22 a AC-30 por inteiro; AC-21 nas transições criar, cancelar, abrir com reserva e expirar.

## (c) Skills do kit e adaptações

| Skill | Uso | Adaptação à especificação |
|---|---|---|
| `modern-csharp` | *records*, `sealed`, *primary constructors* | Eventos `sealed record` com `[DomainEvent]`; `ReservationPolicy` como *record* |
| `clean-architecture` | Portas em `Application/Abstractions`; Domain sem dependências | A política da reserva e a regra do horário no Domain; a configuração entra pela Infrastructure como `ReservationPolicy`, não como `IOptions` |
| `ef-core` | Repositório, leitor, consultas compiladas | Exceção D10. A proteção do lugar é uma consulta compilada com `Any`. Sem migração: o `EXCLUDE` e o `xmin` da F2 chegam |
| `error-handling` | Result e ProblemDetails | 412 → 409 nas transições sem `If-Match` (cancelar, cumprir, expirar); `23P01` → 409 `Reservation.Overlaps`; no *worker*, só `DbException` é apanhada, por passagem |
| `minimal-api`, `openapi`, `scalar` | `IEndpointGroup` por recurso, `TypedResults` | Sem ETag nem `If-Match` (especificação, «Concorrência sem `If-Match`»); `AllowAnonymous` no grupo |
| `configuration` | Opções com validação no arranque | `Reservations:*` com os valores por omissão da especificação e `ValidateOnStart`, iguais na Api e nos Workers |
| `dependency-injection` | Registo das portas e do *worker* | `SystemCurrentActor` registado antes do `TryAdd` do `anonymous`; um *scope* por reserva no *worker*; nos testes, o *hosted service* é removido e o `IUnitOfWork` decorado |
| `messaging` | *Outbox* | Só a escrita; o *worker* não publica nada (F6) |
| `serilog`, `opentelemetry` | Registos estruturados | *Message templates* com ids e contagens; nenhum dado pessoal. Métricas do *worker* ficam para a F12, se forem precisas |
| `testing` | Testcontainers, AAA, `…_ACn` | O relógio escrito à mão; uma passagem chamada diretamente; o conflito de `xmin` provocado por um decorador, sem *mocks* |

## (d) Decisões D1–D10

- **D1** (por omissão): as reservas em PostgreSQL, na mesma base das sessões, o que permite o cumprimento na transação da abertura.
- **D2** (por omissão): uma `OutboxMessage` por transição, incluindo as do *worker*; a publicação continua para a F6 (`published_at = null`).
- **D10** (ADR 0003): um repositório novo (`IReservationRepository`) e uma porta de leitura (`IReservationReader`). Nenhum repositório genérico nem `IQueryable`; o AC-21 e o AC-22 da F2 continuam a verificar isto.
- Nenhuma outra decisão é tocada nem alterada.

## (e) Correspondência AC → passo → teste

Os testes da Api correm no *host* `Sessions` (ou `Gated`, no AC-27): modo `Hybrid`, login da aplicação, `FakeTimeProvider`. Os do *worker* correm no *host* `Expiry` dos Workers. Cada teste fixa o seu instante e avança o relógio entre transições (diário da F3a).

| AC | Passo | Teste |
|---|---|---|
| AC-21 | 6, 7, 8, 12, 13 | `Sessions/TransitionOutboxTests.Transition_WritesExactlyOneOutboxMessage_AC21` ganha três linhas: criar → `ReservationCreated.v1`, cancelar → `ReservationCancelled.v1`, abrir com reserva → `SessionOpened.v1` (conta pelo `xmin` da linha da reserva). `Workers.Tests/Reservations/ReservationExpiryTests.Expire_WritesExactlyOneOutboxMessage_AC21` (expirar → `ReservationExpired.v1`), porque a Api não pode correr o *worker* |
| AC-22 | 6 | `Reservations/CreateReservationTests.Create_Valid_Returns201ActiveOneAuditOneEvent_AC22` |
| AC-23 | 2, 6 | `CreateReservationTests.Create_RuleBroken_Returns400AndWritesNothing_AC23` (*theory*: `startsAt` no passado, duração de 24 h + 1 min, antecedência de 7 dias + 1 min, fora do horário: começa às 07:30, acaba às 20:30, atravessa a meia-noite, dia sem horário); `Create_ConflictRule_Returns409AndWritesNothing_AC23` (*theory*: lugar `Inactive`, parque `Inactive`, veículo de outro utilizador, sobreposição com outra reserva `Active`). Extras: `Create_EndsAtClosingTime_Returns201` e `Create_OverlapsCancelledReservation_Returns201` (o `EXCLUDE` só conta as `Active`) |
| AC-24 | 7 | `Reservations/CancelReservationTests.Cancel_Active_Returns200CancelledOneEvent_AC24`, `Cancel_Again_Returns409_AC24` |
| AC-25 | 2, 8 | `Sessions/OpenSessionWithReservationTests.Open_WithReservationInWindow_Returns201FulfilledOneEventTwoAudits_AC25` (as duas auditorias com o mesmo `outbox_message_id` e o mesmo `xmin` da mensagem); `Open_WithReservation_Fails409AndWritesNothing_AC25` (*theory*: 1 µs antes da margem, 1 µs depois da tolerância, outro veículo, outro lugar). Extras: nos dois extremos exatos da janela dá 201; uma reserva `Cancelled` dá 409; um `reservationId` inexistente dá 404 |
| AC-26 | 2, 8 | `OpenSessionWithReservationTests.Open_OtherVehicleInWindow_Returns409_AC26`, `Open_OtherVehicleAfterGrace_Returns201_AC26` (a reserva ainda está `Active`: o *worker* não correu). Extra: na margem de chegada antecipada (`startsAt − 15 min`) já dá 409 |
| AC-27 | 9 | `Reservations/ReservationConcurrencyTests.CancelAndFulfil_ConcurrentOnSameXmin_OneSucceedsOne409SingleReservationEvent_AC27` (barreira) |
| AC-28 | 11, 12 | `Workers.Tests/Reservations/ReservationExpiryTests.Pass_ThreeReservations_ExpiresOnlyTheDueOne_AC28` (vencida, dentro da tolerância, `Cancelled`; a auditoria tem `actor = system` e há um `ReservationExpired.v1`); `Pass_XminConflictOnOne_ExpiresTheOthers_AC28` (ponto 13: a reserva em conflito fica `Cancelled`, sem evento do *worker*; as outras duas ficam `Expired`; `ExpiryPassResult` com 1 conflito) |
| AC-29 | 11, 12 | `Workers.Tests/Configuration/WorkersAppLoginTests.UpdateAudit_WithWorkersConnection_Fails42501_AC29` (o `NpgsqlDataSource` resolvido do *host* dos Workers executa o `UPDATE audit_entries` e recebe `42501`; o `current_user` não é superutilizador e é membro de `smartparking_app`); `WorkersAppSettings_Postgres_IsApplicationLoginNotOwner_AC29` (lê o `src/SmartParking.Workers/appsettings.json`, como o `ContainerImagesTests` lê o `compose.yaml`) |
| AC-30 | 6 | `Reservations/GetReservationTests.Get_AfterCreateAndCancel_ReturnsCurrentStatus_AC30`, `Get_UnknownId_Returns404_AC30` |

Testes sem AC:
- `ReservationTests` (transições só a partir de `Active`; `Fulfil` não levanta evento; `Reserve` normaliza os instantes);
- `ReservationPolicyTests` (janela fechada nos dois extremos; `IsDue` estritamente depois);
- `OpeningHoursTests` (`FitsInOneOpeningInterval`: acaba às `closesAt`, atravessa a meia-noite, dia sem horário, data de verão);
- `ParkingSessionTests` (abrir com reserva: duas entidades alteradas, um evento);
- `ReservationExpiryWorkerTests.Worker_Started_ExpiresDueReservation` (o *worker* real, com `ExpiryScanSeconds = 1`; espera até 30 s pela linha `Expired`);
- `Startup_ReservationsOptionInvalid_FailsNamingTheKey` (por exemplo, `Reservations:ExpiryScanSeconds = 0`);
- `ReservationExpiryWorkerTests.Worker_DatabaseUnreachable_HostKeepsRunning` (com as `UnreachableSettings`, o `/health/live` continua a responder depois da primeira passagem falhada: a `DbException` não para o *host*).

O AC-21 e o AC-22 da F2 (eventos com `[DomainEvent]`, repositórios sem `IQueryable`) voltam a correr sem alterações e passam a cobrir os tipos novos.

## Passos

1. **Preparação:**
   - este plano em `docs/plans/002b-…md` e no índice;
   - a especificação 002 passa a «Em implementação (F3b)»;
   - a linha RF-02 da RASTREABILIDADE passa a «F3b em implementação».
2. **Domain:** `ReservationPolicy`; `Reservation` (`Reserve`, `Cancel`, `Fulfil`, `Expire`) e os três eventos; `ParkingFacility.FitsInOneOpeningInterval`; `ParkingSession.Open` com reserva. Testes unitários do Domain e o `InvariantTests` atualizado. *Commit* `feat(reservations): reservation lifecycle in the domain (spec 002)`.
3. **Configuração:** `ReservationOptions`, *bind* e validação, o `ReservationPolicy` registado, as secções nos dois `appsettings.json`. Teste de arranque com valor inválido.
4. **Portas e Infrastructure:** `IReservationRepository` (com a consulta compilada da proteção), `IReservationReader`, `ReservationView`, as implementações e o registo. `get_di_registrations` nos dois modos.
5. ***Fixtures* de teste:** `DbProbe` para `tests/Common` (com o `<Using>`), `ReservationGraph`, `BeforeSaveUnitOfWork`, a extensão `RaceAsync`, `ReservationApi`. Os 304 testes da F3a passam sem alterações de comportamento. *Commit* `test(fixtures): …`.
6. **Criar e consultar:** `CreateReservation`, `GetReservation`, `ReservationErrors`, `ReservationEndpoints`, os DTO e o validador. Testes AC-22, AC-23, AC-30 e as linhas «criar» do AC-21.
7. **Cancelar:** `CancelReservation` e a rota. Testes AC-24 e a linha «cancelar» do AC-21.
8. **Cumprir e proteger o lugar:** `OpenSession` (ver a secção própria), sem o `ReservationNotSupported`. Testes AC-25, AC-26 e a linha «abrir com reserva» do AC-21; sai o teste «até à F3b».
9. **Concorrência:** AC-27 com a barreira.
10. **Expiração na Application:** `ListDueReservations` e `ExpireReservation`, com `ExpiryOutcome`.
11. **Workers:**
    - `SystemCurrentActor`, `ReservationExpiryPass`, `ReservationExpiryWorker`, `ReservationExpiryRegistration`, `InternalsVisibleTo`;
    - o login dos Workers: `appsettings.json`, `db-app-user.sh`, `.env.example`, `DatabaseContainers` (conforme o ponto 1);
    - `get_project_graph` (os Workers só dependem da Application e da Infrastructure) e `get_di_registrations` dos Workers.
12. **Testes dos Workers:** o *host* `Expiry` e o relógio no `WorkersFixture`; AC-28, AC-29, a linha «expirar» do AC-21, o *worker* real e a configuração inválida.
13. **`sessions.http` e verificação manual:**
    - `sessions.http` com os pedidos das reservas e o `HttpFileTests` com o prefixo `/api/reservations` (o AC-20 e o AC-23 da F2 continuam a passar);
    - verificação manual contra o `compose.yaml` (ver abaixo).
14. **Verificação:**
    - `/verify`: build com `-warnaserror`, format, `dotnet test` (duas corridas seguidas, por causa do AC-27), `get_diagnostics`, `detect_antipatterns`, `get_project_graph`;
    - testes de mutação, com cópia de segurança dos ficheiros não versionados antes de começar:
      - sem a reserva nas entidades alteradas do `SessionOpened`, a unidade de trabalho grava a reserva `Fulfilled` sem auditoria, e o AC-25 falha (uma só `AuditEntry`);
      - com a janela aberta no fim (`<` em vez de `≤`), o caso «no extremo exato da tolerância» do AC-25 falha;
      - com uma só unidade de trabalho para toda a passagem, o caso de conflito do AC-28 falha (as outras reservas não expiram);
      - sem a conversão 412 → 409 no `CancelReservation`, o AC-27 falha quando o cancelamento perde;
    - revisão independente (`code-reviewer`, só leitura, sobre `git diff 9f41f33...HEAD -- src tests`); os achados tratam-se com o acordo do Diogo.
15. **Documentação e fecho:**
    - ADR 0007 e o índice;
    - RASTREABILIDADE (RF-02 e RNF-06), DIARIO, estado deste plano e da especificação;
    - *commits* atómicos ao longo dos passos (`feat(reservations)`, `feat(sessions)`, `feat(workers)`, `test…`, `docs(adr)…`, todos com «(spec 002)»);
    - preparar o PR da F3a e da F3b para a `main` e pedir o OK do Diogo antes de o abrir;
    - `/checkpoint`.

São 15 passos, dentro do limite, sem nova divisão.

## Linha a atualizar em `docs/RASTREABILIDADE.md`

Na linha RF-02 (matriz): a coluna de critérios fica «AC-1 a AC-30»; a de especificação junta o plano [002b](plans/002b-reservas-worker.md) e o [ADR 0007](adr/0007-reservas-cumprimento-e-expiracao.md); a de código junta `Domain/Sessions/{Reservation,ReservationPolicy}`, `Application/Features/Reservations`, `Api/Features/Reservations` e `Workers/Reservations`; a de testes junta `Api.Tests/Features/Reservations`, `Sessions/OpenSessionWithReservationTests` e `Workers.Tests/Features/Reservations`; o estado fica «F3a e F3b a passar (<data>)».

Na linha RNF-06, a coluna de teste ganha `Workers.Tests/Reservations/ReservationExpiryTests` (AC-28: actor `system`) e `Workers.Tests/Configuration/WorkersAppLoginTests` (AC-29), e o código ganha o login dos Workers em `scripts/db-app-user.sh`.

## Entrada a acrescentar em `docs/DIARIO.md`

```
## F3b: reservas e worker de expiração (especificação 002)

- **Data:** <início> a <fim>
- **Feito:**
  - Plano aprovado pelo Diogo (docs/plans/002b-reservas-worker.md), com os pontos 1 a 3 e as propostas 4 a 15.
  - Ciclo da reserva no Domain (Reserve, Cancel, Fulfil, Expire) e ReservationPolicy; FitsInOneOpeningInterval sobre IsOpenAt.
  - Cumprimento na transação da abertura (duas auditorias, um SessionOpened.v1) e proteção do lugar; sai o 409 temporário da F3a.
  - Worker de expiração (uma transação por reserva, actor system, conflito de xmin ignorado); Workers ligados como <login>.
  - Testes: AC-22 a AC-30 e as transições de reserva do AC-21; <n> testes a passar (304 antes da F3b). Preencher com o resultado real.
  - Verificação manual contra o compose.yaml: <preencher>.
- **Problemas:** <preencher durante a implementação, incluindo as abordagens que falharam e se foi preciso voltar a planear>
- **Decisões:** pontos 1 a 3 e propostas 4 a 15 do plano 002b; ADR 0007; <desvios, com a razão>
- **Commits:** <hashes>
```

## Riscos e pontos de atenção

- **Tolerância diferente na Api e nos Workers.** A janela de cumprimento (Api) e a expiração (Workers) leem a mesma chave em dois processos. Com valores diferentes, uma reserva pode expirar dentro da janela que a Api ainda aceita (o cumprimento dá 409 por estado, não grava nada errado) ou ficar `Active` depois da janela. Os dois `appsettings.json` levam os mesmos valores e o ADR 0007 regista o risco; uma configuração partilhada fica para a F13, se for precisa.
- **Varredura sem índice.** `ListDueIdsAsync` filtra por `status = 'Active' AND starts_at < cutoff`, sem índice próprio (o do `EXCLUDE` é por lugar e intervalo). Com poucas reservas, uma leitura sequencial por minuto é aceitável; se as medições da F12 mostrarem custo, um índice parcial é uma alteração ao esquema e a especificação volta a «In Review». Não se mede nem se escreve nenhum valor agora.
- **Duas raízes numa transação** (ponto 8): o bloqueio da linha da reserva durante a abertura é curto, mas existe; o AC-27 prova que o perdedor recebe 409 e não fica nada a meio.
- **Chegada antecipada antes da abertura do parque.** Uma reserva que começa às `opensAt` tem a margem de chegada antecipada fora do horário: abrir nesse período dá 409 `Session.FacilityClosed`, porque as pré-condições da F3a continuam a valer. É coerente com a especificação; fica registado no diário se aparecer na verificação manual.
- **Ordem dos `occurred_at` com o relógio parado:** os testes avançam o relógio entre a criação, o cancelamento e o cumprimento (lição da F3a).
- **Testes do *worker* partilham a base da coleção:** a passagem expira também reservas vencidas de outros testes. As asserções são sempre por `id` (nunca contagens globais), e o caso de conflito usa ids escolhidos pelo teste.
- **Processo órfão:** o `TaskStop` de `dotnet run` deixa o `.exe` vivo (já aconteceu com a Api na porta 5080); na verificação manual, os Workers também se procuram pelo nome no fim.
- **Dados pessoais:** os eventos das reservas só levam ids e instantes; o *worker* regista ids e contagens, nunca a matrícula.

## Verificação ponta a ponta

1. `dotnet build SmartParking.slnx -warnaserror` e `dotnet format --verify-no-changes`.
2. `dotnet test` (com o Docker ligado), duas vezes seguidas: os 304 testes da F3a e os `…_ACn` da tabela acima. O `hooks/post-test-analyze.sh` continua a não existir (diário da F3a); os resultados leem-se da saída do `dotnet test`.
3. Local:
   - `docker compose up -d --wait`; `scripts/db-app-user.sh` (cria o login dos Workers, conforme o ponto 1);
   - a Api ligada como `smartparking_api`; os Workers ligados com o seu login, com `Reservations__ExpiryGraceMinutes=0` e `Reservations__ExpiryScanSeconds=10` (só nesta corrida, para não esperar 15 minutos) e a Api com a mesma tolerância;
   - os pedidos de reservas do `sessions.http`, por ordem: criar, consultar, cancelar (200), cancelar de novo (409), criar outra que começa daqui a 1–2 minutos e esperar pelo *worker*;
   - na base: a reserva `Expired`, a auditoria com `actor = system`, um `ReservationExpired.v1` com `published_at = null`; nenhuma linha órfã;
   - `SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = 'smartparking'`, com a Api e os Workers a correr, mostra os logins da aplicação e nunca o dono `smartparking` (salvo a própria sessão do `psql`).
4. Roslyn: `get_project_graph` (dependências só para dentro), `get_di_registrations` (portas novas nos dois modos; `ICurrentActor` = `SystemCurrentActor` nos Workers e `AnonymousCurrentActor` na Api), `get_diagnostics` e `detect_antipatterns`.
