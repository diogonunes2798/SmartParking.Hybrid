# Fase 10b — Revisão final de toda a solução

Revisão independente feita por um subagente geral, só de leitura, a 18-11-2026. Incidiu sobre o ramo `phase-10b-final-review` (HEAD `aa76a1e`) e sobre as alterações em `bench/` ainda por fazer *commit* nesse momento (`compose.bench.yml` e leitura por `$collStats`). O subagente não compilou nem correu testes, Docker ou cenários. O que depende do comportamento em execução está marcado como «não verificado». O tratamento de cada achado está na secção final.

## Resumo

1. **A implementação está completa e bem testada** para os UC-01 a UC-05, o RF-06 e a variante monolítica (§7). As revisões por fase deixaram poucas pontas soltas no código.
2. **Falta o grosso das medições da tese.**
   - Dos seis cenários da §6, só o `retention` (RNF-05) foi medido oficialmente.
   - `availability-read`, `session-payment`, `ingest-scale`, `schema-evolution` e `failover` continuam por fazer.
   - Por isso, os RNF-01 a RNF-04 e a comparação 6.5 assentam só no `smoke` (2 execuções) e em pilotos.
3. **O RNF-05 está marcado como «Concluído», mas está parcial.**
   - O *stream* Redis e a outbox nunca são aparados nem purgados.
   - O espaço do *stream* não entra na medição.
   - Os CSV da Fase 10a têm `_free_bytes` = 0 errados.
4. **Há um enviesamento transversal nos cenários futuros.** O serviço de retenção está ligado por omissão também no ambiente `Benchmark`. No monolítico, cada execução faz `min("timestamp")` sem índice.
5. **As alterações por fazer *commit* estão bem orientadas,** mas ainda não são reprodutíveis enquanto não houver *commit*.

## Achados

| # | Severidade | Área | Achado | Evidência | Recomendação |
|---|---|---|---|---|---|
| 1 | alta | Cobertura / medição | Cinco dos seis cenários da §6 não existem. A §6 pede pelo menos 5 execuções por cenário. Os alvos do RNF-01 (UC-03 com p95 < 200 ms, UC-02 com p95 < 2 s, D7 < 1 s), o RNF-02, o RNF-03 e a medição oficial do RNF-04 não estão medidos. | `bench/README.md:70-74`; `bench/scenarios/` só tem `smoke.sh` e `retention.sh`; §6 | Planear uma Fase 9b com os cinco cenários antes de escrever os Cap. 6.2 e 6.5. Até lá, não usar o `smoke` nem os pilotos como resultados. |
| 2 | alta | Retenção / validade (RNF-05, 6.5) | O *stream* Redis e a outbox nunca são aparados nem purgados. Há `XADD` sem `MAXLEN` e nenhum `XTRIM`; a tabela `outbox_messages` só cresce. O espaço medido no híbrido exclui a cópia de cada evento no *stream*, o que favorece o híbrido na comparação de espaço. | `Messaging/RedisStreamsEventBus.cs:30`; ADR-0006 (:37, :65 «fica para a Fase 10»); ADR-0009 (:53, :145); revisão 2b, achado 2; `retention_stats.py` | Implementar `XTRIM MINID` e a purga da outbox (ADR novo ou adenda ao 0013), ou declarar a exclusão como limitação. Medir a memória do *stream*. Mudar o RNF-05 para «Parcial». |
| 3 | média | Resultados versionados | Nas linhas do híbrido dos CSV da Fase 10a, `storage_*_free_bytes` = 0 e `_used_bytes` = `_total_bytes`. | `results/retention/2026-11-17_1236.csv`, `_1540.csv`; `DIARIO.md:1594`, `:1619` | Não apagar os CSV. Anotar no README, no diário e no ADR-0013 que essas linhas são inválidas. |
| 4 | média | Transversal (retenção × *benchmarks*) | `Retention:Enabled` = `true` e `Interval` = 1 min por omissão, sem alteração em `appsettings.Benchmark.json`. Em todos os cenários exceto o `retention`, o serviço corre nas duas variantes. No monolítico, `GetEarliestTimestampAsync` faz `min("timestamp")` a cada partição sem índice que comece por `timestamp`. O agregador do UC-04 também corre a cada minuto. | `RetentionContracts.cs:19,44`; `Workers/Program.cs:149`; `MonolithicRawEventRetention.cs:30-43,179-185`; `AddMonolithicSchema.cs:47-59` | Desligar a retenção (e decidir sobre a agregação) em `appsettings.Benchmark.json` e ligá-las só nos cenários que as medem, ou registar a carga de fundo na coluna `configuracao`. |
| 5 | média | Reprodutibilidade | `bench/compose.bench.yml` não estava versionado, e o `meta.json` não inclui ficheiros novos no *diff*. | `git status`; `write_meta.py` | Fazer *commit* antes de qualquer sessão oficial. |
| 6 | média | Medição (*diff*) | `hybrid_storage_rows` exige `indexFreeStorageSize`, mas a sonda só confirmou `freeStorageSize`. **Não verificado.** | `retention_stats.py` | Confirmar ao vivo antes de uma sessão de 5 execuções. |
| 7 | média | UC-02 / consistência eventual | Os eventos de sessão e de pagamento são publicados em `smartparking:domain-events`, mas nada os consome. A janela de consistência só é medida para os eventos de configuração. A RASTREABILIDADE diz que têm consumidor desde a Fase 6, o que não é verdade. | `RASTREABILIDADE.md:16`; `Workers/Program.cs:94-126`; `OutboxRelay.cs:18` | Corrigir a RASTREABILIDADE. `TODO(Diogo)`: medir a janela do UC-02 só até ao *bus*, ou criar um consumidor? |
| 8 | baixa/média | Medição (*diff*) | Com o *override*, os contentores efémeros de `docker compose run mongo …` (contagens e `compact`) herdam `start_interval: 1s` e correm o `mongosh` do *healthcheck* de 1 em 1 s. **Não verificado.** | `count_events`; `retention.sh:240-242` | Criar serviços cliente com `healthcheck: disable: true`. |
| 9 | baixa | Medição (*diff*) | `start_interval` exige o Docker Engine 25 ou superior. Uma execução com mais de cerca de 1 h volta a ter um *healthcheck* dentro da janela. | `compose.bench.yml` | Validar a versão do Docker e documentar o limite de 1 h. |
| 10 | baixa | Documentação desatualizada | Há textos desatualizados: o README do *bench* (`stats()`, repouso com *healthchecks*), o ADR-0013 («30 s») e o cabeçalho do `retention.sh` (o parágrafo da 10b ficou inserido no meio da lista da Fase 10). O CLAUDE.md lista o `apply-retention` sem aviso sobre a guarda. | `bench/README.md:53-54`; ADR-0013 (:164, :174); `retention.sh:24-34` | Atualizar. |
| 11 | baixa | RASTREABILIDADE ↔ código | (a) `RetentionStorageTests`: o nome real é `MongoRetentionStorageTests` / `MonolithicDayPartitionTests`. (b) `Readiness_is_healthy_when_all_storage_engines_are_up`: o nome real é `…_of_the_variant_are_up`. (c) Faltam migrações na linha do esquema versionado. (d) As contagens de testes da §7 estão desatualizadas. (e) A contagem dos testes Python está desatualizada. (f) Falta a linha da Fase 10 na tabela de revisões. (g) A linha «D7 — Fase 3» está fora de ordem. | `RASTREABILIDADE.md` | Corrigir no fecho. |
| 12 | baixa | Especificação | O RF-04 não existe nem na especificação nem na RASTREABILIDADE. | `grep RF-04` | `TODO(Diogo)`: confirmar no Cap. 3. |
| 13 | baixa | Código morto | O *record* `Domain/Occupancy/OccupancyEvent` não é usado; o comentário ainda fala da Fase 3. | `OccupancyEvent.cs:11-16` | Remover, ou documentar que é o modelo conceptual do Cap. 3.5. |
| 14 | baixa | Rótulos dos CSV | A chave `compressor=` aparece duas vezes na coluna `configuracao` do híbrido. | `results/retention/2026-11-17_1236.csv` | Renomear uma das chaves. |
| 15 | baixa | Validade (ruído de fundo) | No monolítico, o `mongo` e o `redis` continuam a correr e competem pelo CPU. | `retention.sh:114-115` | Arrancar só o `postgres`, ou registar como ameaça à validade. |
| 16 | baixa | Estrutura e documentação | O CLAUDE.md descreve uma pasta `baseline/` que não existe e não lista `tests/SmartParking.SensorSimulator.Tests`. | CLAUDE.md | Atualizar. |
| 17 | info | Desenvolvimento | Com o `ArchivePath` relativo e `dotnet run`, o arquivo vai para `src/SmartParking.Workers/archive/`, que não está no `.gitignore`. | `RetentionContracts.cs:56`; `.gitignore` | Acrescentar `archive/` ao `.gitignore`. |
| 18 | info | Ainda em aberto de revisões anteriores | *Pool* com 4 Workers (9a, #5); 2b #3, #4 e #7; teste instável `Latency_histogram_…`; percentis da D7 pelo limite superior dos *buckets*; sem *health check* da idade do último `collMod`. | revisões 9a, 2b e 10 | Tratar nos cenários `ingest-scale` e `session-payment`. |
| 19 | info | Especificação | A especificação v0.1 ainda trata a D8 como «Confirmar com o Diogo», mas foi fechada a 16-11. | §2 | Subir a versão da especificação quando o Diogo o decidir. |
| 20 | info | Língua | O código, os testes e os *commits* estão em inglês, e `docs/` está em PT-PT. Não foi encontrado código de depuração esquecido. | `git grep` | — |

## Cobertura por requisito (estado real)

| Requisito | Estado real | Discrepância com a RASTREABILIDADE |
|---|---|---|
| UC-01 / RF-01 | Concluído; medição sob carga: Base | Falta o `ingest-scale` |
| UC-02 / RF-02 | Concluído; consistência eventual: Parcial | Os eventos de sessão não têm consumidor (achado 7). Falta o `session-payment` |
| UC-03 / RF-03 | Concluído; alvo p95: Base | Falta o `availability-read` |
| UC-04 / RF-05 | Concluído | — |
| UC-05 | Concluído (demonstração); medição: Parcial | Falta o `schema-evolution` com 5 execuções |
| RF-06 | Concluído | — |
| RF-04 | — | Não existe (achado 12) |
| RNF-01, RNF-02, RNF-03 | Base | — |
| RNF-04 | Parcial | — |
| RNF-05 | **Parcial** | Estava «Concluído» (achados 2 e 3) |
| RNF-06 | Parcial | — |
| §7 / 6.5 | Concluído (variante); comparação: Parcial | Contagens de testes desatualizadas |

## Lista de `TODO(Diogo)`

Ficam de fora as cópias em `bin/`. As linhas do diário e das revisões são citações históricas do mesmo ponto.

**A. Decisões D1–D8**
- **D1, sessões em NoSQL?**
  - ADR-0003:47 e ADR-0005:15
  - RASTREABILIDADE:57
  - DIARIO:281 e :427
- **D2, Kafka: justificação qualitativa ou medição?**
  - ADR-0006:51
  - RASTREABILIDADE:59
  - DIARIO:541
- **D4, taxa, `AllowedLateness`, limites, *heartbeats* e denominador**
  - ADR-0010:54, :76, :95, :133 e :137
  - `Application/Analytics/AnalyticsContracts.cs:5` e `:83`
  - `OccupancyReport.cs:26`
  - `Workers/appsettings.json:24`
  - RASTREABILIDADE:63
  - revisão 6 (:19, :22, :43, :49)
  - DIARIO:1036, :1047 e :1057
- **D7 e janela de consistência, valor único**
  - `ServiceDefaults/LatencyTargets.cs:30`
  - `Api/appsettings.json:26` e `Workers/appsettings.json:35`
  - `OutboxRelayTests.cs:228`
  - ADR-0009:86
  - revisão 5, lacunas (:14 e :44)
  - DIARIO:880 e :900
- **D8, número de parques (20)**
  - `bench/scenarios/d8-reference.json:4`
  - RASTREABILIDADE:64
  - DIARIO:1084 e :1212

**B. Regras de domínio (Cap. 3)**
- **Tarifário e regra de cálculo**
  - `Domain/Tariffs/ITariffPricing.cs:5` e `:16`
  - `Tariff.cs:69`
  - `TariffConfiguration.cs:24`
  - `SessionCommands.cs:63`
  - `Api/ApplicationRegistration.cs:32`
  - `SessionDomainTests.cs:9`
  - ADR-0003:13 e :90
- **`VehicleType`**
  - `Domain/Facilities/Enums.cs:13`
  - ADR-0003:91
- **Aceitação parcial de um lote**
  - `IngestOccupancyBatchHandler.cs:18`
  - `IngestionEndpointTests.cs:55`
- **`MaxClockSkew` (5 min)**
  - `IngestOccupancyBatchHandler.cs:26`
  - ADR-0008:150
- **Lugar sem leitura = livre**
  - ADR-0008:82
- **Raio máximo de 50 km**
  - `AvailabilitySearch.cs:18`
  - ADR-0008:102
- **Contador de propriedades desconhecidas**
  - ADR-0011:63

**C. Retenção (RNF-05)**
- **Prazos `RawEvents`, `ArchiveDelay`, `GuardMargin` e `MaxEventAge`**
  - `Application/Retention/RetentionContracts.cs:23`, `:29` e `:36`
  - `IngestOccupancyBatchHandler.cs:33`
  - ADR-0013 (:3, :37, :81, :119, :152)
- **Comparação do CPU da carga:** decidido na 10b (opção A: `bench/compose.bench.yml`).

**D. Variante monolítica e validade**
- **M2 e a ameaça «`jsonb` dispensaria a migração»**
  - ADR-0012:95
  - revisão 8:12
  - DIARIO:1382
- **_Pool_ da API monolítica (40) e duração do aquecimento**
  - DIARIO:1473

**E. Referências bibliográficas por inserir (não inventar)**
- ADR-0004:78
- ADR-0006:70
- ADR-0007:63

**F. Processo e máquina**
- Repetir o teste manual no Swagger: DIARIO:264
- `version` opcional no OpenAPI: DIARIO:395
- Estado da máquina na aceitação da 9a: DIARIO:1399
- `journal:true` como variável de medição: revisão 3:57

## Tratamento (sessão 10b)

| # | Tratamento |
|---|---|
| 1 | **Documentado.** O resumo das 10 fases e a RASTREABILIDADE dizem que a Fase 9 só fez a 9a. Os cinco cenários ficam para uma Fase 9b, a decidir pelo Diogo. |
| 2 | **Para o Diogo.** O RNF-05 passou para «Parcial» na RASTREABILIDADE. Implementar o aparo e a purga, ou declarar a limitação, é uma decisão de âmbito. |
| 3 | **Corrigido na medição e anotado.** A leitura passou para `$collStats`, com os campos obrigatórios. As linhas inválidas da 10a estão anotadas no `bench/README.md`, no ADR-0013 e no diário; os CSV ficam como estão. |
| 4 | **Para o Diogo.** A mudança altera o comportamento dos cenários futuros nas duas variantes. |
| 5 | **Corrigido.** As alterações têm *commit* antes de qualquer sessão. |
| 6 | **Confirmado e corrigido.** O `$collStats` não tem `indexFreeStorageSize`. O valor passou a ser a soma de `file bytes available for reuse` de cada índice, verificada ao vivo. |
| 7 | **RASTREABILIDADE corrigida.** A medição ou o consumidor ficam para o Diogo. |
| 8 | **Corrigido.** Serviços `mongo-client` e `postgres-client` sem *healthcheck* no *override*, verificados ao vivo. |
| 9 | **Corrigido.** O `run.sh` recusa um Docker Engine anterior ao 25. O limite de 1 h está documentado no `bench/README.md`. |
| 10 | **Corrigido:** `bench/README.md`, ADR-0013, cabeçalho do `retention.sh` e CLAUDE.md. |
| 11 | **Corrigido:** (a), (b), (c) e o estado do UC-02 e do RNF-05. As contagens de testes passam a ser as da 10b, no diário. |
| 12 | **Para o Diogo.** |
| 13 | **Para o Diogo** (remover, ou manter como modelo conceptual). |
| 14 | **Corrigido:** chave `engine_compressor=`. |
| 15 | **Para o Diogo.** Registado como ameaça à validade no ADR-0013. |
| 16 | **Corrigido** (CLAUDE.md). |
| 17 | **Corrigido** (`.gitignore`: `src/**/archive/`). |
| 18 | **Teste instável corrigido** (o *scrape* repete-se até 5 s). O resto fica para os cenários da Fase 9b. |
| 19 | Sem alteração. A especificação só sobe de versão por decisão do Diogo. |
| 20 | — |
