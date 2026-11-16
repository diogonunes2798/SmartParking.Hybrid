# SmartParking.Hybrid

Protótipo de uma **arquitetura híbrida de persistência de dados** para a gestão do estacionamento urbano. É o artefacto da dissertação de mestrado «Arquitetura Híbrida de Persistência de Dados para a Gestão Sustentável de um Espaço Urbano» (ISEC, Departamento de Informática e Sistemas), desenvolvida segundo a metodologia *Design Science Research* (Peffers et al., 2007).

Cada tipo de dados fica no motor que melhor lhe responde, e as escritas e as leituras são separadas com CQRS:

| Dados | Motor | Porquê |
|---|---|---|
| Parques, lugares, sensores (configuração), tarifários, utilizadores, veículos, sessões, pagamentos, auditoria | **PostgreSQL** | ACID e integridade referencial (UC-02, RF-02, RF-06) |
| Eventos de ocupação dos sensores (log imutável) | **MongoDB** | Volume e esquema flexível (UC-01, UC-05, RNF-04) |
| Estado de cada lugar e contagens por parque e zona (*read model*) | **Redis** | Leitura de baixa latência (UC-03, RNF-01) |
| Agregados históricos de ocupação | **PostgreSQL** (schema `analytics`) | Isolamento analítico (UC-04, RF-05) |

A especificação completa está em [docs/ESPECIFICACAO.md](docs/ESPECIFICACAO.md).

## Arquitetura

```
 Sensores ──HTTP──▶ Api ──XADD──▶ Redis Streams ──▶ Workers ──┬─▶ MongoDB (log de eventos, append-only)
 (SensorSimulator)   │                                         ├─▶ Redis (read model + pub/sub)
                     │                                         └─▶ PostgreSQL analytics (resumos horários)
                     │
 Clientes ──HTTP──▶ Api ──EF Core──▶ PostgreSQL (+ tabela outbox, mesma transação)
                     │                    │
                     │                    └─▶ relay da outbox (Workers) ──▶ Redis Streams ──▶ read model
                     └─ GET /api/availability lê só do Redis
```

Padrões principais:

- **CQRS** com *handlers* próprios, sem bibliotecas de mediação.
- **Outbox transacional** em vez de *two-phase commit*; o *relay* usa `FOR UPDATE SKIP LOCKED` (ADR-0004, ADR-0009).
- **Redis Streams** como *bus*, atrás de `IEventBus` (decisão D2, ADR-0006).
- **Consumidores idempotentes**, porque a entrega é «pelo menos uma vez».
- **Log de eventos append-only** em MongoDB, com `payload` extensível por geração de sensor (D3, ADR-0007, ADR-0011).
- **Observabilidade** com OpenTelemetry: métricas em `/metrics` (Prometheus) e *traces* opcionais por OTLP (Jaeger).

As decisões estão registadas em [docs/adr/](docs/adr/).

## Estado

O plano de implementação tem dez fases (secção 9 da especificação). Estado atual:

| Fase | Conteúdo | Estado |
|---|---|---|
| 1 | Fundação: solução, compose, *health checks*, OpenTelemetry | Concluída |
| 2a / 2b | Domínio, EF Core, configuração (RF-06), sessões e pagamento (UC-02) com outbox | Concluída |
| 3 | Ingestão (UC-01) até ao MongoDB | Concluída |
| 4 | *Read model* de disponibilidade em Redis (UC-03) | Concluída |
| 5 | *Relay* da outbox e medição da janela de consistência | Concluída |
| 6 | Analítica com tabelas de resumo (UC-04) | Concluída |
| 7 | Simulador de sensores e evolução de esquema (UC-05), com ensaio piloto | Concluída |
| 8 | Variante monolítica (`Persistence:Mode=Monolithic`, ADR-0012) | Concluída |
| 9 | *Benchmarks* (cenários da secção 6, `bench/run.sh`, CSV) | Em curso: 9a (infraestrutura de medição, [bench/README.md](bench/README.md)) feita; cenários por fazer |
| 10 | Retenção e sustentabilidade (RNF-05) | Por fazer |

O estado de cada requisito, com os artefactos e os testes que o comprovam, está em [docs/RASTREABILIDADE.md](docs/RASTREABILIDADE.md). O registo do que foi feito em cada fase, com os problemas e as decisões, está em [docs/DIARIO.md](docs/DIARIO.md).

## Requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/) (versão fixada em [global.json](global.json))
- Docker com `docker compose` (os testes de integração também precisam do Docker, por causa do Testcontainers)
- Python 3, apenas para os *scripts* de medição (`bench/lib/`, `bench/schema-evolution/`)
- Git Bash no Windows, para o `bench/run.sh`; o k6 corre num contentor e não precisa de instalação

## Início rápido

Infraestrutura local (PostgreSQL 16, MongoDB 7 e Redis 7, com imagens fixadas por *digest*):

```bash
docker compose up -d
```

Ferramentas e esquema relacional:

```bash
dotnet tool restore
dotnet ef database update --project src/SmartParking.Infrastructure.Relational --startup-project src/SmartParking.Api
```

API e Workers (cada um no seu terminal):

```bash
dotnet run --project src/SmartParking.Api
```

```bash
dotnet run --project src/SmartParking.Workers
```

| Serviço | Endereço |
|---|---|
| API (Swagger) | http://localhost:5080/swagger |
| API (prontidão e métricas) | http://localhost:5080/health/ready, http://localhost:5080/metrics |
| Workers (prontidão e métricas) | http://localhost:5090/health/ready, http://localhost:5090/metrics |

As credenciais do compose são valores de desenvolvimento. Para as mudar, copie [.env.example](.env.example) para `.env` e atualize as *connection strings* em `appsettings.Development.json`.

### Observabilidade (opcional)

```bash
docker compose --profile observability up -d
```

Arranca o Prometheus (http://localhost:9090) e o Jaeger (http://localhost:16686). Os *traces* só são exportados se a API e os Workers arrancarem com `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`.

## API

| Caso de uso | Endpoints |
|---|---|
| UC-01 — Ingestão de eventos de ocupação | `POST /api/ingest/occupancy` (lotes; responde `202 Accepted`) |
| UC-02 — Sessão e pagamento | `POST /api/sessions`, `POST /api/sessions/{id}/close`, `POST /api/sessions/{id}/pay`, `GET /api/sessions/{id}` |
| UC-03 — Disponibilidade | `GET /api/availability?lat=&lon=&radius=&type=&limit=` |
| UC-04 — Relatório de ocupação | `GET /api/reports/occupancy?zone=&from=&to=&granularity=hour\|day\|week\|month` |
| RF-06 — Configuração | CRUD em `/api/facilities`, `/api/facilities/{facilityId}/spots` e `/api/spots/{id}`, `/api/sensors`, `/api/tariffs` |
| Pré-condição do UC-02 | `POST /api/users`, `POST /api/users/{id}/vehicles` |

O contrato completo está no Swagger.

## Comandos dos Workers

Além do serviço contínuo (consumidores, *relay* da outbox, agregação), os Workers têm dois comandos pontuais:

```bash
dotnet run --project src/SmartParking.Workers -- rebuild-read-model
```

```bash
dotnet run --project src/SmartParking.Workers -- aggregate-occupancy
```

O primeiro reconstrói o *read model* do Redis a partir do PostgreSQL e do MongoDB (ADR-0008). O segundo agrega as horas fechadas nas tabelas de resumo (ADR-0010).

## Simulador de sensores

O [SensorSimulator](tools/SensorSimulator/README.md) gera carga reprodutível (semente fixa) para o UC-01 e o UC-05: cria parques, lugares e sensores pela API e envia eventos de ocupação coerentes em lotes. Precisa da API e dos Workers a correr.

```bash
dotnet run --project tools/SensorSimulator -- --facilities 2 --spots 50 --rate 5 --duration 60 --seed 42
```

```bash
dotnet run --project tools/SensorSimulator -- --scenario bench/scenarios/d8-reference.json
```

Os cenários versionados estão em [bench/scenarios/](bench/scenarios/): `smoke.json`, `d8-reference.json` (cenário de referência D8: 5 000 lugares, um evento por lugar a cada 5 min em média, picos de 10×) e `schema-evolution-pilot.json`.

## Testes

```bash
dotnet build
```

```bash
dotnet test
```

Os testes usam xUnit sobre o Microsoft.Testing.Platform. Os de integração arrancam o PostgreSQL, o MongoDB e o Redis com Testcontainers, com as mesmas imagens do compose. Para correr um único projeto:

```bash
dotnet test --project tests/SmartParking.ArchitectureTests
```

| Projeto | O que verifica |
|---|---|
| `SmartParking.ArchitectureTests` | Regra de dependências entre camadas (NetArchTest e grafo de projetos) |
| `SmartParking.Api.IntegrationTests` | Endpoints, persistência, outbox, *read model*, analítica, falhas e telemetria, contra os motores reais |
| `SmartParking.SensorSimulator.Tests` | Determinismo, coerência e cenários do simulador |

## Estrutura do repositório

```
src/
  SmartParking.Domain/                      entidades, eventos de domínio, regras (sem dependências externas)
  SmartParking.Application/                 comandos, consultas (CQRS), interfaces de repositório
  SmartParking.Infrastructure.Relational/   EF Core + PostgreSQL, outbox, auditoria, analítica
  SmartParking.Infrastructure.Documents/    MongoDB (log de eventos de ocupação)
  SmartParking.Infrastructure.Cache/        Redis (read model de disponibilidade, pub/sub)
  SmartParking.Infrastructure.Messaging/    Redis Streams (IEventBus, consumidores)
  SmartParking.Api/                         endpoints HTTP e ingestão
  SmartParking.Workers/                     projeções, relay da outbox, agregação
  SmartParking.ServiceDefaults/             OpenTelemetry e health checks partilhados
tests/                                      arquitetura, integração e simulador
tools/SensorSimulator/                      gerador de eventos de sensores
bench/                                      cenários de carga e scripts de medição
infra/                                      configuração do Prometheus
docs/                                       especificação, rastreabilidade, diário, ADR e revisões
results/                                    resultados das experiências (CSV, versionados)
```

## Documentação

- [docs/ESPECIFICACAO.md](docs/ESPECIFICACAO.md): objetivo, domínio, casos de uso, requisitos não funcionais e decisões em aberto (D1–D8)
- [docs/RASTREABILIDADE.md](docs/RASTREABILIDADE.md): requisitos ↔ artefactos ↔ testes
- [docs/DIARIO.md](docs/DIARIO.md): diário de desenvolvimento por fase (matéria-prima do Capítulo 5)
- [docs/adr/](docs/adr/): registos de decisões de arquitetura (ADR-0001 a ADR-0011)
- [docs/revisoes/](docs/revisoes/): revisões independentes de cada fase

## Resultados

Cada experiência escreve um CSV em `results/<cenario>/<AAAA-MM-DD_HHMM>.csv`. Por agora só existem os ensaios piloto do UC-05 ([results/schema-evolution-pilot/](results/schema-evolution-pilot/)); a interpretação de cada ficheiro está no diário da Fase 7. As medições oficiais dos Capítulos 5 e 6 são feitas na Fase 9.

## Licença

[MIT](LICENSE) © 2026 Diogo Nunes
