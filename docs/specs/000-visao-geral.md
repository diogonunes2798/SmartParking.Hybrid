# Spec 000: Visão geral e fundação da solução

**Status:** Approved (2026-11-24)
**Date:** 2026-11-24
**Fase:** F1 · **Secção da tese:** 5.9.1 · **Objetivos:** OE6 · **Requisitos:** transversal (sem RF/RNF próprios)

## Visão geral

Protótipo da arquitetura híbrida de persistência (artefacto da DSRM): PostgreSQL com PostGIS para os agregados transacionais, a auditoria, a *outbox* e as tabelas de resumo; MongoDB para o registo de eventos de ocupação; Redis para o estado de disponibilidade. A sincronização faz-se por *outbox* e Redis Streams (D2). Clean Architecture com pastas por funcionalidade em Application. A ordem das fases F1–F13, os requisitos de cada uma e as dependências entre fases estão em [`docs/specs/README.md`](README.md) e não se repetem aqui.

## Problem

Sem uma fundação comum, cada fase reinventaria os aspetos transversais (erros, *logs*, telemetria, *health checks*, configuração, testes de integração) e o modo monolítico (F9) não conseguiria partilhar Domain, Application e Api com o modo híbrido, o que comprometeria a comparação do Cap. 7.5.

**Critério de sucesso:** as fases F2–F13 só acrescentam Features e implementações de portas, sem mexer na fundação; as regras de arquitetura (direção das dependências e isolamento das subcamadas) são verificadas por testes desde o primeiro *commit* da solução.

## Scope

### In

- Visão geral do protótipo (secção acima) e registo das decisões D1–D10 (secção «Deferred Decisions»).
- Esqueleto executável da solução, sem funcionalidades de negócio: `SmartParking.slnx`, `global.json`, `Directory.Packages.props`, `Directory.Build.props` e os projetos Domain, Application, Infrastructure (subpastas `Relational/`, `Documents/`, `Cache/` e `Messaging/`, E1), Api e Workers.
- `Directory.Build.props`: `TreatWarningsAsErrors`, `Nullable` e analisadores .NET ligados em todos os projetos, testes incluídos.
- Abstrações transversais (só contratos e a sua infraestrutura, sem uso de negócio):
  - `Result`/`Result<T>` com `Error` tipado (`NotFound`, `Validation`, `Conflict`, `Unauthorized`) e o mapeamento para ProblemDetails;
  - `ICommandHandler<TCommand, TResult>` e `IQueryHandler<TQuery, TResult>` com registo automático;
  - `IEndpointGroup` com `app.MapEndpoints()` e o filtro de *endpoint* do FluentValidation;
  - `IEventBus` (interface em `Application/Abstractions`, D2); a *outbox* e o Redis Streams ficam para F3/F4;
  - `IUnitOfWork` (interface em `Application/Abstractions`, D10: o `DbContext` é a unidade de trabalho); sem implementação até à F2.
- Tratamento global de exceções (`UseExceptionHandler` + `IExceptionHandler`) com ProblemDetails.
- Serilog: consola com JSON compacto e envio para o OTLP quando o *endpoint* estiver configurado; o `TraceId` acompanha cada *log*.
- OpenTelemetry: *traces*, métricas e *logs* com exportação OTLP. O *endpoint* vem da configuração (`OTEL_EXPORTER_OTLP_ENDPOINT`); sem valor, a exportação OTLP fica desligada e a aplicação arranca na mesma. Nenhum serviço novo no `compose.yaml` (o ADR 0001 não muda).
- OpenAPI com Scalar, só no ambiente `Development`.
- `Persistence:Mode`: opções tipadas validadas no arranque (`ValidateOnStart`); um valor diferente de `Hybrid` ou `Monolithic` impede o arranque. Um método de extensão de registo por modo, ainda vazio, fica como ponto de extensão para a F2.
- *Health checks* na Api e nos Workers: `/health/live` sem dependências; `/health/ready` verifica PostgreSQL, MongoDB e Redis no modo `Hybrid` e só o PostgreSQL no modo `Monolithic`. Os *endpoints* são um `IEndpointGroup`.
- Workers: projeto `Microsoft.NET.Sdk.Web` mínimo, só com `/health/*` e a telemetria. Na F1 não há nenhum `BackgroundService`.
- Projetos de teste: `SmartParking.ArchitectureTests`, `SmartParking.Api.Tests` (integração com `ApiFixture`), `SmartParking.Workers.Tests` (integração do *host* dos Workers) e `SmartParking.UnitTests` (pastas `Domain/` e `Application/`).
- Testcontainers com as mesmas imagens do `compose.yaml` (`postgis/postgis:17-3.7`, `mongo:8.0.38` em replica set, `redis:8.12.0`), declaradas numa constante única nos testes.
- ADR 0003 (D10, Repository por raiz de agregado).

### Out

- Qualquer entidade, *endpoint*, tabela ou migração de negócio (a partir de F2).
- Os mapas completos das fases F2–F13: ficam no índice das especificações.
- Exportador de métricas para CSV: F12, onde se fixa o formato das colunas de `results/`.
- `tools/SensorSimulator` (F4/F8) e `tools/ProjectionRebuilder` (F6/F12): criados na fase que os usa.
- Autenticação, RBAC, HTTPS obrigatório e HSTS: F11 (RNF-06).
- Implementações de `IEventBus` (F3/F4) e de `IUnitOfWork` (F2).
- Integração contínua (por exemplo, GitHub Actions): o *pre-commit* (antipadrões + `dotnet format`) e o `dotnet test` local chegam nesta fase.

## Domain Model

Sem entidades de domínio na F1. O projeto Domain fica vazio, exceto o que as abstrações transversais exigirem (nenhuma das listadas depende de Domain). Os tipos `Result`, `Error` e as interfaces de handlers, `IEventBus` e `IUnitOfWork` ficam em Application.

## API Contract

| Verbo | Rota | Projeto | Resposta | Erros |
|---|---|---|---|---|
| GET | `/health/live` | Api, Workers | 200 `Healthy` | — |
| GET | `/health/ready` | Api, Workers | 200 com JSON: estado global e, por verificação (`postgres`, `mongo`, `redis` conforme o modo), estado e duração | 503 com o mesmo JSON quando uma verificação falha |
| GET | `/openapi/v1.json` | Api | 200 (só `Development`) | 404 fora de `Development` |
| GET | `/scalar/v1` | Api | 200 (só `Development`) | 404 fora de `Development` |

Contrato de erro comum (todas as fases): `application/problem+json` (RFC 9457) com `traceId`; os erros de validação usam `ValidationProblemDetails` com o dicionário `errors`. Fora de `Development`, a resposta a uma exceção não tratada não inclui a mensagem nem o *stack trace*.

## Authorization

- `/health/live` e `/health/ready` (Api e Workers) com `AllowAnonymous` explícito.
- OpenAPI e Scalar só no ambiente `Development`.
- Sem autenticação, RBAC nem HTTPS obrigatório na F1: pertencem ao RNF-06 (F11). Registado como fora do âmbito, não como esquecimento.

## Edge Cases & Failure Modes

- Uma base não responde: `/health/ready` devolve 503 com o estado e a duração de cada verificação; `/health/live` continua a devolver 200. Cada verificação tem *timeout* configurável, para que o *endpoint* não fique pendurado.
- `Persistence:Mode` ausente ou com valor inválido: a aplicação não arranca (`ValidateOnStart`).
- `OTEL_EXPORTER_OTLP_ENDPOINT` ausente: a exportação OTLP fica desligada, sem erro.
- Exceção não tratada: 500 com ProblemDetails e `traceId`, registada no Serilog com o *stack trace*.

## Non-Functional Requirements

- Sem alvo de desempenho na F1: as medições começam na F12 (RNF-01 a RNF-03) e nenhum valor de desempenho é escrito à mão.
- Reprodutibilidade: SDK fixado em `global.json`, versões NuGet centralizadas (última estável compatível com `net10.0`; EF Core e Npgsql na linha 10.x, ver diário A1–A2) e as mesmas imagens nos testes e no `compose.yaml`.
- Os testes de integração precisam do Docker (Testcontainers).

## Integrations

- PostgreSQL+PostGIS, MongoDB (replica set) e Redis: só *health checks* na F1.
- Coletor OTLP opcional, indicado por configuração.
- Nenhum evento publicado, nenhum *webhook* e nenhum serviço externo.

## Acceptance Criteria

Cada AC tem um teste com o seu número no nome, exceto os marcados como documentais.

- **AC-1 (dependências entre camadas).** Dado o conjunto de *assemblies* da solução, quando os testes de arquitetura correm, então Domain não depende de nenhum outro projeto da solução, Application depende só de Domain, e nem Domain nem Application dependem de Infrastructure, Api ou Workers.
- **AC-2 (isolamento das subcamadas, E1).** Dado o *assembly* de Infrastructure, quando os testes de arquitetura correm, então os tipos de `Infrastructure.Relational` não usam `MongoDB.Driver` nem `StackExchange.Redis`, e os de `Infrastructure.Documents` e `Infrastructure.Cache` não usam EF Core.
- **AC-3 (Application sem EF Core, D10).** Dado o *assembly* de Application, quando os testes de arquitetura correm, então nenhum tipo usa `Microsoft.EntityFrameworkCore`, `MongoDB.Driver` nem `StackExchange.Redis`.
- **AC-4 (liveness da Api).** Dada a Api a correr sem nenhuma base acessível, quando se pede `GET /health/live`, então a resposta é 200.
- **AC-5 (readiness híbrida).** Dada a Api em `Persistence:Mode=Hybrid` com as três bases dos Testcontainers, quando se pede `GET /health/ready`, então a resposta é 200 e o JSON lista `postgres`, `mongo` e `redis` como `Healthy`.
- **AC-6 (readiness monolítica).** Dada a Api em `Persistence:Mode=Monolithic` com o PostgreSQL, quando se pede `GET /health/ready`, então a resposta é 200 e o JSON lista só `postgres`.
- **AC-7 (readiness com falha).** Dada a Api em modo `Hybrid` com o Redis parado, quando se pede `GET /health/ready`, então a resposta é 503 e o JSON indica `redis` como `Unhealthy`; `GET /health/live` continua a devolver 200.
- **AC-8 (health dos Workers).** Dado o *host* dos Workers nos modos `Hybrid` e `Monolithic`, quando se pede `/health/live` e `/health/ready`, então o comportamento é o de AC-4 a AC-6.
- **AC-9 (modo inválido).** Dado `Persistence:Mode` ausente ou com um valor fora de `Hybrid`/`Monolithic`, quando a Api ou os Workers arrancam, então o arranque falha com um erro de validação de opções que nomeia a chave.
- **AC-10 (exceção não tratada).** Dado um *endpoint* de teste que lança uma exceção, com o ambiente diferente de `Development`, quando é chamado, então a resposta é 500, `application/problem+json`, com `traceId` e sem a mensagem nem o *stack trace* da exceção.
- **AC-11 (Result → ProblemDetails).** Dado um `Error` de cada tipo, quando é convertido em resposta HTTP, então `NotFound` → 404, `Validation` → 400 com `errors`, `Conflict` → 409 e `Unauthorized` → 401, todos em ProblemDetails.
- **AC-12 (filtro de validação).** Dado um *endpoint* de teste com um validador FluentValidation, quando recebe um pedido inválido, então a resposta é 400 com `ValidationProblemDetails` e o *handler* não é chamado.
- **AC-13 (OpenAPI por ambiente).** Dada a Api em `Development`, quando se pede `/openapi/v1.json`, então a resposta é 200; dada a Api noutro ambiente, a mesma rota devolve 404.
- **AC-14 (imagens dos testes).** Dados o `compose.yaml` e a constante de imagens dos testes, quando o teste os compara, então as três imagens e tags coincidem.
- **AC-15 (documental: ADR 0003).** O ADR `docs/adr/0003-*.md` existe, com contexto, decisão, alternativas consideradas, consequências, requisito e secção da tese (5.4.2 e 5.7), e a referência por confirmar marcada com [VERIFICAR]; está no índice dos ADR.
- **AC-16 (documental: compilação limpa).** Dado um clone limpo, quando se corre `dotnet build SmartParking.slnx` e `dotnet format --verify-no-changes`, então ambos terminam sem erros nem avisos. Verificado pelo `/verify`, não por um teste xUnit.

## Deferred Decisions

Fonte: secção 5 («Decisões em aberto») do plano da tese, colada pelo Diogo a 2026-11-24. As referências de secção são da tese v16.

Regra (Diogo, 2026-11-24): D1–D9 ficam diferidas; o valor por omissão é o da tese v16 (coluna «Estado atual») e o gatilho de revisão é a confirmação de quem decide. O código usa o valor por omissão atrás de uma interface, para que uma alteração não obrigue a reescrever os consumidores. D10 está decidida.

| ID | Decisão | Estado atual | Quem decide |
|---|---|---|---|
| D1 | Onde ficam as sessões de estacionamento | No texto: relacional (5.4.1) | Confirmar com o orientador |
| D2 | *Broker* de mensagens | No texto: *outbox* + Redis Streams no protótipo; Kafka em produção (5.6.2) | Confirmar com o orientador |
| D3 | Event Sourcing | No texto: registo imutável de eventos em MongoDB (5.5.2) | Confirmar com o orientador |
| D4 | Camada analítica | No texto: tabelas de resumo em PostgreSQL; *data warehouse* ou *lakehouse* como evolução (5.6.3) | Confirmar com o orientador |
| D5 | Azure Cosmos DB | No texto: fora do protótipo, trabalho futuro (5.5.3) | Confirmar com o orientador |
| D6 | Ferramenta de carga | k6 | Diogo |
| D7 | Latência alvo do UC-01 | No texto: < 1 s no p95 | Confirmar |
| D8 | Cenário de referência | No texto (3.1, Tabela 9): 5 000 lugares, 1 evento a cada 5 min por lugar, picos de 10× (≈1,4 M eventos/dia, ≈170 eventos/s em pico) | Confirmar |
| D9 | Prioridades dos requisitos | No texto (Tabelas 13 e 14): alta para RF-01, 02, 03 e 06 e para RNF-01, 02 e 04; média para os restantes | Diogo e orientador |
| D10 | Repository sobre o EF Core | Decidida (24-11-2026): Repository por raiz de agregado na escrita (interface em `Application/Abstractions`, uma implementação por subcamada e por `Persistence:Mode`, sem `SaveChanges`: o `DbContext` é a unidade de trabalho); consultas sem repositório, com portas de leitura próprias; sem repositório genérico. Segue a tese (5.4.2 e 5.7) e mantém do *kit* a proibição do repositório genérico. Aplicada no `CLAUDE.md` e na secção «Data Access» do `.claude/rules/architecture.md`. Falta: ADR 0003 (F1, AC-15); guião (A1, F1, *prompt* da F2); texto da 5.4.2 e da 5.7 e a referência pedida pelo orientador (candidatas: Fowler, 2002; Evans, 2003; guia da Microsoft *.NET Microservices: Architecture for Containerized .NET Applications*; [VERIFICAR]) | Diogo (resolvido); o orientador valida o texto da 5.4.2 e da 5.7 |

## Open Questions

Nenhuma.
