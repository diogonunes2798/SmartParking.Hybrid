# Plano 001a — Domínio, esquema e parques (F2a)

- **Especificação:** [001-configuracao](../specs/001-configuracao.md) (aprovada a 2026-11-24), secção «Implementação em duas partes»
- **Estado:** Implementado no ramo `feat/001-configuracao` (2026-11-25): 15 passos, 131 testes a passar, `/verify` e revisão independente feitos (ver [diário](../DIARIO.md), F2a). Integrado na `main` com a F2b ([PR #2](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/2), *merge commit* `f123c86`, 2026-11-25).
- **Desvios aprovados durante a implementação:** `CHECK` de latitude e longitude retirados (o `geography` converte as coordenadas fora do intervalo); validadores na Api e não em Application; sem `SetExpectedVersion`; sem `ConnectionStrings:PostgresMigrations` no `appsettings.json`. Pormenores e razões no diário.
- **Requisitos:** RF-06 (parques); RNF-06 (auditoria só por acréscimo); preparação de RF-04, UC-02 e UC-05
- **Ramo:** `feat/001-configuracao` (F2a e F2b no mesmo ramo, um PR no fim da F2b; se o Diogo preferir, um PR por parte)

## Contexto

O Domain está vazio e não existe nenhuma tabela de negócio. As fases F3 a F9 precisam do modelo relacional comum, e a tese (5.4.1, 5.6.2) defende que cada alteração deixa, na mesma transação, uma auditoria imutável e um evento na *outbox*. A F2a cria todo o Domain e a migração `Initial` (com PostGIS, `btree_gist`, FK compostas, `EXCLUDE`, `CHECK` e o operador de referência), o papel `smartparking_app`, a auditoria e a *outbox* atómicas, a porta `ICurrentActor`, os erros 412 e 428 e os *endpoints* de parques com ETag/If-Match. A F2b reutiliza este esquema sem migrações novas.

## Decisões do Diogo neste planeamento (2026-11-24)

- **Pacotes:** autorizados só os três da stack do `CLAUDE.md`: `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` (Infrastructure) e `Microsoft.EntityFrameworkCore.Design` (Api, `PrivateAssets=all`), na linha 10.x, com `dotnet add package`. O «Out» da spec lê-se como «fora da stack». O `snake_case` faz-se com uma convenção escrita à mão, sem `EFCore.NamingConventions`. Fica registado no diário.
- **AC-17 e AC-18:** a F2a cobre os casos de parques (latitude, fuso, horário; paginação de `GET /facilities` e 404). A F2b acrescenta aos mesmos testes os casos de tarifário e de lugares.
- **Operador inexistente** no `POST /facilities`: 409 pela FK (`23503` → `Conflict`), sem porta nova.

## (a) UC, RF e RNF da fase

- **RF-06** (configuração), na parte dos parques; **UC-02** e **UC-05** só pela estrutura: as tabelas de tarifário, sessão, reserva e pagamento e as de sensor existem e têm as restrições, mas o comportamento é da F2b/F3.
- **RNF-06**, só na parte da auditoria só por acréscimo garantida pela base de dados (AC-10). A cifra e o RBAC ficam para a F11.
- **RF-04** (preparação): `geography(Point,4326)` com índice GiST (AC-2). A pesquisa é da F5.
- Decisões D1 (sessões em PostgreSQL) e D2 (*outbox*), no valor por omissão.

## (b) Ficheiros e projetos

Nenhum projeto novo. A F2a acrescenta pastas e ficheiros aos projetos existentes.

**Raiz e configuração**
- `Directory.Packages.props`, `src/SmartParking.Infrastructure/*.csproj`, `src/SmartParking.Api/*.csproj`: os três pacotes; `InternalsVisibleTo` de Infrastructure para os projetos de teste, se for preciso.
- `.env.example`: `POSTGRES_APP_PASSWORD`.
- `src/SmartParking.Api/appsettings.json`: `ConnectionStrings:Postgres` passa a `Username=smartparking_api` (*login* membro de `smartparking_app`, sem palavra-passe no ficheiro); nova `ConnectionStrings:PostgresMigrations` com `Username=smartparking` (dono). As palavras-passe chegam por variáveis de ambiente ou `user-secrets`, como na F1. O `appsettings.json` dos Workers não muda: fica como ponto a resolver na F3/F4.
- `scripts/db-app-user.sh` (novo): lê o `.env` e, com `docker compose exec -T postgres psql`, cria ou atualiza o *login* `smartparking_api` com `POSTGRES_APP_PASSWORD` e `GRANT smartparking_app`. É idempotente e falha com uma mensagem clara se o grupo ainda não existir (`database update` primeiro). A palavra-passe passa por uma variável do `psql` com `format('%L')` e `\gexec`, nunca por interpolação.

**Domain** (`src/SmartParking.Domain/`, sem dependências)
- `Common/`: `Entity`, `AggregateRoot` (lista de eventos, `Raise`, `DequeueEvents`), `IDomainEvent`, `DomainEventAttribute(name, version)` (dá o `type` da *outbox*, por exemplo `FacilityCreated.v1`), `GeoCoordinate` (value object com os intervalos de latitude e longitude), `DomainException` só para invariantes violadas por código (a validação de entrada é do FluentValidation).
- `Facilities/` (agregado de infraestrutura; não se chama `Infrastructure/` para não colidir com o projeto): `Operator` (com `ReferenceOperatorId` fixo), `ParkingFacility` (`Version` = `xmin`; `Create`, `Update` devolve se mudou alguma coisa, `MarkForDeletion`; levanta os eventos), `FacilityOpeningHours`, `FacilityStatus`, `Zone`, `ParkingSpot`, `SpotType`, `SpotStatus`, `Sensor`, `SensorKind`, `SensorStatus`, `Tariff`. Na F2a, só estrutura e invariantes de construção; o comportamento de zonas, lugares, sensores e tarifários é da F2b.
- `Users/`: `User` (email em minúsculas), `Vehicle` (matrícula normalizada), `PaymentMethod`, `PaymentMethodType`.
- `Sessions/`: `ParkingSession`, `SessionStatus`, `Reservation`, `ReservationStatus`, `Payment`, `PaymentStatus` (só estrutura; o comportamento é da F3).
- `Facilities/Events/`: `FacilityCreated`, `FacilityUpdated`, `FacilityDeleted` (`sealed record`, *payload* da spec: id, operatorId, name, latitude, longitude, timeZone, status). Os eventos de zonas, lugares, sensores e tarifários chegam na F2b, com o comportamento que os levanta.

**Application** (`src/SmartParking.Application/`)
- `Abstractions/Error.cs` e `ErrorType.cs`: `PreconditionFailed` e `PreconditionRequired`.
- `Abstractions/Persistence/IUnitOfWork.cs`: `SaveChangesAsync` passa a devolver `Task<Result>`, para a Infrastructure devolver os `Error` das violações. Não há implementações nem chamadas (confirmar com `find_references`).
- `Abstractions/ICurrentActor.cs` (`string Name`).
- `Abstractions/Paging/`: `PageRequest` (page, pageSize) e `PagedResult<T>` (items, page, pageSize, totalCount), com um validador partilhado (pageSize de 1 a 100).
- `Abstractions/Persistence/IParkingFacilityRepository.cs` (escrita: `GetByIdAsync`, `Add`, `Remove`, `SetExpectedVersion`) e `Abstractions/Reading/IFacilityReader.cs` (leitura: `GetAsync`, `ListAsync`, projeções para DTO).
- `Features/Facilities/`: `CreateFacility.cs`, `UpdateFacility.cs`, `DeleteFacility.cs`, `GetFacility.cs`, `ListFacilities.cs` (comando ou consulta, *handler* e validador). Validadores: latitude e longitude, fuso IANA (`TimeZoneInfo.TryFindSystemTimeZoneById`), horário (dias 0–6 sem repetidos, `opensAt < closesAt`). Os *handlers* de alteração comparam o `ExpectedVersion` com a `Version` carregada: nulo dá `PreconditionRequired`; diferente dá `PreconditionFailed`.

**Infrastructure/Relational** (`src/SmartParking.Infrastructure/Relational/`)
- `SmartParkingDbContext.cs`: implementa `IUnitOfWork` de forma explícita. Antes de gravar, recolhe os eventos dos agregados e, por cada evento, cria uma `OutboxMessage` e uma `AuditEntry` ligadas por `outboxMessageId`: `before` e `after` vêm dos valores originais e atuais do `ChangeTracker` (a raiz e as linhas do horário), o `actor` vem de `ICurrentActor` e o `traceId` de `Activity.Current`. Uma só chamada a `base.SaveChangesAsync`, numa só transação.
- `PostgresErrorTranslator.cs`: `DbUpdateException` com `PostgresException` → `23505`, `23503`, `23P01` → `Conflict`; `23514` → `Validation`, com o nome da restrição e a dependência na mensagem. `DbUpdateConcurrencyException` → `PreconditionFailed`. Só *catches* específicos.
- `Auditing/AuditEntry.cs`, `Outbox/OutboxMessage.cs` (tipos de persistência, fora do Domain) e as suas configurações.
- `Configurations/`: uma `IEntityTypeConfiguration` por entidade. Enums guardados como texto (os `CHECK` comparam com `'Occupancy'` e `'Active'`); `xmin` com `IsRowVersion`; `GeoCoordinate` ↔ `Point` (SRID 4326, `geography(Point,4326)`) por conversor, com índice GiST; FK compostas (`(facility_id, zone_id)` → `zones(facility_id, id)` e `(facility_id, spot_id)` → `parking_spots(facility_id, id)`) sobre chaves alternativas; índices únicos parciais; `CHECK`; `OnDelete(Restrict)` em tudo, exceto `FacilityOpeningHours` (`Cascade`); `HasData` do operador de referência.
- `SnakeCaseNamingConvention.cs`: tabelas no plural em `snake_case`, colunas, chaves e índices.
- `Repositories/ParkingFacilityRepository.cs`, `Reading/FacilityReader.cs` (`AsNoTracking` e projeções).
- `Migrations/…_Initial.cs`: gerada; acrescenta à mão, com `migrationBuilder.Sql`, os dois `EXCLUDE` (tarifários e reservas `Active`) e, no fim, o script do papel.
- `Migrations/Sql/0001_app_role.sql` (recurso embebido, versionado): cria o grupo `smartparking_app NOLOGIN` se não existir e dá as permissões da spec (negócio: `SELECT, INSERT, UPDATE, DELETE`; `audit_entries`: `SELECT, INSERT`; `outbox_messages`: `SELECT, INSERT, UPDATE`; sem DDL). O `Down` revoga as permissões.
- `DesignTimeDbContextFactory.cs`: o `dotnet ef` usa `ConnectionStrings__PostgresMigrations` (variável de ambiente; erro claro se faltar), para os comandos do `CLAUDE.md` correrem como dono sem mudar o `CLAUDE.md`.
- `RelationalDatabase.cs` (público): `MigrateAsync(ownerConnectionString, ct)`, usado pelos testes.
- `RelationalRegistration.cs`: `NpgsqlDataSourceBuilder` com `UseNetTopologySuite()`, `AddDbContext` sobre esse *data source*, `IUnitOfWork`, o repositório e o leitor. Igual nos dois modos (AC-22).
- `Hosting/AnonymousCurrentActor.cs` (`anonymous`), registado em `AddInfrastructure` para os dois modos.

**Api** (`src/SmartParking.Api/`)
- `Common/ResultExtensions.cs`: 412 e 428.
- `Common/EntityTagHeaders.cs`: `uint` ↔ `ETag` forte (`"123"`); um `If-Match` ausente dá `null`, e um malformado ou `*` dá uma versão impossível (412).
- `Features/Facilities/FacilityEndpoints.cs` (`IEndpointGroup`, `/api/facilities`, `AllowAnonymous` no grupo) e os DTO do pedido e da resposta. `TypedResults`, `Location` e `ETag` no 201 e no 200.
- `tests/http/configuration.http`: começa com os pedidos de parques; a F2b completa (AC-23).

**Testes**
- `tests/Common/DatabaseContainers.cs`: depois de arrancar, aplica as migrações como dono e cria o *login* `smartparking_api` (palavra-passe gerada por execução). Expõe `OwnerConnectionString`, `AppConnectionString` e `CreateIsolatedDatabaseAsync()`. As definições da Api usam o *login* da aplicação.
- `tests/SmartParking.Api.Tests/Fixtures/`: `DbProbe` (consultas SQL de verificação como dono) e dados únicos por teste (nomes com `Guid`), sem limpar a base entre testes.
- Ficheiros de teste: ver (e).

**Documentação**
- `docs/adr/0004-papel-da-aplicacao-e-auditoria.md` e uma linha no índice. O ADR dos tarifários com vigência é da F2b.
- `docs/RASTREABILIDADE.md`, `docs/DIARIO.md`, `docs/plans/README.md`, `docs/specs/README.md` (estado da 001: «Em implementação (F2a)»).

## (c) Skills do kit e adaptações à especificação

| Skill | Uso | Adaptação |
|---|---|---|
| `ef-core` | `DbContext`, configurações, migrações, `xmin` | Exceção D10: há repositório por raiz de agregado. Auditoria e *outbox* no `SaveChangesAsync` do `IUnitOfWork`, sem *interceptor*, para a transação ficar visível num só sítio. `EXCLUDE` por SQL na migração |
| `clean-architecture` | Domain sem dependências; portas em `Application/Abstractions` | `AuditEntry` e `OutboxMessage` na Infrastructure: são persistência, não domínio |
| `minimal-api`, `openapi` | `IEndpointGroup`, `TypedResults` | ETag/If-Match à mão (o `xmin` não é um *hash* do corpo) |
| `error-handling` | Result e ProblemDetails | `ErrorType` com 412 e 428; violações traduzidas na Infrastructure |
| `modern-csharp` | *records*, `sealed`, *primary constructors*, expressões de coleção | Entidades com construtor privado para o EF |
| `configuration`, `dependency-injection` | *connection strings*, registo por modo | Duas *connection strings*: aplicação e dono (só para migrações) |
| `testing` | Testcontainers e AAA | Sem Respawn: dados únicos por teste; base isolada só no AC-18 |
| `docker` | `db-app-user.sh` | Sem mexer no `compose.yaml` |
| `serilog` | Sem PII em `Information` | O email do `User` não entra nos *logs* |

## (d) Decisões D1–D10 em causa

- **D10 (ADR 0003):** `IParkingFacilityRepository` (escrita, sem `IQueryable` nem `SaveChanges`); `IFacilityReader` (leitura, sem repositório); o `DbContext` é o `IUnitOfWork`. Os repositórios dos outros agregados chegam com o comportamento (F2b, F3).
- **D2:** *outbox* em PostgreSQL; as linhas ficam com `published_at = null` (o leitor é da F3/F4).
- **D1:** sessões, reservas e pagamentos em PostgreSQL (só tabelas).
- As outras decisões não se alteram.

## (e) Correspondência AC → teste (F2a)

| AC | Teste |
|---|---|
| AC-1 | `Api.Tests/Features/Schema/InitialMigrationTests.Migrate_EmptyDatabase_CreatesSchemaExtensionsRoleAndReferenceOperator_AC1`; documental: `dotnet ef migrations add Initial` e `database update` contra o `compose.yaml` |
| AC-2 | `…InitialMigrationTests.FacilitiesLocation_Inspected_IsGeographyPointWithGistIndex_AC2` e `Api.Tests/Features/Facilities/CreateFacilityTests.CreateThenGet_ValidCoordinates_ReturnsSameCoordinates_AC2` |
| AC-3 | `…InitialMigrationTests.ParkingSpots_Inspected_HasNoOccupancyColumnOnlyStatus_AC3` |
| AC-4 | `…CreateFacilityTests.Create_ValidRequest_Returns201WithOneAuditEntryAndOneOutboxMessage_AC4` |
| AC-5 | `…DeleteFacilityTests.Delete_FacilityWithSpots_Returns409AndWritesNothing_AC5` (zona e lugar inseridos por SQL, porque os *endpoints* são da F2b) |
| AC-6 | `…DeleteFacilityTests.Delete_EmptyFacilityWithHours_Returns204CascadesHoursAndAudits_AC6` |
| AC-10 | `Api.Tests/Features/Schema/AppRolePermissionsTests` (`InsertSelectAudit_AsAppUser_Succeed_AC10`, `UpdateDeleteTruncateAudit_AsAppUser_Fail42501_AC10`, `DeleteOutbox_AsAppUser_Fails42501_AC10`) |
| AC-12 | `…UpdateFacilityTests` (`Put_StaleIfMatch_Returns412_AC12`, `Put_MissingIfMatch_Returns428_AC12`, `Put_CurrentETag_Returns200WithNewETag_AC12`) |
| AC-17 | `…FacilityValidationTests.Create_InvalidInput_Returns400ValidationProblemAndWritesNothing_AC17` (*theory*: latitude, fuso, horário; a F2b junta tarifário) |
| AC-18 | `…ListFacilitiesTests` (`List_Page2Size10Of25_Returns10AndTotal25_AC18` numa base isolada, `List_PageSize101_Returns400_AC18`, `Get_UnknownFacility_Returns404_AC18`); a F2b junta os lugares |
| AC-19 | `Api.Tests/Features/Schema/SessionConstraintsTests` (`OverlappingActiveReservations_AsAppUser_Fail23P01_AC19`, `TwoSucceededPayments_AsAppUser_Fail23505_AC19`, `DeleteTariffWithSessions_AsAppUser_Fails23503_AC19`) |
| AC-20 | `Api.Tests/Features/Errors/ErrorHandlingTests.ErrorToProblem_PreconditionErrors_Map412And428AsProblemDetails_AC20` |
| AC-21 | `ArchitectureTests/Features/RepositoryRulesTests` (`…_NoGenericRepository_AC21`, `…_RepositoryInterfacesInAbstractionsWithoutIQueryable_AC21`), `DomainEventRulesTests.DomainEvents_AreSealedRecordsInDomain_AC21`; os testes AC-1 a AC-3 da spec 000 continuam a passar |
| AC-22 | `Api.Tests/Features/Configuration/PersistenceModeTests.ConfigurationPorts_EachMode_AllResolve_AC22` (*theory* Hybrid/Monolithic) |

Testes sem AC: `UnitTests/Domain/` (invariantes de `GeoCoordinate`, horário, normalização do email e da matrícula); `PostgresErrorTranslatorTests`, que entram no AC-5 e no AC-19 de forma indireta.

## Passos

1. Ramo `feat/001-configuracao`; este plano em `docs/plans/` e no índice; a linha de RASTREABILIDADE (estado «em implementação»).
2. Pacotes (os três autorizados, `dotnet add package`) e espera do *restore* do *hook*.
3. Fundação: `ErrorType`/`Error` (412, 428), `ResultExtensions`, `ICurrentActor`, `IUnitOfWork` → `Task<Result>`, `PageRequest`/`PagedResult`. Teste AC-20.
4. Domain: base (`AggregateRoot`, `IDomainEvent`, atributo, `GeoCoordinate`), as entidades dos três agregados e os eventos de parques. Testes unitários do Domain.
5. Relational: `DbContext`, convenção `snake_case`, configurações de todas as entidades, `AuditEntry`, `OutboxMessage`, `HasData`.
6. Migração `Initial` (gerada) com os `EXCLUDE` e o script `0001_app_role.sql`; `DesignTimeDbContextFactory`; `RelationalDatabase.MigrateAsync`. Revisão do SQL gerado (`dotnet ef migrations script`).
7. Unidade de trabalho: auditoria e *outbox* no `SaveChangesAsync`, `PostgresErrorTranslator`, registo em `RelationalRegistration` (*data source* com NTS) e `AnonymousCurrentActor`.
8. Fixtures de teste: migrar como dono, criar o *login*, base isolada, `DbProbe`. Testes AC-1, AC-2 (esquema), AC-3, AC-10, AC-19.
9. Parques em Application: repositório, leitor, comandos, consultas e validadores.
10. Api: `FacilityEndpoints`, DTO, `EntityTagHeaders`, `configuration.http` (parques). Testes AC-2 (API), AC-4, AC-5, AC-6, AC-12, AC-17, AC-18.
11. Testes de arquitetura AC-21 e de modos AC-22.
12. Desenvolvimento local: `.env.example`, `appsettings.json` da Api, `scripts/db-app-user.sh`; AC-1 documental contra o `compose.yaml` e um pedido manual a `POST /api/facilities`.
13. ADR 0004 (papel da aplicação e auditoria só por acréscimo) e índice.
14. `/verify` (build `-warnaserror`, format, `dotnet test`, `get_diagnostics`, `detect_antipatterns`, `get_project_graph`, `get_di_registrations`) e revisão independente (`code-reviewer`).
15. RASTREABILIDADE, DIARIO, estado do plano e da spec; *commits* atómicos (`feat(domain)…`, `feat(relational)…`, `feat(facilities)…`, `docs…`, com «(spec 001)»); `/checkpoint`.

## Linhas a acrescentar em `docs/RASTREABILIDADE.md`

Na matriz, a linha RF-06 passa a:

| RF-06 | UC-02, UC-05 | AC-1 a AC-6, AC-12, AC-17, AC-18, AC-21, AC-22 (F2a); AC-7 a AC-9, AC-11, AC-13 a AC-16, AC-23 (F2b) | 001-configuracao (F2a, F2b) | `Domain/Facilities`, `Application/Features/Facilities`, `Infrastructure/Relational`, `Api/Features/Facilities` | `Api.Tests/Features/Facilities`, `Schema`; `ArchitectureTests/Features/RepositoryRulesTests`, `DomainEventRulesTests` | F2a a passar; F2b por fazer |

Na linha RNF-06, as colunas AC, especificação, código e teste ganham a parte desta fase («AC-10 (001-configuracao, F2a): auditoria só por acréscimo; `Relational/Migrations/Sql/0001_app_role.sql`; `Schema/AppRolePermissionsTests`»), e o estado fica «parcial (auditoria); cifra e RBAC na F11».

Nos critérios transversais, uma linha nova: `001-configuracao (F2a) | AC-19, AC-20 | Restrições do agregado de sessão; erros 412/428 | Relational/Configurations/Sessions, Api/Common/ResultExtensions.cs | Schema/SessionConstraintsTests, Errors/ErrorHandlingTests | a passar`. (O AC-19 prepara o RF-02, mas o RF-02 é da F3; liga-se aí.)

## Entrada a acrescentar em `docs/DIARIO.md`

```
## F2a: domínio, esquema e parques (spec 001)

- **Data:** 2026-11-24
- **Feito:** Domain dos três agregados; migração Initial (PostGIS, btree_gist, FK compostas, EXCLUDE, CHECK, operador de referência); grupo smartparking_app e login smartparking_api; auditoria e outbox na mesma transação; ICurrentActor; 412/428; endpoints de parques com ETag/If-Match; tradução 23505/23503/23P01/23514. Testes: <n> a passar (preencher com o resultado real).
- **Problemas:** <preencher durante a implementação, incluindo as abordagens que falharam>
- **Decisões:**
  - Pacotes EF Core da stack autorizados apesar do «Out» da spec (Diogo, 2026-11-24); snake_case por convenção própria, sem EFCore.NamingConventions.
  - AC-17 e AC-18 cobertos com parques na F2a; tarifários e lugares juntam-se na F2b (Diogo, 2026-11-24).
  - operatorId inexistente dá 409 pela FK (Diogo, 2026-11-24).
  - IUnitOfWork.SaveChangesAsync passa a devolver Result (tradução das violações sem EF Core em Application).
  - AuditEntry e OutboxMessage na Infrastructure; auditoria e outbox no SaveChangesAsync do DbContext, sem interceptor.
  - Migrações como dono por DesignTimeDbContextFactory (ConnectionStrings__PostgresMigrations); a Api liga como smartparking_api.
  - Workers continuam a ligar como dono: a rever na F3/F4, com o leitor da outbox.
- ***Commits:*** <hashes>
```

## Riscos e pontos de atenção

- **FK compostas com coluna partilhada** (`facility_id` em duas FK) e **conversor `GeoCoordinate` → `Point`**: se o EF 10 não os modelar bem, a alternativa é uma propriedade *shadow* `Point` e a FK composta por SQL na migração. Regista-se no diário. Se uma abordagem falhar duas vezes, para-se e volta-se a planear.
- **Papel ao nível do *cluster*:** `CREATE ROLE` é global; o script é idempotente e o `Down` não apaga o grupo (pode servir outras bases, como a base isolada do AC-18).
- **Schema `public` no PostgreSQL 17:** o *login* da aplicação precisa de `USAGE` no *schema*; o script dá-o de forma explícita.
- Se a F2a revelar um erro no esquema que obrigue a migração nova na F2b, a spec volta a «In Review» (regra da spec).

## Verificação ponta a ponta

1. `dotnet build SmartParking.slnx -warnaserror` e `dotnet format --verify-no-changes`.
2. `dotnet test` (Docker ligado): todos os testes da F1 e os `…_ACn` da tabela acima.
3. Local: `docker compose up -d --wait`; `ConnectionStrings__PostgresMigrations=… dotnet ef database update --project src/SmartParking.Infrastructure --startup-project src/SmartParking.Api`; `scripts/db-app-user.sh`; `dotnet run --project src/SmartParking.Api` com `ConnectionStrings__Postgres` do *login* da aplicação; `POST`, `GET`, `PUT` (com e sem `If-Match`) e `DELETE` de um parque pelo `configuration.http`; confirmar uma linha em `audit_entries` e outra em `outbox_messages` por alteração.
4. Roslyn: `get_project_graph` (dependências só para dentro), `get_di_registrations` (portas nos dois modos), `get_diagnostics`, `detect_antipatterns`.
