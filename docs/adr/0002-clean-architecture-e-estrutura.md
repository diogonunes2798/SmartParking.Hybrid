# ADR 0002 — Clean Architecture e estrutura da solução

- **Estado:** Proposto
- **Data:** 2026-11-24
- **Requisitos:** transversal (OE6); prepara a comparação do Cap. 7.5 (modo monolítico)
- **Decisão (D1–D10):** nenhuma diferida; aplica E1 (um só projeto de infraestrutura) e o ponto de extensão de D2 e D10.
- **Secção da tese:** 5.9.1.
- **Especificação:** [000-visao-geral](../specs/000-visao-geral.md) (F1).

## Contexto

O protótipo tem de servir duas implementações de persistência (híbrida e monolítica) atrás das mesmas portas, para que a comparação do Cap. 7.5 meça só a persistência e não diferenças de código de aplicação. Também precisa de uma fundação comum (erros, *logs*, telemetria, *health checks*, testes de integração) que as fases F2–F13 reutilizem sem a alterar. A arquitetura (Clean Architecture com pastas por funcionalidade em Application) e a estrutura E1 foram decididas antes desta fase e estão no `CLAUDE.md`; este ADR regista a forma concreta que tomaram na F1 e os desvios.

## Decisão

- Cinco projetos em `src/`: `SmartParking.Domain` (sem dependências), `SmartParking.Application` (depende só de Domain), `SmartParking.Infrastructure`, `SmartParking.Api` e `SmartParking.Workers`. Dependências só para dentro.
- Application contém as abstrações transversais: `Result`/`Result<T>` e `Error` tipado (`NotFound`, `Validation`, `Conflict`, `Unauthorized`), `ICommandHandler<TCommand, TResult>`, `IQueryHandler<TQuery, TResult>`, `IEventBus` (D2) e `IUnitOfWork` (D10). O registo por DI faz-se por varrimento do *assembly*, sem biblioteca de mediação. Application usa só `FluentValidation` e `Microsoft.Extensions.DependencyInjection.Abstractions`.
- Infrastructure é um só projeto (E1), com uma subpasta por subcamada: `Relational/` (PostgreSQL), `Documents/` (MongoDB), `Cache/` (Redis) e `Messaging/` (barramento). `AddInfrastructure` lê `Persistence:Mode`, valida-o no arranque e chama um método de registo por modo. Na F1 só regista as ligações e os *health checks*: `postgres` nos dois modos, `mongo` e `redis` só no modo `Hybrid`.
- **Desvio à estrutura do `CLAUDE.md`:** uma quinta subpasta, `Infrastructure/Hosting/`, guarda a fundação web partilhada pela Api e pelos Workers: `IEndpointGroup` e `MapEndpoints()`, os *endpoints* `/health/live` e `/health/ready`, o tratamento global de exceções com ProblemDetails, o Serilog e o OpenTelemetry. Por isso, Infrastructure tem uma `FrameworkReference` a `Microsoft.AspNetCore.App`. Aprovado pelo Diogo a 2026-11-24.
- O isolamento é verificado por testes de arquitetura (`tests/SmartParking.ArchitectureTests`): Relational não usa MongoDB, Redis nem Hosting; Documents e Cache não usam EF Core, a outra subcamada, Relational nem Hosting; Application não usa nenhuma biblioteca de persistência.

## Alternativas consideradas

- **Duplicar a fundação web na Api e nos Workers:** cumpre à letra as quatro subpastas, mas são cerca de cinco ficheiros copiados que podem divergir. Uma diferença nos *health checks* ou na telemetria entre os dois *hosts* afetaria as medições do Cap. 7.
- **Projeto `SmartParking.ServiceDefaults`** (ao estilo do .NET Aspire): separa melhor a fundação web, mas acrescenta um projeto fora da estrutura do `CLAUDE.md`.
- **Um projeto de infraestrutura por subcamada:** isolamento garantido pelo compilador, mas mais projetos e referências; foi rejeitado na decisão E1, e os testes de arquitetura dão a mesma garantia.
- **Vertical Slice sem camadas:** menos cerimónia, mas não separa as portas das implementações, de que o modo monolítico precisa.

## Consequências

- As fases seguintes acrescentam Features em Application, implementações de portas na subpasta da subcamada e um `IEndpointGroup` por funcionalidade na Api; `Program.cs` não muda.
- A ASP.NET Core fica disponível para todo o *assembly* de Infrastructure; só os testes de arquitetura impedem que as subcamadas de persistência a usem.
- O `Persistence:Mode` inválido ou ausente impede o arranque (`ValidateOnStart`), o que evita medir o modo errado por engano.
- A F1 não inclui EF Core: entra na F2 com o primeiro `DbContext`.
