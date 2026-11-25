# Rastreabilidade dos requisitos

Liga cada requisito funcional (RF, Tabela 13 da tese) e não funcional (RNF, Tabela 14) ao caso de uso (UC, Tabela 12), aos critérios de aceitação (`AC-n`) da especificação, ao código e aos testes que o demonstram. Deve bater certo com a Tabela 17 da tese.

## Referência: casos de uso e requisitos da tese

Resumo das Tabelas 12, 13 e 14, para consulta durante o `/spec` e o `/plan`. Os alvos da Tabela 14 são requisitos a verificar, não resultados: os valores medidos ficam só em `results/`.

### Casos de uso (Tabela 12)

| UC | Nome | Em resumo |
|---|---|---|
| UC-01 | Ingestão de eventos de ocupação | O sensor envia eventos (em lote), que são validados, deduplicados por `eventId`, gravados no MongoDB (só por acréscimo) e refletidos no Redis. Latência ponta a ponta < 1 s no p95. |
| UC-02 | Sessão de estacionamento e pagamento | Abrir, fechar e pagar a sessão no PostgreSQL, numa transação ACID com concorrência otimista e evento na *outbox*. |
| UC-03 | Consulta de disponibilidade | Lugares livres por localização, raio e tipo de viatura, lidos só do Redis (GEO e *hashes*). Latência < 200 ms no p95. |
| UC-04 | Relatório analítico de ocupação | Por zona e período, com granularidade de hora, dia, semana ou mês. Lê das tabelas de resumo, nunca das operacionais. |
| UC-05 | Evolução do esquema dos sensores | Uma nova geração de sensores traz `temperature`, `airQuality` e `noise` no `payload`, sem migração síncrona e sem paragem. |

### Requisitos funcionais (Tabela 13)

| RF | Requisito | Ligado a | Prioridade (D9) |
|---|---|---|---|
| RF-01 | Receber e validar eventos de ocupação: esquema mínimo, idempotência e `payload` extensível | UC-01, UC-05 | Alta |
| RF-02 | Ciclo de vida das sessões: cada mudança de estado da sessão (aberta, fechada, paga) é atómica; uma falha a meio do pagamento não deixa a sessão num estado intermédio; as atualizações concorrentes da mesma sessão são detetadas | UC-02 | Alta |
| RF-03 | Manter e disponibilizar o estado de ocupação em tempo real | UC-03 | Alta |
| RF-04 | Pesquisa geoespacial de lugares disponíveis | UC-03 | Média |
| RF-05 | Produzir agregados históricos e relatórios analíticos | UC-04 | Média |
| RF-06 | Configuração: CRUD de parques, lugares, sensores e tarifários | UC-02, UC-05 | Alta |

### Requisitos não funcionais (Tabela 14)

| RNF | Requisito | Alvo / verificação | Prioridade (D9) |
|---|---|---|---|
| RNF-01 | Desempenho (latência) | UC-03 < 200 ms p95; UC-01 < 1 s p95; UC-02 < 2 s p95 | Alta |
| RNF-02 | Escalabilidade | Débito de ingestão com 1, 2 e 4 consumidores a crescer de forma próxima da linear | Alta |
| RNF-03 | Disponibilidade / tolerância a falhas | Falha de uma réplica sem perda de eventos nem de transações confirmadas | Média |
| RNF-04 | Evolução do esquema | 0 s de indisponibilidade durante a alteração | Alta |
| RNF-05 | Sustentabilidade / ciclo de vida dos dados | Armazenamento e CPU por 10⁶ eventos; redução mensurável com TTL, compressão e arquivo | Média |
| RNF-06 | Segurança e proteção de dados (RGPD) | Cifragem em trânsito e em repouso, controlo de acesso baseado em papéis (RBAC) por ator e auditoria só por acréscimo, verificados por testes de integração | Média |

## Matriz de rastreabilidade

Estado: «por preencher» até existir uma especificação aprovada que cubra o requisito. A coluna «UC» segue a coluna «Ligado a» da Tabela 13 e, nos RNF, os UC que a Tabela 14 nomeia; «—» quer dizer que a Tabela 14 não liga o requisito a um UC. A coluna «Especificação» indica a especificação reservada e a fase (ordem em [`docs/specs/README.md`](specs/README.md)).

| Requisito | UC | AC-n | Especificação | Código | Teste | Estado |
|---|---|---|---|---|---|---|
| RF-01 | UC-01, UC-05 | AC-1 a AC-18 (F4a), AC-19 a AC-24 (F4b) de 003-ingestao | 003-ingestao (F4a, F4b; aprovada a 2026-11-25); 007-simulador-esquema (F8, `payload` extensível) | por preencher | por preencher | por preencher |
| RF-02 | UC-02 | AC-1 a AC-30 | 002-sessoes-pagamentos (F3a, F3b; aprovada a 2026-11-25); planos [002a](plans/002a-utilizadores-sessoes-pagamento.md) e [002b](plans/002b-reservas-worker.md); [ADR 0006](adr/0006-auditoria-por-entidade-alterada.md) e [ADR 0007](adr/0007-reservas-cumprimento-e-expiracao.md) | `Domain/{Users,Sessions}` (`ParkingSession`, `SessionPricing`, `Payment`, `Reservation`, `ReservationPolicy`), `Domain/Facilities/ParkingFacility` (`IsOpenAt`, `FitsInOneOpeningInterval`), `Application/Features/{Users,Sessions,Reservations}`, `Infrastructure/Relational` (unidade de trabalho por entidade alterada, migração `OpenSessionPerVehicle`, repositórios e leitores), `Infrastructure/{Payments,ReservationOptions}`, `Api/Features/{Users,Sessions,Reservations}`, `Workers/Reservations` (*worker* de expiração), `tests/http/sessions.http` | `Api.Tests/Features/{Users,Sessions,Reservations}` (incluindo `Sessions/OpenSessionWithReservationTests`, AC-25 e AC-26, e `Reservations/ReservationConcurrencyTests`, AC-27), `Api.Tests/Features/Schema/OpenSessionPerVehicleIndexTests` (AC-1), `Api.Tests/Features/HttpFiles/HttpFileTests` (AC-20); `Workers.Tests/Features/Reservations` (AC-28 e a expiração do AC-21); `UnitTests/Domain/{SessionPricingTests,ParkingSessionTests,ReservationTests,ReservationPolicyTests,OpeningHoursTests}` (AC-8, AC-15), `UnitTests/Infrastructure/OpenSessionPerVehicleMigrationTests` (AC-1) | F3a e F3b a passar (2026-11-25) |
| RF-03 | UC-03 | por preencher | 004-disponibilidade (F5) | por preencher | por preencher | por preencher |
| RF-04 | UC-03 | por preencher | 004-disponibilidade (F5) | por preencher | por preencher | por preencher |
| RF-05 | UC-04 | por preencher | 006-analitica (F7) | por preencher | por preencher | por preencher |
| RF-06 | UC-02, UC-05 | AC-1 a AC-6, AC-12, AC-17, AC-18, AC-21, AC-22 (F2a); AC-7 a AC-9, AC-11, AC-13 a AC-16, AC-17 (tarifários), AC-18 (lugares), AC-23 (F2b) | 001-configuracao (F2a, F2b); planos [001a](plans/001a-dominio-esquema-parques.md) e [001b](plans/001b-zonas-lugares-sensores-tarifarios.md); [ADR 0005](adr/0005-versoes-de-tarifario-com-vigencia.md) | `Domain/Facilities`, `Application/Features/{Facilities,Zones,Spots,Sensors,Tariffs}`, `Infrastructure/Relational` (modelo, migração `Initial`, unidade de trabalho, repositórios e leitores), `Api/Features/{Facilities,Zones,Spots,Sensors,Tariffs}`, `tests/http/configuration.http` | `Api.Tests/Features/{Facilities,Zones,Spots,Sensors,Tariffs}`, `Api.Tests/Features/Schema/InitialMigrationTests`, `Api.Tests/Features/Configuration/PersistenceModeTests` (AC-22), `Api.Tests/Features/HttpFiles/HttpFileTests` (AC-23); `ArchitectureTests/Features/RepositoryRulesTests`, `DomainEventRulesTests` (AC-21) | a passar (F2a e F2b, 2026-11-25) |
| RNF-01 | UC-01, UC-02, UC-03 | por preencher | 005-sincronizacao (F6, UC-01); 011-medicoes (F12, os três UC) | por preencher | por preencher | por preencher |
| RNF-02 | — | por preencher | 011-medicoes (F12) | por preencher | por preencher | por preencher |
| RNF-03 | — | por preencher | 011-medicoes (F12) | por preencher | por preencher | por preencher |
| RNF-04 | UC-05 | por preencher | 007-simulador-esquema (F8) | por preencher | por preencher | por preencher |
| RNF-05 | — | por preencher | 009-ciclo-de-vida (F10) | por preencher | por preencher | por preencher |
| RNF-06 | — | AC-10 e AC-8 de 001-configuracao (auditoria só por acréscimo e na mesma transação); AC-4, AC-12, AC-14, AC-28 e AC-29 de 002-sessoes-pagamentos; restantes por preencher | 001-configuracao (F2a, auditoria; [ADR 0004](adr/0004-papel-da-aplicacao-e-auditoria.md)); 010-seguranca (F11, cifra e RBAC) | `Infrastructure/Relational/Migrations/Sql/0001_app_role.sql`, `Infrastructure/Relational/SmartParkingDbContext.cs`, `scripts/db-app-user.sh` (logins da Api e dos Workers), `Workers/Hosting/SystemCurrentActor.cs` | `Api.Tests/Features/Schema/AppRolePermissionsTests`; `Api.Tests/Features/Tariffs/TariffAtomicityTests` (AC-8: a falha da *outbox* reverte o negócio e a auditoria); de 002-sessoes-pagamentos: `Sessions/PaySessionTests` (AC-12: uma auditoria por entidade alterada, na mesma transação), `Users/PaymentMethodTests` (AC-4: o `providerToken` fora da auditoria e da *outbox*), `Sessions/PaymentAtomicityTests` (AC-14), `Workers.Tests/Features/Reservations/ReservationExpiryTests` (AC-28: auditoria com actor `system`), `Workers.Tests/Features/Configuration/WorkersAppLoginTests` (AC-29: os Workers ligam como papel da aplicação) | parcial: auditoria a passar (F2a, 2026-11-25); cifra e RBAC na F11 |

### Critérios transversais (sem RF/RNF próprio)

A especificação 000 (F1, aprovada a 2026-11-24) não responde a nenhum RF ou RNF da tese: prepara a fundação comum (OE6, secção 5.9.1). Os seus critérios ficam aqui, fora da matriz, para não inflacionar a Tabela 17. O mesmo vale para os critérios da especificação 001 que não respondem ao RF-06 nem ao RNF-06: o AC-19 prepara o RF-02, mas o RF-02 só liga à F3.

| Especificação | AC-n | Tema | Código | Teste | Estado |
|---|---|---|---|---|---|
| 000-visao-geral (F1) | AC-1 a AC-3 | Regras de arquitetura (camadas, subcamadas E1, D10) | `src/*` (dependências), `Infrastructure/{Relational,Documents,Cache}` | `ArchitectureTests/Features/LayerDependencyTests`, `SublayerIsolationTests` | a passar |
| 000-visao-geral (F1) | AC-4 a AC-9 | *Health checks* e `Persistence:Mode` | `Infrastructure/Hosting/HealthEndpoints.cs`, `Infrastructure/DependencyInjection.cs`, `PersistenceOptions.cs` | `Api.Tests/Features/Health`, `Configuration`; `Workers.Tests/Features` | a passar |
| 000-visao-geral (F1) | AC-10 a AC-13 | ProblemDetails, Result, validação, OpenAPI | `Infrastructure/Hosting/GlobalExceptionHandler.cs`, `Api/Common/`, `Api/Program.cs` | `Api.Tests/Features/Errors`, `Validation`, `OpenApi` | a passar |
| 000-visao-geral (F1) | AC-14 | Imagens dos Testcontainers iguais às do `compose.yaml` | `tests/Common/ContainerImages.cs` | `ArchitectureTests/Features/ContainerImagesTests` | a passar |
| 000-visao-geral (F1) | AC-15, AC-16 | Documentais: ADR 0003 e compilação limpa | `docs/adr/0003-repository-por-raiz-de-agregado.md` | `dotnet build -warnaserror` e `dotnet format --verify-no-changes` corridos à mão (2026-11-24); `/verify` por correr | verificado à mão |
| 001-configuracao (F2a) | AC-19, AC-20 | Restrições do agregado de sessão; erros 412/428 | `Infrastructure/Relational/Configurations/Sessions/`, migração `Initial` (`EXCLUDE`), `Api/Common/ResultExtensions.cs` | `Api.Tests/Features/Schema/SessionConstraintsTests`, `Api.Tests/Features/Errors/ErrorHandlingTests` | a passar (2026-11-25) |
