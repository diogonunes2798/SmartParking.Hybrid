# Plano 000 — Fundação da solução (F1)

- **Especificação:** [000-visao-geral](../specs/000-visao-geral.md) (aprovada a 2026-11-24)
- **Estado:** Implementado (2026-11-24), ramo `feat/000-fundacao`
- **Requisitos:** transversal (OE6, secção 5.9.1); pontos de extensão de D2 e D10

## Decisões tomadas no planeamento (Diogo, 2026-11-24)

- Prevalece a especificação 000 sobre o pedido inicial (`/health/live` e `/health/ready` em vez de `/health`; Workers sem `BackgroundService`; quatro projetos de teste). Acrescenta-se o ADR 0002 do pedido inicial.
- Pacotes fora da lista inicial autorizados: `Microsoft.AspNetCore.OpenApi`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.DependencyInjection.Abstractions` (Application). `Microsoft.NET.Test.Sdk` e `xunit.runner.visualstudio` também foram autorizados, mas saíram durante a implementação (ver diário).
- Os *logs* chegam ao OTLP pelo Serilog (`writeToProviders`) e pelo *logging* do OpenTelemetry, sem `Serilog.Sinks.OpenTelemetry`.
- Fundação web partilhada em `Infrastructure/Hosting/` (ADR 0002).

## Passos

1. Raiz: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `SmartParking.slnx`.
2. Domain (vazio) e Application (Result, Error, *handlers*, `IEventBus`, `IUnitOfWork`, `AddApplication`).
3. Infrastructure: `PersistenceOptions`, `AddInfrastructure`, uma subpasta por subcamada com a ligação e o *health check*.
4. `Infrastructure/Hosting/`: `IEndpointGroup`, `MapEndpoints`, `HealthEndpoints`, `GlobalExceptionHandler`, Serilog e OpenTelemetry.
5. Api (`Program.cs` mínimo, `ResultExtensions`, `ValidationFilter`, OpenAPI e Scalar só em `Development`).
6. Workers (`Microsoft.NET.Sdk.Web`, mesmo `Program.cs` sem OpenAPI).
7. Testes: ficheiros partilhados em `tests/Common/` (imagens, contentores, *factory*), ArchitectureTests, Api.Tests, Workers.Tests e UnitTests.
8. Verificação: `dotnet build -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test`, ferramentas Roslyn, pedido manual a `/health/ready`.
9. Documentação: ADR 0002 e 0003, este plano, rastreabilidade e diário.

## Correspondência AC → teste

| AC | Teste |
|---|---|
| AC-1 | `ArchitectureTests/Features/LayerDependencyTests` (`DomainAssembly_…_AC1`, `ApplicationAssembly_…_AC1`) |
| AC-2 | `ArchitectureTests/Features/SublayerIsolationTests` (`…_AC2`) |
| AC-3 | `ArchitectureTests/Features/LayerDependencyTests.ApplicationAssembly_Inspected_UsesNoPersistenceLibrary_AC3` |
| AC-4 | `Api.Tests/Features/Health/HealthEndpointsTests.GetLive_NoDatabaseReachable_Returns200_AC4` |
| AC-5 | `…HealthEndpointsTests.GetReady_HybridModeWithAllDatabases_…_AC5` |
| AC-6 | `…HealthEndpointsTests.GetReady_MonolithicMode_…_AC6` |
| AC-7 | `…HealthEndpointsTests.GetReady_HybridModeWithRedisStopped_…_AC7` |
| AC-8 | `Workers.Tests/Features/Health/WorkersHealthTests` (`…_AC8`) |
| AC-9 | `Api.Tests/Features/Configuration/PersistenceModeTests` e `Workers.Tests/Features/Configuration/WorkersPersistenceModeTests` (`…_AC9`) |
| AC-10 | `Api.Tests/Features/Errors/ErrorHandlingTests.UnhandledException_…_AC10` |
| AC-11 | `…ErrorHandlingTests.ErrorToProblem_EachErrorType_…_AC11` |
| AC-12 | `Api.Tests/Features/Validation/ValidationFilterTests` (`…_AC12`) |
| AC-13 | `Api.Tests/Features/OpenApi/OpenApiTests` (`…_AC13`) |
| AC-14 | `ArchitectureTests/Features/ContainerImagesTests` (`…_AC14`) |
| AC-15 | documental: `docs/adr/0003-repository-por-raiz-de-agregado.md` e índice dos ADR |
| AC-16 | documental: `dotnet build SmartParking.slnx -warnaserror` e `dotnet format --verify-no-changes` (`/verify`) |
