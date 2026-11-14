# CLAUDE.md — Protótipo da arquitetura híbrida de persistência (SmartParking.Hybrid)

Este repositório contém o protótipo da dissertação de mestrado de Diogo Barbosa Nunes, «Arquitetura Híbrida de Persistência de Dados para a Gestão Sustentável de um Espaço Urbano» (ISEC, Departamento de Informática e Sistemas; orientador: Prof. Francisco Barros Leite).

O protótipo é o artefacto da metodologia Design Science Research (DSRM, Peffers et al., 2007). Serve para demonstrar a arquitetura proposta no Capítulo 4 e para produzir as medições dos Capítulos 5 e 6. Cada decisão técnica tem de poder ser justificada na tese. Por isso, documenta-se o porquê e não só o como.

A especificação completa está em `docs/ESPECIFICACAO.md`. Lê-a antes de qualquer tarefa.

## Regras de trabalho

- O código, os nomes de classes, os testes e as mensagens de commit são escritos em inglês.
- A documentação em `docs/` e as notas para a tese são escritas em português europeu (PT-PT). Por exemplo: «utilizador», «ficheiro», «registo», «desempenho».
- Antes de implementar uma funcionalidade, confirma a que caso de uso (UC-xx), requisito funcional (RF-xx) ou requisito não funcional (RNF-xx) da especificação ela responde. Regista essa ligação em `docs/RASTREABILIDADE.md`.
- Não alteres as decisões marcadas como «EM ABERTO» na especificação sem perguntar ao Diogo. Usa o valor por omissão indicado e deixa o ponto de extensão preparado.
- Cada experiência tem de ser reprodutível: um comando, uma configuração versionada e um ficheiro de resultados (CSV) em `results/`.
- Não inventes resultados, valores de referência nem citações. Se faltar um dado, escreve `TODO(Diogo):` e pergunta.
- Mantém os commits pequenos e com uma mensagem descritiva.

## Stack

- .NET 10 (LTS), ASP.NET Core Minimal APIs e C#.
- PostgreSQL 16 ou superior com EF Core e Npgsql (subcamada relacional).
- MongoDB 7 ou superior com MongoDB.Driver (subcamada documental).
- Redis 7 ou superior com StackExchange.Redis (estado de disponibilidade em memória e pub/sub).
- Mensagens: ver a decisão D2 na especificação.
- Testes: xUnit e Testcontainers. Carga: k6 ou NBomber (decisão D6).
- Observabilidade: OpenTelemetry (métricas e traces) com exportação para Prometheus ou para CSV.
- Infraestrutura local: `docker compose`.

## Comandos (a manter atualizados)

```bash
docker compose up -d                 # PostgreSQL, MongoDB, Redis (+ broker, se ativo)
docker compose --profile observability up -d   # + Prometheus em http://localhost:9090
dotnet build
dotnet test                          # Microsoft.Testing.Platform (global.json); precisa do Docker (Testcontainers)
dotnet test --project tests/SmartParking.ArchitectureTests   # um único projeto de testes
dotnet run --project src/SmartParking.Api        # http://localhost:5080/swagger, /health/ready, /metrics
dotnet run --project src/SmartParking.Workers    # http://localhost:5090/health/ready, /metrics
dotnet run --project tools/SensorSimulator -- --sensors 5000 --rate 200
./bench/run.sh <cenario>             # gera results/<cenario>/<data>.csv
```

## Estrutura da solução

```
src/
  SmartParking.Domain/            entidades, eventos de domínio, regras (sem dependências externas)
  SmartParking.Application/       comandos, consultas (CQRS), interfaces de repositório
  SmartParking.Infrastructure.Relational/   EF Core + PostgreSQL, outbox
  SmartParking.Infrastructure.Documents/    MongoDB (eventos de ocupação, dados de sensores)
  SmartParking.Infrastructure.Cache/        Redis (read model de disponibilidade, pub/sub)
  SmartParking.Infrastructure.Messaging/    publicação/consumo de eventos (D2)
  SmartParking.Api/               endpoints HTTP e ingestão
  SmartParking.Workers/           projeções, sincronização, retenção (RNF-05)
  SmartParking.ServiceDefaults/   OpenTelemetry e health checks partilhados pela Api e pelos Workers (ADR-0001)
tests/
  SmartParking.ArchitectureTests/       regra de dependências (NetArchTest)
  SmartParking.Api.IntegrationTests/    WebApplicationFactory + Testcontainers (PostgreSQL, MongoDB, Redis)
infra/                            configuração de serviços auxiliares (Prometheus)
tools/
  SensorSimulator/                gerador de eventos de sensores (UC-01, UC-05)
bench/                            cenários de carga e scripts de medição
baseline/                         variante monolítica para comparação (Cap. 6.5)
docs/                             especificação, rastreabilidade, decisões (ADR)
results/                          resultados das experiências (CSV) — versionados
```

## Decisões de arquitetura

Regista cada decisão relevante como ADR em `docs/adr/NNNN-titulo.md`, com contexto, decisão, alternativas e consequências. Estes registos vão alimentar diretamente o Capítulo 4 e a secção 6.4.
