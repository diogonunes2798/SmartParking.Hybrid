<!-- Versão 0.2 (24-11-2026; revista às 14:20 com a D10 e a estrutura da infraestrutura (E1) fechadas). Fonte: tese v16 «revisto-17.11.2026».
     Substitui a v0.1 de 13-11-2026. Estrutura segundo o dotnet-claude-kit: template web-api (opção B,
     Clean Architecture) e regras de «CLAUDE.md Mastery for .NET». Máximo de 200 linhas; o detalhe vai
     para .claude/rules/ com paths. -->
# SmartParking.Hybrid — Web API e workers

Protótipo .NET da arquitetura híbrida de persistência do trabalho de projeto de Diogo Barbosa Nunes (Mestrado em Informática, ISEC; orientador: Prof. Francisco Barros Leite). É o artefacto da DSRM: demonstra os casos de uso (Cap. 6 da tese) e produz as medições da avaliação (Cap. 7).

## Contexto do projeto

- O protótipo existe para ser medido e justificado, não para produção. Cada decisão tem de poder ser defendida no Cap. 5: regista o porquê num ADR (`docs/adr/NNNN-titulo.md`).
- As especificações aprovadas estão em `docs/specs/` (formato `/spec` do kit). Lê a da funcionalidade antes de planear.
- Usa a numeração da tese em código, testes, commits e documentos: UC-01 a UC-05 (Tabela 12), RF-01 a RF-06 (Tabela 13), RNF-01 a RNF-06 (Tabela 14), decisões D1 a D10 (secção «Deferred Decisions» da especificação 000).
- Arquitetura escolhida: Clean Architecture, com pastas por funcionalidade dentro de Application. Já foi decidida; não voltes a correr o `architecture-advisor`.
- Cenário de referência (Tabela 9): 5 000 lugares, 1 evento por lugar a cada 5 min, picos de 10× (≈1,4 M eventos/dia; ≈17 eventos/s em média; ≈170 eventos/s em pico).

## Tech stack

- .NET 10 / C# 14. SDK fixado em `global.json`; versões NuGet centralizadas em `Directory.Packages.props` (última estável).
- ASP.NET Core Minimal APIs: um `IEndpointGroup` por funcionalidade e `app.MapEndpoints()`; OpenAPI com Scalar.
- Subcamada relacional: PostgreSQL 17 com PostGIS, EF Core 10 com Npgsql e NetTopologySuite (RF-04).
- Subcamada não relacional: MongoDB 8 (`MongoDB.Driver` 3.x) para o registo de eventos; Redis 8 (`StackExchange.Redis`) para o estado de disponibilidade, GEO, pub/sub e Streams.
- Barramento de eventos: interface `IEventBus`. No protótipo, outbox em PostgreSQL + Redis Streams (D2).
- Comandos e consultas: handlers próprios (`ICommandHandler<TCommand, TResult>`, `IQueryHandler<TQuery, TResult>`), sem biblioteca de mediação.
- Validação: FluentValidation num filtro de endpoint. Erros: Result pattern e ProblemDetails.
- Logs: Serilog estruturado. Métricas e traces: OpenTelemetry (exportação OTLP e CSV).
- Testes: xUnit v3, WebApplicationFactory e Testcontainers (PostgreSQL, MongoDB, Redis). Carga: k6 (D6).
- Infraestrutura local: `docker compose`, com as tags das imagens fixadas em `compose.yaml`.

## Estrutura

```
src/SmartParking.Domain/                    entidades, value objects, eventos de domínio (sem dependências)
src/SmartParking.Application/               Features/<Funcionalidade>/<Operação>.cs; Abstractions/ (portas)
src/SmartParking.Infrastructure/            um só projeto (E1), uma subpasta por subcamada:
  Relational/                               EF Core: agregados, auditoria, outbox, tabelas de resumo, modo monolítico
  Documents/                                MongoDB: registo de eventos só por acréscimo, índices, TTL
  Cache/                                    Redis: estado dos lugares, contadores, GEO, pub/sub
  Messaging/                                IEventBus: leitor da outbox, Redis Streams
src/SmartParking.Api/                       endpoints e ingestão; Program.cs mínimo
src/SmartParking.Workers/                   projetores, publicação da outbox, agregação, retenção
tools/SensorSimulator/                      gerador do cenário de referência (UC-01, UC-05); criado na F4
tools/ProjectionRebuilder/                  reconstrói o estado do Redis a partir do MongoDB (RNF-03); criado na F6
tests/                                      *.Tests com Features/ e Fixtures/ApiFixture.cs; SmartParking.ArchitectureTests
bench/                                      cenários k6 e scripts de medição (Cap. 7)
results/                                    CSV das experiências: versionados, nunca editados à mão
docs/                                       specs/, adr/, RASTREABILIDADE.md, DIARIO.md
```

## Comandos

```bash
docker compose up -d                                   # PostgreSQL+PostGIS, MongoDB (replica set), Redis
dotnet build SmartParking.slnx
dotnet test                                            # precisa do Docker (Testcontainers)
dotnet format --verify-no-changes
dotnet run --project src/SmartParking.Api
dotnet run --project src/SmartParking.Workers
dotnet ef migrations add <Nome> --project src/SmartParking.Infrastructure --startup-project src/SmartParking.Api
dotnet ef database update --project src/SmartParking.Infrastructure --startup-project src/SmartParking.Api
dotnet run --project tools/SensorSimulator -- --spots 5000 --mean-interval 300 --peak-factor 10 --seed 42   # a partir da F4
./bench/run.sh <cenario> --mode hybrid|monolithic --runs 5   # ainda não existe; escreverá results/<cenario>/<AAAA-MM-DD_HHMM>.csv
```

## Regras de arquitetura

- Dependências só para dentro: Domain ← Application ← Infrastructure, Api, Workers. Confirma com `get_project_graph` depois de mexer em referências.
- Estrutura da infraestrutura (E1): um projeto `SmartParking.Infrastructure` com as subpastas `Relational/`, `Documents/`, `Cache/` e `Messaging/` (template de Clean Architecture do kit). O isolamento das subcamadas é garantido por testes de arquitetura (`tests/SmartParking.ArchitectureTests`): `Infrastructure.Relational` não usa `MongoDB.Driver` nem `StackExchange.Redis`; `Documents` e `Cache` não usam EF Core. Não cries projetos de infraestrutura separados sem perguntar.
- Repartição dos dados (Tabela 15 da tese):
  - PostgreSQL: Parque, Lugar, Sensor (registo do dispositivo), Tarifário, Utilizador, Veículo, Meio de pagamento, Sessão, Reserva, Pagamento, auditoria e outbox;
  - MongoDB: eventos de ocupação e leituras ambientais;
  - Redis: estado atual de cada lugar e contadores de lugares livres por parque e por zona;
  - tabelas de resumo em PostgreSQL: agregados analíticos.
- CQRS: os comandos escrevem no PostgreSQL (ou no registo de eventos, na ingestão). A disponibilidade (UC-03) lê só do Redis. Os relatórios (UC-04) leem só das tabelas de resumo. Nenhuma consulta de elevada frequência toca nas tabelas transacionais.
- Sincronização: o evento de domínio vai para a outbox na mesma transação da alteração de negócio; os consumidores são idempotentes, porque a entrega é «pelo menos uma vez». Nunca uses transações distribuídas (2PC).
- `Persistence:Mode=Hybrid|Monolithic` escolhe as implementações das portas. O modo monolítico (Cap. 7.5) guarda tudo em PostgreSQL e partilha Domain, Application e Api com o modo híbrido.
- Repository (D10; tese 5.4.2 e 5.7; justificação no ADR 0003). Exceção deliberada à recomendação geral do kit de usar o `DbContext` diretamente, sem repositórios:
  - Escrita: um repositório por raiz de agregado, com a interface em `Application/Abstractions` e uma implementação por subcamada e por `Persistence:Mode`. Métodos de domínio (`GetByIdAsync`, `Add`…), sem devolver `IQueryable`.
  - O repositório não chama `SaveChanges`: o `DbContext` é a unidade de trabalho e grava a outbox na mesma transação. Por dentro, usa consultas compiladas e não esconde o change tracking.
  - Consultas: sem repositórios. Cada consulta usa uma porta de leitura própria em `Application/Abstractions` (por exemplo, `IAvailabilityReader`, `IReportReader`), implementada em Infrastructure com projeções e `AsNoTracking`, ou sobre o Redis e as tabelas de resumo.
  - Nunca criar um repositório genérico (`IRepository<T>`) nem métodos CRUD genéricos.
- Detalhe por subcamada: `.claude/rules/persistencia.md`. Medições: `.claude/rules/medicoes.md`.

## Convenções de código

### Nomes
- Código, testes, commits e mensagens de log em inglês. Documentação em `docs/` em português europeu.
- PascalCase nos membros públicos, `_camelCase` nos campos privados, sufixo `Async` nos métodos assíncronos.
- Testes: `Metodo_Cenario_ResultadoEsperado`; cada teste de aceitação tem no nome o `AC-n` da especificação.

### Padrões que usamos
- Primary constructors, records para DTO e eventos, `sealed` por omissão, file-scoped namespaces, collection expressions.
- `TimeProvider` injetado; `CancellationToken` em todos os caminhos assíncronos.
- `TypedResults` nos endpoints; DTO próprios na fronteira da API.
- Concorrência otimista com token (`xmin` do PostgreSQL) nas sessões e reservas (RF-02).
- `IHttpClientFactory` no simulador e em qualquer cliente HTTP.

### Padrões que NÃO usamos (nunca sugerir)
- Endpoints em `Program.cs`, `MapGroup` manual, `Results.Ok()`.
- `DateTime.Now`/`UtcNow`, `new HttpClient()`, `async void`, `.Result`, `.Wait()`.
- Devolver entidades do domínio nos endpoints.
- Repositório genérico (`IRepository<T>`); repositórios nas consultas; `DbContext` injetado em Application.
- Base de dados em memória nos testes.
- `catch (Exception)` genérico; interpolação de strings nas mensagens de log.
- MediatR, MassTransit, AutoMapper, FluentAssertions 8+ (licenças comerciais).
- Alterar ou apagar eventos de ocupação ou linhas de auditoria: são só por acréscimo.
- Valores de desempenho escritos à mão em código, testes ou documentos.

## Validação

- RF-01: um evento sem `eventId`, `sensorId`, `spotId`, tipo ou `timestamp` é rejeitado com 400 e ProblemDetails. Um `eventId` repetido é ignorado sem erro (idempotência).
- Os campos desconhecidos em `payload` são aceites e guardados (UC-05, RNF-04).

## Testes

- Integração primeiro: WebApplicationFactory + Testcontainers, padrão AAA.
- Cada `AC-n` de uma especificação aprovada tem um teste com esse nome (`/tdd`).
- Os testes de desempenho não fazem parte do `dotnet test`; estão em `bench/`.

## Git

- Conventional Commits, pequenos e atómicos, com a especificação: `feat(ingest): idempotent occupancy ingestion (spec 001)`.
- Ramos `feat/<NNN>-<slug>`. Nunca `push --force` para `main`. Nunca saltar os hooks de pre-commit.

## Fluxo de trabalho

- Ciclo do kit: `/spec` → `/plan` → `/scaffold` → `/verify`. Plan mode em tudo o que não seja trivial.
- Antes de implementar, confirma o UC, RF ou RNF e o `AC-n` a que a funcionalidade responde e regista a ligação em `docs/RASTREABILIDADE.md`.
- Não alteres uma decisão D1 a D10 sem perguntar ao Diogo. Usa o valor por omissão e deixa o ponto de extensão preparado.
- Não inventes resultados, valores de referência nem citações. Se faltar um dado, escreve `TODO(Diogo):` e pergunta.
- No fim de cada fase do plano: atualizar `docs/RASTREABILIDADE.md` e `docs/DIARIO.md` (o que foi feito, problemas, decisões). O diário é a matéria-prima das secções 5.9 e 7.8. Fecha a sessão com `/checkpoint` ou `/wrap-up`.
- Se uma abordagem falhar duas vezes, para e volta a planear. Pesquisa longa vai para um subagente, uma tarefa de cada vez.

## Skills do kit a carregar

`modern-csharp`, `clean-architecture`, `minimal-api`, `openapi`, `scalar`, `ef-core` (com a exceção D10: a secção «Don't Wrap DbContext in a Repository» não se aplica aos repositórios por agregado do lado da escrita), `caching`, `messaging`, `error-handling`, `configuration`, `dependency-injection`, `serilog`, `opentelemetry`, `testing`, `docker`, `workflow-mastery`.

## MCP (cwm-roslyn-navigator)

- `find_symbol` e `get_symbol_detail` antes de alterar um tipo; `find_references` antes de mudar uma assinatura.
- `get_project_graph` para confirmar a direção das dependências; `get_di_registrations` ao mudar o `Persistence:Mode`.
- `get_diagnostics` e `detect_antipatterns` depois de cada alteração.

## Termos do domínio

| Tese (PT) | Código |
|---|---|
| Parque de Estacionamento | `ParkingFacility` |
| Lugar de Estacionamento | `ParkingSpot` |
| Sensor (registo do dispositivo) | `Sensor` |
| Evento de Ocupação (imutável) | `OccupancyEvent` (`Occupied`, `Freed`) |
| Sessão de Estacionamento | `ParkingSession` (`Open`, `Closed`, `Paid`) |
| Reserva | `Reservation` |
| Tarifário | `Tariff` |
| Utilizador (condutor) / Veículo | `User` / `Vehicle` |
| Pagamento / Meio de pagamento | `Payment` / `PaymentMethod` |
| Zona / Operador | `Zone` / `Operator` |
| Registo de auditoria | `AuditEntry` |
| Tabela de saída de eventos | `OutboxMessage` |
| Projetor / vista de leitura | `*Projector` / read model |
| Tabela de resumo | `OccupancySummary`, `RevenueSummary` |
