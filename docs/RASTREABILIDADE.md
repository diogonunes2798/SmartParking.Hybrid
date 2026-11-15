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
| UC-02 / RF-02 — Sessão e pagamento | 2a → 2b | Base | `Domain/Sessions/ParkingSession.cs` (máquina de estados Open → Closed → Paid), `Domain/Sessions/Payment.cs`, `Application/Abstractions/Persistence.cs` (`IParkingSessionRepository`, `IUnitOfWork`), `Relational/Persistence/EfUnitOfWork.cs` (`EfParkingSessionRepository`), índice único parcial `ux_parking_sessions_spot_id_open`. Os *endpoints* e a outbox ficam para a Fase 2b. | `ParkingSessionRepositoryTests`: `Session_lifecycle_is_persisted_and_the_open_session_of_a_spot_can_be_found`, `Second_open_session_on_the_same_spot_is_rejected_by_the_database`, `Concurrent_modification_of_a_session_raises_a_concurrency_conflict` |
| UC-03 / RF-03 — Consulta de disponibilidade | 4 | — | — | — |
| UC-04 / RF-05 — Relatório analítico | 6 | — | — | — |
| UC-05 — Evolução do esquema | 7 | — | — | — |
| RF-06 — Configuração (CRUD) | 2a | Parcial | `Domain/Facilities/*`, `Domain/Tariffs/Tariff.cs`, `Application/Configuration/*` (comandos e `IConfigurationQueries`), `Relational/Configurations/*`, `Relational/Persistence/EfConfigurationQueries.cs`, `Relational/Migrations/*_InitialCreate.cs`, `Api/Endpoints/ConfigurationEndpoints.cs`, `Api/Endpoints/ProblemDetailsExceptionHandler.cs`; ADR-0003. Falta emitir eventos de configuração para o *read model* (depende da outbox, Fase 2b/5). | `ConfigurationEndpointsTests` (25 testes: CRUD dos quatro recursos; 400 com ProblemDetails para valores inválidos, corpo ilegível, campos obrigatórios em falta, caracteres de controlo e instantes abaixo do microssegundo; 404; 409 por concorrência, duplicado e dependentes); `MigrationTests` (3); teste manual por HTTP com o compose (diário da Fase 2a) |
| RNF-01 — Latência (p95) | 1 → 9 | Base | `ServiceDefaults/ServiceDefaultsExtensions.cs` (OpenTelemetry, `/metrics`, *buckets* explícitos, sondas excluídas do histograma, `service.version` com o commit), `Application/Diagnostics/SmartParkingTelemetry.cs` | `HealthAndTelemetryTests`: `Metrics_endpoint_exposes_http_server_metrics_with_service_resource`, `Probes_and_scrapes_are_excluded_from_latency_histograms`, `Latency_histogram_uses_fine_buckets_around_the_200_ms_target`; recolha no Prometheus verificada manualmente (`up=1` para a API e os Workers) |
| RNF-02 — Throughput ao escalar consumidores | 1 → 9 | Base | Métricas de *runtime* e HTTP nos Workers (`/metrics` na porta 5090) | Verificação manual da recolha no Prometheus |
| RNF-03 — Falha de um nó | 1 → 9 | Base | Separação `/health/live` e `/health/ready`; `Infrastructure.Cache/RedisHealthCheck.cs` e `RedisConnectionWarmup.cs`; *timeouts* de 3 s; Redis com AOF no compose | `ReadinessFailureTests`: `Readiness_reports_the_engine_that_stops_and_liveness_stays_healthy` (PostgreSQL, MongoDB, Redis) e `Api_starts_and_reports_unready_when_redis_is_down_at_boot` |
| RNF-04 — Indisponibilidade na alteração de esquema | 7 | — | — | — |
| RNF-05 — Armazenamento, CPU e retenção | 10 | — | — | — |
| RNF-06 — Segurança e RGPD | 2a → | Parcial | Detalhes de *health* só em Development (não expõem a topologia). Auditoria: `Relational/Auditing/AuditSaveChangesInterceptor.cs`, `AuditEntry.cs` (tabela `audit_log`), `Domain/Common/PersonalDataAttribute.cs` (mascaramento), `ICurrentActor` (ator `anonymous` até haver RBAC). Faltam a cifra em trânsito e o RBAC. | `AuditTests`: `Creating_a_facility_two_spots_and_a_sensor_writes_one_audit_row_each`, `Update_records_only_changed_fields_with_old_and_new_values_and_delete_records_old_values`, `Failed_change_leaves_no_audit_row`, `Personal_data_is_masked_in_the_audit_log`, `Complex_and_json_properties_are_audited_with_dotted_names`; verificação manual em `psql` (4 linhas) |

## Requisitos transversais (CLAUDE.md, Cap. 4)

| Requisito | Fase | Artefactos | Teste / medição |
|---|---|---|---|
| Regra de dependências e isolamento da persistência (Cap. 4.1, 4.6; ADR-0001) | 1 | `src/*` (oito projetos + ServiceDefaults) | `ProjectGraphTests` (grafo de `.csproj`, 8 testes) e `LayerDependencyTests` (NetArchTest, 10 testes) |
| Uma subcamada de persistência por motor (Cap. 3.5, tabela de mapeamento) | 1 | `Infrastructure.Relational`, `.Documents`, `.Cache`, `.Messaging` | `ProjectGraphTests.Project_references_only_what_its_layer_allows`, `LayerDependencyTests.Infrastructure_sub_layers_are_independent_of_each_other_and_of_the_hosts` |
| Infraestrutura local reprodutível | 1 | `docker-compose.yml` (imagens fixadas por `tag@sha256`), `global.json` (`latestPatch`), `Directory.Packages.props` | `ComposeConsistencyTests` (o *fixture* usa as mesmas imagens que o compose) |
| Integração com os três motores | 1 | `DependencyInjection.cs` de cada subcamada | `HealthAndTelemetryTests.Readiness_is_healthy_when_all_storage_engines_are_up` |
| Documentação da API (Swagger/OpenAPI, §10) | 1 | `Api/Program.cs` | `HealthAndTelemetryTests.OpenApi_document_is_served_outside_production` |
| CQRS: os *endpoints* passam pelos *handlers* e não usam os *drivers* (§5, ADR-0001) | 1, 2a | `Api/Program.cs`, `Api/ApplicationRegistration.cs`; lado dos comandos em `Application/Configuration/ConfigurationCommands.cs`, lado das consultas em `IConfigurationQueries` (leituras sem *tracking*) | `LayerDependencyTests.Api_does_not_use_storage_drivers_directly` |
| Esquema relacional versionado e reprodutível | 2a | `Relational/Migrations/` (`InitialCreate`), manifesto local `dotnet-tools.json` (`dotnet-ef` 10.0.15) | `MigrationTests.Migrations_apply_to_a_new_database_and_roll_back_completely` (base de dados nova, `Up()` e `Down()`), `Model_has_no_changes_missing_from_the_migrations`, `Creates_the_expected_tables` |
| Localização compatível com Redis GEO (UC-03) | 2a | `Domain/Facilities/GeoPoint.cs`, `Application/Configuration/Dtos.cs` (`GeoJsonPoint`), restrições `CHECK` em `parking_facilities`; ADR-0003 | `ConfigurationEndpointsTests.Facility_location_is_geojson_longitude_first_and_stored_as_latitude_and_longitude_columns`, `Facility_with_coordinates_out_of_range_is_rejected` |
| Um só *pool* Npgsql e telemetria por motor (nota da revisão da Fase 1) | 2a | `Relational/DependencyInjection.cs` (`AddNpgsqlDataSource`), `ServiceDefaults` (fontes `Npgsql` e medidores `Npgsql`, `Microsoft.EntityFrameworkCore`) | — (verificação por inspeção; a decomposição da latência por motor é usada na Fase 9) |

## Decisões em aberto (D1–D8) tocadas

| Decisão | Fase | Como foi tratada |
|---|---|---|
| D2 — Broker | 1 | Sem contentor de broker: com o valor por omissão (Outbox e Redis Streams) basta o Redis. O projeto `Infrastructure.Messaging` está criado e vazio. O `IEventBus` entra na Fase 3. |
| D5 — Cosmos DB | 1 | Não há contentor nem dependência. |
| D1 — Sessões | 2a | Valor por omissão (relacional) atrás de `IParkingSessionRepository`; implementação `EfParkingSessionRepository`. Mudar para MongoDB exige uma nova implementação **e** uma gravação explícita no contrato (hoje as alterações só são gravadas pelo *change tracking* do EF), mais uma solução de atomicidade com a outbox. Ver o ADR-0003, ponto 5, e a revisão independente, achado 4. |

## Revisões independentes

| Fase | Relatório | Achados | Estado |
|---|---|---|---|
| 2a | `docs/revisoes/fase-2a-revisao-independente.md` | 7 (0 altos, 2 médios, 5 baixos) | 5 corrigidos com testes (1, 2, 3, 6, 7), 1 documentado (4), 1 adiado para a Fase 2b (5) |
