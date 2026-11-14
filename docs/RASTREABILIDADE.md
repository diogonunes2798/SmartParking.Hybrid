# Rastreabilidade — requisitos ↔ artefactos ↔ testes

Liga cada caso de uso (UC), requisito funcional (RF) e requisito não funcional (RNF) da `ESPECIFICACAO.md` ao código que lhe responde e ao teste ou medição que o comprova. É atualizada no fim de cada fase (§9).

Legenda do estado:
- **Base:** a infraestrutura de que o requisito depende existe, mas o requisito ainda não está implementado.
- **Parcial:** o requisito está parcialmente implementado.
- **Concluído:** o requisito está implementado e verificado.
- **—:** ainda não iniciado.

## Requisitos

| Requisito | Fase | Estado | Artefactos | Teste / medição |
|---|---|---|---|---|
| UC-01 / RF-01 — Ingestão de ocupação | 3 | — | — | — |
| UC-02 / RF-02 — Sessão e pagamento | 2 | — | — | — |
| UC-03 / RF-03 — Consulta de disponibilidade | 4 | — | — | — |
| UC-04 / RF-05 — Relatório analítico | 6 | — | — | — |
| UC-05 — Evolução do esquema | 7 | — | — | — |
| RF-06 — Configuração (CRUD) | 2 | — | — | — |
| RNF-01 — Latência (p95) | 1 → 9 | Base | `ServiceDefaults/ServiceDefaultsExtensions.cs` (OpenTelemetry, `/metrics`, *buckets* explícitos, sondas excluídas do histograma, `service.version` com o commit), `Application/Diagnostics/SmartParkingTelemetry.cs` | `HealthAndTelemetryTests`: `Metrics_endpoint_exposes_http_server_metrics_with_service_resource`, `Probes_and_scrapes_are_excluded_from_latency_histograms`, `Latency_histogram_uses_fine_buckets_around_the_200_ms_target`; recolha no Prometheus verificada manualmente (`up=1` para a API e os Workers) |
| RNF-02 — Throughput ao escalar consumidores | 1 → 9 | Base | Métricas de *runtime* e HTTP nos Workers (`/metrics` na porta 5090) | Verificação manual da recolha no Prometheus |
| RNF-03 — Falha de um nó | 1 → 9 | Base | Separação `/health/live` e `/health/ready`; `Infrastructure.Cache/RedisHealthCheck.cs` e `RedisConnectionWarmup.cs`; *timeouts* de 3 s; Redis com AOF no compose | `ReadinessFailureTests`: `Readiness_reports_the_engine_that_stops_and_liveness_stays_healthy` (PostgreSQL, MongoDB, Redis) e `Api_starts_and_reports_unready_when_redis_is_down_at_boot` |
| RNF-04 — Indisponibilidade na alteração de esquema | 7 | — | — | — |
| RNF-05 — Armazenamento, CPU e retenção | 10 | — | — | — |
| RNF-06 — Segurança e RGPD | 2 → | — | Detalhes de *health* só em Development (não expõem a topologia) | — |

## Requisitos transversais (CLAUDE.md, Cap. 4)

| Requisito | Fase | Artefactos | Teste / medição |
|---|---|---|---|
| Regra de dependências e isolamento da persistência (Cap. 4.1, 4.6; ADR-0001) | 1 | `src/*` (oito projetos + ServiceDefaults) | `ProjectGraphTests` (grafo de `.csproj`, 8 testes) e `LayerDependencyTests` (NetArchTest, 10 testes) |
| Uma subcamada de persistência por motor (Cap. 3.5, tabela de mapeamento) | 1 | `Infrastructure.Relational`, `.Documents`, `.Cache`, `.Messaging` | `ProjectGraphTests.Project_references_only_what_its_layer_allows`, `LayerDependencyTests.Infrastructure_sub_layers_are_independent_of_each_other_and_of_the_hosts` |
| Infraestrutura local reprodutível | 1 | `docker-compose.yml` (imagens fixadas por `tag@sha256`), `global.json` (`latestPatch`), `Directory.Packages.props` | `ComposeConsistencyTests` (o *fixture* usa as mesmas imagens que o compose) |
| Integração com os três motores | 1 | `DependencyInjection.cs` de cada subcamada | `HealthAndTelemetryTests.Readiness_is_healthy_when_all_storage_engines_are_up` |
| Documentação da API (Swagger/OpenAPI, §10) | 1 | `Api/Program.cs` | `HealthAndTelemetryTests.OpenApi_document_is_served_outside_production` |
| CQRS: os *endpoints* passam pelos *handlers* e não usam os *drivers* (§5, ADR-0001) | 1 | `Api/Program.cs` | `LayerDependencyTests.Api_does_not_use_storage_drivers_directly` |

## Decisões em aberto (D1–D8) tocadas

| Decisão | Fase | Como foi tratada |
|---|---|---|
| D2 — Broker | 1 | Sem contentor de broker: com o valor por omissão (Outbox e Redis Streams) basta o Redis. O projeto `Infrastructure.Messaging` está criado e vazio. O `IEventBus` entra na Fase 3. |
| D5 — Cosmos DB | 1 | Não há contentor nem dependência. |
