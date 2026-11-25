# Plano 002a — Utilizadores, sessões e pagamento (F3a)

- **Especificação:** [002-sessoes-pagamentos](../specs/002-sessoes-pagamentos.md) (aprovada a 2026-11-25), secção «Implementação em duas partes»
- **Estado:** Aprovado pelo Diogo (2026-11-25), tal como está, incluindo as decisões 1 a 4 e as propostas 5 a 12. Por implementar.
- **Requisitos:** UC-02 (estacionar e pagar); RF-02 (atomicidade das transições, falha a meio do pagamento, concorrência por `xmin`); RNF-06 (auditoria só por acréscimo, estendida às entidades alteradas)
- **Ramo:** `feat/002-sessoes-pagamentos`, a partir da `main` (`60238a0`). A F3b continua no mesmo ramo; o PR para a `main` só sai no fim da F3b, com o OK do Diogo.
- **Critérios desta parte:** AC-1 a AC-21. No AC-21, só as transições abrir, fechar, pagar e recusa.

## Contexto

A F2 deixou o agregado de sessão (`ParkingSession`, `Payment`) e o de utilizador (`User`, `Vehicle`, `PaymentMethod`) só com estrutura e tabelas. A unidade de trabalho (`SmartParkingDbContext`, que implementa `IUnitOfWork`) grava, por cada evento de domínio de uma `AggregateRoot`, uma `OutboxMessage` e **uma** `AuditEntry` da raiz que o levantou, tudo numa só chamada a `SaveChanges`.

A F3a precisa de duas coisas que este mecanismo ainda não dá:
- **uma auditoria por entidade alterada**, todas com o `outboxMessageId` do único evento da transição. Pagar altera o `Payment` e a sessão;
- **auditar entidades que não são raiz** (`Vehicle`, `PaymentMethod`, `Payment`), sem expor o `providerToken`.

O resto segue os padrões da F2:
- uma classe estática por operação, com `Command` ou `Query` e `Handler`;
- repositório por raiz (D10, ADR 0003);
- porta de leitura por consulta;
- `IEndpointGroup` por recurso, com `AllowAnonymous`;
- testes com Testcontainers e `DbProbe`;
- base isolada para o *trigger* de falha (AC-8 da F2).

## Decisões tomadas com o Diogo (2026-11-25)

1. **`FakePaymentGateway`** em `src/SmartParking.Infrastructure/Payments/`, uma pasta nova fora das quatro subcamadas, com `PaymentsRegistration`. O `.csproj` do Infrastructure ganha `InternalsVisibleTo(SmartParking.Api.Tests)`.
2. **`reservationId` na F3a:** o DTO já tem `reservationId?`. Quando vem preenchido, o *handler* devolve 409 `Session.ReservationNotSupported` sem gravar nada. A F3b substitui isto pelo cumprimento da reserva.
3. **Blocos por período:** a duração divide-se em períodos de 24 h a contar de `startedAt`. Cada período tem `ceil(minutos do período / incremento)` blocos, com ou sem `dailyMaximum`. O máximo, quando existe, limita cada período.
4. **Horário:** `[opensAt, closesAt)`, no fuso do parque. Às `closesAt` exatas o parque já está fechado.

## Propostas do plano, para aprovar com ele

Nenhuma muda a especificação nem as decisões D1–D10.

5. **Auditoria por entidade alterada.** Este é o ponto pedido; o detalhe está na secção própria, mais abaixo.
6. **`providerToken` fora da auditoria.** O `AuditSnapshot` passa a ignorar as propriedades de uma lista explícita, `AuditExclusions`, no Infrastructure. Por agora a lista tem só `PaymentMethod.ProviderToken`. O Domain não ganha atributos de auditoria. O AC-4 prova a exclusão.
7. **Pagamentos sem `Pending`.** O gateway é chamado antes da transação, por isso o `Payment` nasce já `Succeeded` ou `Failed` (`Payment.Succeeded(…)` e `Payment.Failed(…)`, *internal* ao Domain e chamados pela sessão). O valor `Pending` do enum fica, sem uso na F3.
8. **Eventos dos filhos do utilizador.** É o `User` que levanta `VehicleRegistered`, `VehicleRemoved`, `PaymentMethodAdded` e `PaymentMethodRemoved`, com o filho como entidade alterada. A linha `users` não é tocada (ver o ponto 5). Assim há uma só auditoria, a do veículo ou do meio (AC-3).
9. **412 → 409 nas transições.** Os comandos `/close` e `/pay` não levam `If-Match`. Um `xmin` desatualizado chega da unidade de trabalho como 412 (`ConcurrencyCheck.StaleVersion`), e o *handler* converte-o em 409 `Session.ConcurrentChange`, como o `CreateTariffVersion`.
10. **402 com `paymentId`.** O `Error` ganha `Extensions` (dicionário vazio por omissão), e `Error.PaymentDeclined(code, description, paymentId)` cria um `ErrorType.PaymentDeclined`. O `ResultExtensions.ToProblem` passa a mapear 402 e a copiar as extensões para o ProblemDetails. Os outros tipos de erro não mudam.
11. **Relógio dos testes.** O `FakeTimeProvider` é escrito à mão em `tests/Common/FakeTimeProvider.cs` (com `SetUtcNow` e `Advance`), para não juntar o pacote `Microsoft.Extensions.TimeProvider.Testing`. É registado só num *host* próprio das sessões. Os testes da coleção «Api» correm em série, por isso partilhar um relógio mutável não cria corridas.
12. **Teste dos ficheiros `.http`.** O `ConfigurationHttpFileTests` (AC-23 da F2) hoje exige que o `configuration.http` cubra **todas** as rotas `/api`. Com os *endpoints* novos, esse teste falharia. Passa a ser uma *theory* por ficheiro, com os prefixos de cada um:
    - `configuration.http`: `/api/facilities` e `/api/sensors`;
    - `sessions.http`: `/api/users` e `/api/sessions`. As rotas `/api/reservations` entram na F3b.

    Juntos, os ficheiros cobrem todas as rotas `/api`, sem pedidos para rotas que não existem.

## Auditoria por entidade alterada, sem mudar a F2

**Domain** (`Common/AggregateRoot.cs`):
- `Raise(IDomainEvent domainEvent, params ReadOnlySpan<object> changed)`. Com `changed` vazio, a entidade alterada é a própria raiz, que é exatamente o comportamento da F2. Todas as chamadas atuais continuam iguais.
- Os eventos ficam guardados como `RaisedEvent(IDomainEvent Event, IReadOnlyList<object> Changed)`, num tipo novo do Domain.
- `DomainEvents` e `DequeueDomainEvents()` mantêm a assinatura, porque os testes unitários da F2 usam-nas. Junta-se `DequeueRaisedEvents()`, que só a unidade de trabalho usa.

**Unidade de trabalho** (`AppendAuditAndOutbox`):
- **Uma `OutboxMessage` por evento.** Não muda.
- **Uma `AuditEntry` por entidade de `Changed`,** todas com o `outboxMessageId` dessa mensagem:
  - `EntityType` = nome do tipo CLR;
  - `EntityId` = chave primária, lida dos metadados do EF (as entidades que não são raiz não têm uma base comum);
  - `Operation`, `Before` e `After` vêm da entrada dessa entidade no *change tracker*.
- **Entidade alterada fora do *change tracker*:** `InvalidOperationException`, porque é um erro de programação (o repositório tem de a ter adicionado antes).
- **«Tocar» na raiz** (passar de `Unchanged` a `Modified`, para o `xmin` ser verificado) só acontece quando a raiz está entre as entidades alteradas. Na F2 está sempre, por isso nada muda. Na F3a, um `PaymentFailed` levantado pela sessão (alterada: só o pagamento) não toca na sessão, e o veículo registado não toca no utilizador.

**O que fica igual na F2:**
- o fecho da versão de tarifário anterior continua sem auditoria própria (ADR 0005), porque o `TariffVersionCreated` só declara a versão nova;
- todos os testes da F2 (201) têm de continuar a passar sem alterações, e isso funciona como teste de regressão;
- junta-se um teste unitário ao `AggregateRoot`: `Raise` sem entidades devolve a raiz.

**Uso na F3a:**

| Transição | Raiz que levanta | Evento | Entidades alteradas (auditorias) |
|---|---|---|---|
| Criar utilizador | `User` | `UserRegistered.v1` | utilizador (1) |
| Registar ou remover veículo | `User` | `VehicleRegistered.v1` ou `VehicleRemoved.v1` | veículo (1) |
| Adicionar ou remover meio | `User` | `PaymentMethodAdded.v1` ou `PaymentMethodRemoved.v1` | meio (1) |
| Abrir | `ParkingSession` | `SessionOpened.v1` | sessão (1); a F3b junta a reserva |
| Fechar | `ParkingSession` | `SessionClosed.v1` | sessão (1) |
| Pagar (aprovado ou 0,00 €) | `ParkingSession` | `SessionPaid.v1` | pagamento e sessão (2) |
| Recusa | `ParkingSession` | `PaymentFailed.v1` | pagamento (1) |

Fica registado no **ADR 0006 — Auditoria por entidade alterada** (RNF-06, RF-02; tese 5.4.2).

## FakePaymentGateway

- **Porta** (`Application/Abstractions/Payments/`):
  - `IPaymentGateway.ChargeAsync(PaymentCharge charge, CancellationToken ct) → Task<PaymentOutcome>`;
  - `PaymentCharge(PaymentIdempotencyKey Key, string ProviderToken, decimal Amount, string Currency)`;
  - `PaymentIdempotencyKey(Guid SessionId, Guid PaymentMethodId)`;
  - `PaymentOutcome` com os valores `Approved` e `Declined`.
- **Implementação** (`Infrastructure/Payments/FakePaymentGateway.cs`, `internal sealed`):
  - `ConcurrentDictionary<PaymentIdempotencyKey, Lazy<PaymentOutcome>>`. O `GetOrAdd` com `Lazy` garante uma só cobrança por chave, mesmo com duas chamadas em simultâneo (AC-16);
  - recusa quando o token começa por `decline_` (comparação ordinal) e aprova os outros;
  - `ChargeCount(PaymentIdempotencyKey)` conta as cobranças efetivas por chave;
  - nunca regista o token em *logs*.
- **Registo:** `Infrastructure/Payments/PaymentsRegistration.AddPayments()`, chamado em `DependencyInjection` nos **dois** modos, como *singleton*. O estado perde-se ao reiniciar, como a especificação aceita.
- **Como os testes o observam:** `factory.Services.GetRequiredService<IPaymentGateway>()` convertido em `FakePaymentGateway` (visível pelo `InternalsVisibleTo`), e depois `ChargeCount(new(sessionId, methodId))`. A expressão «gateway não chamado» corresponde a `ChargeCount == 0` (AC-17, AC-18). Cada *host* tem o seu *singleton*, por isso as contagens não se misturam entre fábricas.
- **Arquitetura:** o `SublayerIsolationTests` ganha um caso: `Infrastructure.Payments` não usa EF Core, MongoDB, Redis nem as outras subcamadas.

## Escritas concorrentes sobre o mesmo `xmin` (AC-11, AC-16)

Disparar dois pedidos em paralelo não chega: o segundo pode ler depois do *commit* do primeiro e receber 409 pelo estado, e nesse caso não se testa o `xmin`. Para o teste ser determinístico:

- **`tests/SmartParking.Api.Tests/Fixtures/ConcurrencyGate.cs`:** uma barreira assíncrona que se arma com `Arm(participants: 2)`. Desarmada, deixa passar tudo, e assim os pedidos de preparação do teste não ficam presos.
- **`GatedUnitOfWork(IUnitOfWork inner, ConcurrencyGate gate)`:** um decorador que espera na barreira e só depois chama `inner.SaveChangesAsync`. É registado só num *host* próprio: o teste envolve a fábrica do descritor `IUnitOfWork` já registado. Não precisa de ver o `SmartParkingDbContext`, que é *internal*.
- **Sequência:**
  1. os dois pedidos leem a sessão, que está `Open` (ou `Closed`) com o mesmo `xmin`, e, no `/pay`, chamam o gateway com a mesma chave;
  2. os dois chegam à barreira e os dois gravam;
  3. o `UPDATE … WHERE xmin = @lido` do segundo espera pelo bloqueio da linha e depois afeta 0 linhas. O `DbUpdateConcurrencyException` dá 412, que o *handler* converte em 409. No `/pay`, o índice `ux_payments_session_succeeded` (`23505`) também dá 409, e a especificação aceita os dois.
- **Tempo limite:** a barreira usa o `CancellationToken` do teste com `CancelAfter(30 s)`, para um erro não pendurar o `dotnet test`.
- O mesmo mecanismo serve um teste extra: duas aberturas simultâneas do mesmo veículo, apanhadas pelo índice parcial novo.

## Consultas compiladas

As duas usam `EF.CompileAsyncQuery` e `AsNoTracking`, porque os *handlers* só as leem para verificar pré-condições:

1. **Sessão aberta de um veículo:** `IParkingSessionRepository.GetOpenByVehicleAsync(vehicleId, ct)`, com `FirstOrDefault(s => s.VehicleId == id && s.Status == SessionStatus.Open)`. Serve-se do índice parcial novo.
2. **Tarifário em vigor:** `ITariffRepository.GetInForceAsync(facilityId, at, ct)`, com `FirstOrDefault(t => t.FacilityId == f && t.ValidFrom <= at && (t.ValidTo == null || t.ValidTo > at))`, o mesmo predicado do `TariffReader.GetCurrentAsync`. Antes de mudar a interface: `find_references` em `ITariffRepository`.

O fecho e o pagamento carregam a sessão com o `GetByIdAsync` compilado (seguido, para o `xmin`) e a versão de tarifário da sessão com o `TariffRepository.GetByIdAsync`, que já é compilado.

## Migração aditiva (AC-1)

- **Nome:** `OpenSessionPerVehicle` (ficheiro `<timestamp>_OpenSessionPerVehicle.cs`, gerado com `dotnet ef migrations add`).
- **Configuração** (`ParkingSessionConfiguration`): `HasIndex(s => s.VehicleId, "ux_parking_sessions_vehicle_open").IsUnique().HasFilter("status = 'Open'")`, como o `ux_payments_session_succeeded`.
- **Risco:** um índice com nome sobre `vehicle_id` pode levar a convenção das FK a abandonar o índice `ix_parking_sessions_vehicle_id` da `Initial`, e a migração ficaria com um `DropIndex`. Para o evitar, declara-se também o índice da FK de forma explícita (`HasIndex(s => s.VehicleId)`, sem nome). Revê-se o `Up` gerado e o `migrations script`: o `Up` só pode ter um `CreateIndex`.
- **Testes:**
  - `UnitTests/Infrastructure/OpenSessionPerVehicleMigrationTests.Up_OnlyCreatesIndex_AC1`: todas as `UpOperations` são `CreateIndexOperation`, com o nome, a tabela, `IsUnique` e o filtro esperados. Prova que nada da `Initial` é removido ou alterado;
  - `Api.Tests/Features/Schema/OpenSessionPerVehicleIndexTests`:
    - `Migrate_CreatesPartialUniqueIndex_AC1` lê o `pg_indexes`;
    - `TwoOpenSessionsSameVehicle_AsAppUser_Fail23505_AC1` usa o *seed* do `SessionConstraintsTests`.
  - Uma sessão `Closed` e outra `Open` do mesmo veículo são aceites.

## (a) UC, RF, RNF e AC

- **UC-02:** abrir, fechar e pagar pela API; registo do condutor, dos veículos e dos meios de pagamento.
- **RF-02:**
  - atomicidade de cada transição (uma chamada a `SaveChanges` com o negócio, a auditoria e uma `OutboxMessage`): AC-5, AC-9, AC-12, AC-13, AC-21;
  - falha a meio do pagamento: AC-14;
  - concorrência por `xmin`: AC-11, AC-16;
  - ciclo que não volta atrás: AC-15.
- **RNF-06:** a auditoria só por acréscimo, agora por entidade alterada (AC-12), e sem o `providerToken` (AC-4).
- **RNF-01:** sem alvo nesta fase; o UC-02 mede-se na F12. Não se escreve nenhum valor de desempenho.
- **AC:** AC-1 a AC-20 por inteiro; AC-21 nas transições abrir, fechar, pagar e recusa.

## (b) Ficheiros a criar ou alterar

Nenhum projeto novo, nenhum pacote novo. Uma migração.

**Domain**
- `Common/AggregateRoot.cs` e `Common/RaisedEvent.cs` (novo).
- `Users/User.cs`:
  - `Register(id, name, email)` levanta `UserRegistered`;
  - `RegisterVehicle(id, plate)` devolve um `Vehicle` e levanta `VehicleRegistered`;
  - `RemoveVehicle(vehicle)`, `AddPaymentMethod(…)` e `RemovePaymentMethod(method)`, cada um com o seu evento.
  O `User.Create` atual passa a ser usado só pelo `Register`.
- `Users/Events/`: `UserRegistered` (só `id`), `VehicleRegistered` e `VehicleRemoved` (`id`, `userId`, `licensePlate`), `PaymentMethodAdded` e `PaymentMethodRemoved` (`id`, `userId`, `type`, `maskedNumber`, `expiresOn`; nunca o token).
- `Facilities/ParkingFacility.cs`: `IsOpenAt(DateTimeOffset instant)`, que converte o instante para o fuso IANA do parque e verifica `[opensAt, closesAt)` no dia local. Um dia sem linha conta como fechado. A F3b vai reutilizar este método nas reservas.
- `Sessions/SessionPricing.cs` (novo): a função pura `Calculate(Tariff tariff, TimeSpan duration)`:
  - limiar de minutos grátis;
  - períodos de 24 h com `ceil` por período (decisão 3);
  - o valor do período é `blocos × incremento × preço / 60`, calculado em `decimal` e limitado ao `dailyMaximum`;
  - soma dos períodos e um só `Math.Round(…, 2, MidpointRounding.AwayFromZero)` no fim.
- `Sessions/ParkingSession.cs`:
  - `Open(…)`, que substitui o `Start`. Normaliza o `startedAt` com `Timestamps.Normalize` e levanta `SessionOpened`;
  - `Close(Tariff tariff, DateTimeOffset now)`: só a partir de `Open` e com `tariff.Id == TariffId`. Calcula `Amount` e levanta `SessionClosed`;
  - `RecordPayment(Guid paymentId, Guid methodId, bool approved, DateTimeOffset now) → Payment`: só a partir de `Closed`. Quando é aprovado, a sessão passa a `Paid` e levanta `SessionPaid(…, changed: payment, this)`. Quando é recusado, levanta `PaymentFailed(…, changed: payment)`.
  Não há nenhum método que saia de `Paid`.
- `Sessions/Payment.cs`: as fábricas `Succeeded` e `Failed` (*internal*), em vez do `Create`.
- `Sessions/Events/`: `SessionOpened` (com `reservationId?`), `SessionClosed` (com `endedAt` e `amount`), `SessionPaid` (com `paymentId` e `amount`), `PaymentFailed` (com `paymentId`, `sessionId` e `amount`). Todos são `sealed record` com `[DomainEvent(nome, 1)]` e levam `spotId` e `vehicleId`, para os projetores da F5.

**Application**
- `Abstractions/ErrorType.cs` (`PaymentDeclined`) e `Abstractions/Error.cs` (`Extensions` e `PaymentDeclined(…)`).
- `Abstractions/Payments/`: `IPaymentGateway`, `PaymentCharge`, `PaymentIdempotencyKey`, `PaymentOutcome`.
- `Abstractions/Persistence/`:
  - `IUserRepository`: `GetByIdAsync`, `Add`, `GetVehicleAsync(userId, vehicleId)`, `FindVehicleAsync(vehicleId)`, `AddVehicle`, `RemoveVehicle`, `GetPaymentMethodAsync(userId, id)`, `FindPaymentMethodAsync(id)`, `AddPaymentMethod`, `RemovePaymentMethod`;
  - `IParkingSessionRepository`: `GetByIdAsync`, `GetOpenByVehicleAsync`, `Add`, `AddPayment`;
  - `ITariffRepository`: mais `GetInForceAsync`.
- `Abstractions/Reading/`:
  - `IUserReader`: `GetAsync`, `ListVehiclesAsync` e `ListPaymentMethodsAsync`. As listas devolvem `null` quando o utilizador não existe (404);
  - `ISessionReader.GetAsync`;
  - as *views* `UserView`, `VehicleView`, `PaymentMethodView` (sem o token) e `SessionView`.
- `Features/Users/`: `RegisterUser`, `GetUser`, `RegisterVehicle`, `ListVehicles`, `RemoveVehicle`, `AddPaymentMethod`, `ListPaymentMethods`, `RemovePaymentMethod`, `UserErrors`.
- `Features/Sessions/`: `OpenSession`, `CloseSession`, `PaySession`, `GetSession`, `SessionErrors`.
  - O `PaySession` segue a ordem da especificação:
    1. pré-condições, sem gateway (404; 409 se a sessão não estiver `Closed`; 409 se o meio for de outro utilizador);
    2. gateway, salvo quando o valor é 0,00 €;
    3. `RecordPayment` e `AddPayment`;
    4. uma gravação;
    5. 412 → 409. Uma recusa gravada devolve `Error.PaymentDeclined` com o `paymentId`.
  - A ordem das pré-condições do `OpenSession` é: 404 para o lugar ou o veículo; depois 409 para a reserva (decisão 2), o lugar ou o parque inativos, o parque fechado, a falta de tarifário em vigor e a sessão já aberta.

**Infrastructure**
- `Relational/SmartParkingDbContext.cs` (auditoria por entidade alterada) e `Relational/Auditing/AuditSnapshot.cs` mais `AuditExclusions.cs` (novo).
- `Relational/Configurations/Sessions/ParkingSessionConfiguration.cs` e `Relational/Migrations/<ts>_OpenSessionPerVehicle.cs` mais o *snapshot*.
- `Relational/Repositories/`: `UserRepository`, `ParkingSessionRepository` (com a consulta compilada 1) e o `TariffRepository` (com a consulta compilada 2).
- `Relational/Reading/`: `UserReader`, `SessionReader`, com `AsNoTracking` e projeções.
- `Relational/RelationalRegistration.cs`: quatro portas novas, iguais nos dois modos.
- `Payments/FakePaymentGateway.cs`, `Payments/PaymentsRegistration.cs` e `DependencyInjection.cs` (chamar `AddPayments()` nos dois modos).
- `SmartParking.Infrastructure.csproj`: `InternalsVisibleTo(SmartParking.Api.Tests)`.

**Api**
- `Common/ResultExtensions.cs`: o 402 e as extensões.
- `Features/Users/`: `UserEndpoints` (`/api/users`), DTO e validadores:
  - email: formato, até 254 caracteres;
  - matrícula: só letras, dígitos, espaços e hífenes, até 16 caracteres depois de normalizada (a mesma regra do Domain);
  - `type` com os nomes exatos do enum (`EnumRules`);
  - `providerToken` e `maskedNumber` com os limites do Domain;
  - paginação com `PageRequest`.
- `Features/Sessions/`: `SessionEndpoints` (`/api/sessions`, `/{id}/close`, `/{id}/pay`) e os DTO `OpenSessionRequest`, `PaySessionRequest`, `SessionResponse` (o `amount` é nulo quando a sessão está `Open`) e `PaymentResponse` (a sessão e o `paymentId`), mais os validadores. O OpenAPI declara o 402.

**Testes**
- `tests/Common/FakeTimeProvider.cs` (decisão 11).
- `tests/SmartParking.Api.Tests/Fixtures/`:
  - `ApiFixture`: as propriedades `Clock` e `Sessions` (um *host* com o relógio falso), e a sobrecarga `CreateFactory(IsolatedDatabase, Action<IServiceCollection>?)`;
  - `ConcurrencyGate` e `GatedUnitOfWork`;
  - `UserApi` e `SessionApi`. O `SessionApi.ArrangeAsync` cria pela API um parque (Europe/Lisbon, das 08:00 às 20:00 em todos os dias), uma zona, um lugar, o tarifário de referência com `validFrom = agora`, um utilizador, um veículo e um meio;
  - `PaymentGatewayProbe` (`ChargeCount`).
- `tests/SmartParking.Api.Tests/Features/Users/` e `Features/Sessions/`: ver (e).
- `tests/SmartParking.Api.Tests/Features/Configuration/ConfigurationHttpFileTests.cs` passa a `Features/HttpFiles/HttpFileTests.cs` (decisão 12).
- `tests/SmartParking.UnitTests/Domain/`: `SessionPricingTests` (AC-8), `ParkingSessionTests` (AC-15 e as transições), `UserTests`, `OpeningHoursTests` e `AggregateRootTests`.
- `tests/SmartParking.UnitTests/Application/ErrorTests` (402 e as extensões) e `UnitTests/Infrastructure/OpenSessionPerVehicleMigrationTests`.
- `tests/SmartParking.ArchitectureTests/Features/SublayerIsolationTests.cs`: o caso `Payments`.
- `tests/http/sessions.http`: um pedido por *endpoint* de utilizadores e de sessões, encadeados.

**Documentação**
- `docs/plans/002a-utilizadores-sessoes-pagamento.md` (este plano) e `docs/plans/README.md`.
- `docs/adr/0006-auditoria-por-entidade-alterada.md` e o índice dos ADR.
- `docs/specs/README.md` (estado da 002), `docs/RASTREABILIDADE.md` e `docs/DIARIO.md`.

## (c) Skills do kit e adaptações

| Skill | Uso | Adaptação à especificação |
|---|---|---|
| `modern-csharp` | *records*, `sealed`, *primary constructors*, `params ReadOnlySpan` | Eventos `sealed record` com `[DomainEvent]` |
| `clean-architecture` | Portas em `Application/Abstractions`; Domain sem dependências | O cálculo do valor e o horário ficam no Domain; o `now` vem do `TimeProvider` do *handler*. O gateway é uma porta com o adaptador em `Infrastructure/Payments` |
| `ef-core` | Repositórios, leitores, consultas compiladas, migração | Exceção D10 (repositório por raiz). Os filhos (`Vehicle`, `PaymentMethod`, `Payment`) entram pelo repositório da raiz. O índice tem nome e o índice da FK é declarado de forma explícita, para a migração ficar aditiva |
| `error-handling` | Result e ProblemDetails | `ErrorType.PaymentDeclined` (402) com a extensão `paymentId`; 412 → 409 nas transições sem `If-Match` |
| `minimal-api`, `openapi`, `scalar` | Um `IEndpointGroup` por recurso, `TypedResults` | Sem ETag nem `If-Match` nas sessões (especificação, «Concorrência sem `If-Match`»). `AllowAnonymous` no grupo |
| `dependency-injection` | Registo das portas | Gateway como *singleton* nos dois modos; o `TimeProvider` e o `IUnitOfWork` decorados só nos *hosts* de teste |
| `testing` | Testcontainers, AAA, `…_ACn` | Relógio escrito à mão (sem pacote novo); a barreira na unidade de trabalho substitui *mocks*; base isolada só no AC-14 |
| `serilog` | Sem dados pessoais nos *logs* | O nome, o email, a matrícula e o token nunca aparecem em *logs* `Information` ou abaixo |
| `messaging` | *Outbox* | Só a escrita, com `published_at = null`; a publicação é da F6 |

## (d) Decisões D1–D10

- **D1** (por omissão): sessões, pagamentos e utilizadores em PostgreSQL.
- **D2** (por omissão): uma `OutboxMessage` por transição, na mesma transação; o leitor da *outbox* fica para a F6.
- **D10** (ADR 0003): dois repositórios novos (`IUserRepository`, `IParkingSessionRepository`), `GetInForceAsync` no `ITariffRepository` e duas portas de leitura. Nenhum repositório genérico nem `IQueryable`. O AC-21 e o AC-22 da F2 continuam a verificar isto.
- Nenhuma outra decisão é tocada nem alterada.

## (e) Correspondência AC → passo → teste

Os testes de integração correm no *host* `Sessions` (modo `Hybrid`, utilizador da aplicação, `FakeTimeProvider`), salvo indicação em contrário.

| AC | Passo | Teste |
|---|---|---|
| AC-1 | 3 | `UnitTests/Infrastructure/OpenSessionPerVehicleMigrationTests.Up_OnlyCreatesIndex_AC1`; `Api.Tests/Features/Schema/OpenSessionPerVehicleIndexTests.Migrate_CreatesPartialUniqueIndex_AC1`, `TwoOpenSessionsSameVehicle_AsAppUser_Fail23505_AC1` |
| AC-2 | 9 | `Users/RegisterUserTests.Register_Valid_Returns201LowerCaseEmailOneAuditOneEvent_AC2`, `Register_EmailDifferentCase_Returns409_AC2` |
| AC-3 | 9 | `Users/VehicleTests.Register_PlateWithSpacesAndHyphen_NormalisesAndReturns201WithAuditAndEvent_AC3`, `Register_DuplicatePlate_Returns409_AC3`, `Delete_VehicleWithSession_Returns409AndWritesNothing_AC3` |
| AC-4 | 9 | `Users/PaymentMethodTests.Add_ProviderToken_NotInListAuditOrOutbox_AC4` (token aleatório; procura-o no corpo da lista, em `before::text`, em `after::text` e em `payload::text`) |
| AC-5 | 10 | `Sessions/OpenSessionTests.Open_ValidSpot_Returns201OpenWithCurrentTariffOneAuditOneEvent_AC5` |
| AC-6 | 10 | `OpenSessionTests.Open_PreconditionFails_Returns409AndWritesNothing_AC6` (*theory*: sem tarifário, lugar `Inactive`, parque `Inactive`, fora do horário, dia sem horário). Extra: `Open_AtClosingTime_Returns409` (decisão 4) e `Open_WithReservationId_Returns409UntilF3b` (decisão 2) |
| AC-7 | 10 | `OpenSessionTests.Open_VehicleAlreadyOpen_Returns409_AC7` (*theory*: mesmo lugar, outro lugar). Extra: `Open_ConcurrentSameVehicle_OneReturns409` (barreira) |
| AC-8 | 5 | `UnitTests/Domain/SessionPricingTests.Calculate_ReferenceTariff_ReturnsExpected_AC8` (*theory* em tabela: 10 min → 0,00; 15 → 0,00; 40 → 0,90; 31 h → 18,40; meio a subir: 0,30 €/h, incremento de 1 min, sem minutos grátis, 1 min = 0,005 → 0,01; sem `dailyMaximum`: 31 h → 37,20). Extra: um incremento de 7 min a atravessar as 24 h (decisão 3) |
| AC-9 | 11 | `Sessions/CloseSessionTests.Close_After40Minutes_Returns200Amount090OneAuditOneEvent_AC9`, `Close_AlreadyClosed_Returns409_AC9` |
| AC-10 | 11 | `CloseSessionTests.Close_NewVersionDuringSession_UsesSessionVersion_AC10` (a V2 é criada pela API com `validFrom = agora + 1 h`; depois o relógio avança 2 h) |
| AC-11 | 11 | `Sessions/SessionConcurrencyTests.Close_TwoConcurrentOnSameXmin_One200One409SingleEvent_AC11` (barreira) |
| AC-12 | 12 | `Sessions/PaySessionTests.Pay_ClosedSession_Returns200PaidOneEventTwoAuditsSameXmin_AC12` (o `xmin::text` da sessão, do pagamento, das duas auditorias e da mensagem é igual; as auditorias têm o mesmo `outbox_message_id`) |
| AC-13 | 12 | `PaySessionTests.Pay_DeclinedToken_Returns402WithPaymentIdAndStaysClosed_AC13`, `Pay_AfterDecline_OtherMethod_Returns200_AC13` |
| AC-14 | 12 | `Sessions/PaymentAtomicityTests.Pay_OutboxInsertFails_Returns500AndWritesNothing_AC14`, numa base isolada com o *trigger* do AC-8 da F2. Depois de o `DROP TRIGGER`, repetir dá 200 e `ChargeCount == 1` |
| AC-15 | 5, 12 | `UnitTests/Domain/ParkingSessionTests.PaidSession_CloseOrPayThrows_AC15` e `ParkingSession_NoMethodLeavesPaid_AC15` (por reflexão: os únicos métodos públicos que mudam o estado são `Close` e `RecordPayment`); `PaySessionTests.PaidSession_CloseOrPayAgain_Returns409NoNewEvents_AC15` |
| AC-16 | 12 | `SessionConcurrencyTests.Pay_TwoConcurrentSameMethod_One200One409OneSucceededOneCharge_AC16` (barreira) |
| AC-17 | 12 | `PaySessionTests.Pay_PreconditionFails_Returns409NoChargeNoWrite_AC17` (*theory*: sessão `Open`, meio de outro utilizador) |
| AC-18 | 12 | `PaySessionTests.Pay_ZeroAmount_SucceedsWithoutGateway_AC18` |
| AC-19 | 10, 11 | `Sessions/GetSessionTests.Get_OpenThenClosed_AmountNullThenValue_AC19`, `Get_UnknownId_Returns404_AC19` |
| AC-20 | 13 | `HttpFiles/HttpFileTests.HttpFile_CoversItsEndpoints_AC20` (*theory*: `sessions.http` com AC-20 e `configuration.http` com AC-23) e `HttpFiles_TogetherCoverEveryApiRoute` |
| AC-21 | 13 | `Sessions/TransitionOutboxTests.Transition_WritesExactlyOneOutboxMessage_AC21` (*theory*: abrir → `SessionOpened.v1`, fechar → `SessionClosed.v1`, pagar → `SessionPaid.v1`, recusa → `PaymentFailed.v1`). Conta as `outbox_messages` com o `xmin` da linha alterada (a sessão, ou o pagamento na recusa): exatamente 1, do tipo certo. As transições de reserva entram na F3b |

Testes sem AC:
- `AggregateRootTests` (o `Raise` sem entidades corresponde à raiz, como na F2);
- `ErrorTests` (402);
- `OpeningHoursTests` (`[opensAt, closesAt)`, dia sem linha, horário de verão);
- `UserTests`;
- o caso `Payments` em `SublayerIsolationTests`;
- os 404 dos utilizadores e dos meios.

O AC-21 e o AC-22 da F2 voltam a correr sem alterações e passam a cobrir os eventos e as portas novas.

## Passos

1. **Preparação:**
   - ramo `feat/002-sessoes-pagamentos`;
   - este plano em `docs/plans/002a-…md` e no índice;
   - a especificação 002 passa a «Em implementação (F3a)»;
   - a linha RF-02 da RASTREABILIDADE passa a «F3a em implementação».
2. **Auditoria por entidade alterada:** `AggregateRoot`, `RaisedEvent`, a unidade de trabalho e `AuditExclusions`. Testes `AggregateRootTests`, mais os 201 testes da F2 a passar sem alterações. *Commit* `refactor(audit)`.
3. **Migração `OpenSessionPerVehicle`:** configuração, `migrations add`, revisão do `Up` e do `migrations script`. Testes AC-1.
4. **Domain dos utilizadores:** `User` e os cinco eventos. `UserTests`.
5. **Domain das sessões:** `SessionPricing`, `ParkingFacility.IsOpenAt`, `ParkingSession` (`Open`, `Close`, `RecordPayment`), as fábricas de `Payment` e os quatro eventos. Testes AC-8, AC-15 (Domain) e `OpeningHoursTests`.
6. **Fundação da Application:** `Error.PaymentDeclined`, `Extensions`, o 402 no `ResultExtensions`, `IPaymentGateway`, as portas novas, `GetInForceAsync` e as *views*. `ErrorTests`.
7. **Infrastructure:**
   - `UserRepository`, `ParkingSessionRepository` e as duas consultas compiladas;
   - `UserReader` e `SessionReader`;
   - o registo;
   - `FakePaymentGateway` e `PaymentsRegistration`, o `InternalsVisibleTo` e o caso `Payments` na arquitetura;
   - `get_di_registrations` nos dois modos.
8. ***Fixtures* de teste:** `FakeTimeProvider`, o *host* `Sessions`, `ConcurrencyGate` e `GatedUnitOfWork`, `UserApi`, `SessionApi`, `PaymentGatewayProbe`.
9. **Utilizadores:** as operações, `UserEndpoints`, os DTO e os validadores. Testes AC-2, AC-3 e AC-4.
10. **Abrir e consultar:** `OpenSession`, `GetSession` e as rotas. Testes AC-5, AC-6, AC-7, AC-19 (aberta e 404) e os extras.
11. **Fechar:** `CloseSession`. Testes AC-9, AC-10, AC-11 e AC-19 (fechada).
12. **Pagar:** `PaySession`. Testes AC-12, AC-13, AC-15 (API), AC-16, AC-17 e AC-18; AC-14 numa base isolada.
13. **`sessions.http` e testes transversais:**
    - `sessions.http`;
    - a generalização do teste dos ficheiros `.http` (AC-20; o AC-23 da F2 continua);
    - o AC-21 em tabela;
    - verificação manual contra o `compose.yaml` (ver abaixo).
14. **Verificação:**
    - `/verify`: build com `-warnaserror`, format, `dotnet test`, `get_diagnostics`, `detect_antipatterns`, `get_project_graph`;
    - testes de mutação, com cópia de segurança dos ficheiros não versionados antes de começar:
      - sem a entidade de pagamento em `SessionPaid`, o AC-12 falha;
      - sem a conversão 412 → 409, o AC-11 falha;
      - sem o `Lazy` no gateway, o AC-16 falha;
    - revisão independente (`code-reviewer`, só leitura); os achados tratam-se com o acordo do Diogo.
15. **Documentação e fecho:**
    - ADR 0006 e o índice;
    - RASTREABILIDADE, DIARIO, estado do plano e da especificação;
    - *commits* atómicos ao longo dos passos (`refactor(audit)`, `feat(schema)`, `feat(users)`, `feat(sessions)`, `feat(payments)`, `test…`, `docs(adr)…`, todos com «(spec 002)»);
    - `/checkpoint`. A F3b começa noutra sessão.

São 15 passos, dentro do limite, sem nova divisão.

## Linha a acrescentar em `docs/RASTREABILIDADE.md`

| RF-02 | UC-02 | AC-1 a AC-21 (F3a); AC-22 a AC-30 e as transições de reserva no AC-21 (F3b) | 002-sessoes-pagamentos (F3a); plano [002a](plans/002a-utilizadores-sessoes-pagamento.md); [ADR 0006](adr/0006-auditoria-por-entidade-alterada.md) | `Domain/{Users,Sessions}`, `Application/Features/{Users,Sessions}`, `Infrastructure/Relational` (unidade de trabalho, migração `OpenSessionPerVehicle`, repositórios e leitores), `Infrastructure/Payments`, `Api/Features/{Users,Sessions}`, `tests/http/sessions.http` | `Api.Tests/Features/{Users,Sessions}`, `Schema/OpenSessionPerVehicleIndexTests`, `HttpFiles/HttpFileTests`; `UnitTests/Domain/{SessionPricingTests,ParkingSessionTests}` | F3a a passar (<data>); F3b por fazer |

Na linha RNF-06, a coluna de teste ganha `PaySessionTests` (AC-12: uma auditoria por entidade alterada), `PaymentMethodTests` (AC-4) e `PaymentAtomicityTests` (AC-14).

## Entrada a acrescentar em `docs/DIARIO.md`

```
## F3a: utilizadores, sessões e pagamento (especificação 002)

- **Data:** <início> a <fim>
- **Feito:**
  - Plano aprovado pelo Diogo (docs/plans/002a-utilizadores-sessoes-pagamento.md), com as decisões 1 a 4 (2026-11-25) e as propostas 5 a 12.
  - Auditoria por entidade alterada na unidade de trabalho (ADR 0006), sem alterar o comportamento da F2.
  - Migração aditiva OpenSessionPerVehicle (ux_parking_sessions_vehicle_open).
  - Utilizadores, veículos e meios de pagamento; abrir, fechar e pagar sessões; FakePaymentGateway.
  - Testes: AC-1 a AC-21 (F3a); <n> testes a passar (201 antes da F3a). Preencher com o resultado real.
  - Verificação manual contra o compose.yaml: <preencher>.
- **Problemas:** <preencher durante a implementação, incluindo as abordagens que falharam e se foi preciso voltar a planear>
- **Decisões:** decisões 1 a 4 e propostas 5 a 12 do plano 002a; <desvios, com a razão>
- **Commits:** <hashes>
```

## Riscos e pontos de atenção

- **Migração não aditiva por convenção:** é o risco do índice da FK descrito acima. Se o `Up` gerado tiver mais do que um `CreateIndex`, para-se. Se não houver forma aditiva, a especificação volta a «In Review».
- **Regressão da F2 na unidade de trabalho:** o passo 2 só fecha com os 201 testes da F2 a passar sem alterações. Se falhar duas vezes, para-se e volta-se a planear.
- **Ordem dos comandos no pagamento concorrente:** o EF Core faz o `UPDATE` da sessão antes do `INSERT` do pagamento (dependência pela FK). Se a ordem for outra, o 409 vem do `23505` em vez do `xmin`, e a especificação aceita os dois. O teste verifica o resultado (um 200, um 409, um `Succeeded`, uma cobrança), não o caminho.
- **Fusos e horário de verão:** `IsOpenAt` usa o `TimeZoneInfo` IANA. Os testes da API usam dezembro em Europe/Lisbon (UTC+0); os testes unitários cobrem uma data de verão.
- **Relógio partilhado:** só o *host* `Sessions` o usa, e a coleção «Api» corre em série. Cada teste fixa o seu instante.
- **Dados pessoais:** os *handlers* não registam o nome, o email, a matrícula nem o token. A auditoria guarda o email e a matrícula (é o seu papel), mas nunca o token (AC-4).

## Verificação ponta a ponta

1. `dotnet build SmartParking.slnx -warnaserror` e `dotnet format --verify-no-changes`.
2. `dotnet test 2>&1 | bash hooks/post-test-analyze.sh` (com o Docker ligado): os 201 testes da F1 e da F2 e os `…_ACn` da tabela acima.
3. Local:
   - `docker compose up -d --wait`;
   - `dotnet ef database update` como dono, para aplicar a `OpenSessionPerVehicle`;
   - a Api ligada como `smartparking_api`;
   - todos os pedidos do `sessions.http`, por ordem;
   - na base, as auditorias `anonymous` e uma mensagem por transição, com `published_at = null`;
   - pagar com um meio `decline_…` dá 402.
4. Roslyn: `get_project_graph` (dependências só para dentro), `get_di_registrations` (portas e gateway nos dois modos), `get_diagnostics` e `detect_antipatterns`.
