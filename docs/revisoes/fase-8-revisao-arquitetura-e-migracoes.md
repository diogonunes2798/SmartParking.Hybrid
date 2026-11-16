# Fase 8 — Revisões independentes: arquitetura das duas variantes e migrações do particionamento

Data: 16-11-2026. Duas revisões só de leitura, feitas por subagentes separados enquanto corria o ensaio do UC-05, sobre o código ainda por fazer *commit*. O guião pedia o agente `dotnet-architecture-reviewer` e a skill `ef-migration-reviewer`. Nenhum dos dois está disponível nesta sessão, por isso foram usados agentes gerais com o mesmo enunciado.

## 1. Arquitetura: as duas variantes partilham domínio, aplicação e API, e só a infraestrutura muda?

**Veredicto: sim.** Não há alterações em `Domain`, em `Application` nem em `Api/Endpoints`. A escolha do modo vive só nos dois `Program.cs` e em `Infrastructure.Relational/Monolithic`. No modo monolítico não fica registado nada de Cache, Documents ou Messaging, e a baseline é justa. Não houve achados altos.

| # | Gravidade | Achado | Destino |
|---|---|---|---|
| M1 | Média | O `e2e_latency` era registado para todos os eventos inseridos. O projetor Redis só o regista para `Applied`/`Unchanged`, por isso eventos atrasados inflacionariam o p95 monolítico. | **Corrigido:** o CTE devolve os eventos que passaram a ser o estado (`RETURNING`) e só esses entram no histograma. ADR-0012, ponto 7. |
| M2 | Média | O log monolítico guarda menos do que o MongoDB (sem `payload`: perde, por exemplo, `sensorGeneration`). Isto favorece o monolítico na comparação do espaço e do custo por inserção. | **Documentado** como ameaça à validade no ADR-0012. `TODO(Diogo):` decidir sobre uma coluna `sensor_generation`. |
| M3 | Média | Três testes estavam marcados como só híbridos sem depender do Redis. Também faltavam testes do 503 com o PostgreSQL em baixo e da composição dos Workers. | **Corrigido:** os três testes correm nos dois modos; novo `Ingestion_answers_503_when_postgres_is_down`. A composição dos Workers no modo monolítico foi verificada à mão (*readiness* só `postgres`, ensaio do UC-05), não por teste. |
| L1 | Baixa | A verificação das colunas do UC-05 nunca volta atrás: depois de um `Down()`, todas as inserções dariam 500. | **Corrigido:** um 42703 força nova verificação e repete a escrita sem as colunas. |
| L2 | Baixa | Um evento de um lugar desconhecido dava estado ao lugar; no Redis é ignorado (`UnknownSpot`). | **Corrigido:** o *upsert* de `spot_state` junta com `parking_spots`. Novo teste `An_event_of_an_unknown_spot_is_logged_but_gives_it_no_state`. |
| L3 | Baixa | O PostgreSQL ordena em microssegundos; o Redis e o MongoDB em milissegundos mais o `eventId`. | **Documentado** no ADR-0012. |
| L4 | Baixa | O histórico de migrações é partilhado (PostGIS e superutilizador também no modo híbrido). A imagem passou de 16.16 para 16.15. | **Documentado** no ADR-0012. Os resultados híbridos usados no Cap. 6.5 têm de ser obtidos com a imagem nova (Fase 9). |
| L5 | Baixa | Nada deteta uma API e uns Workers em modos diferentes. | **Documentado.** O CSV regista a variante e a *readiness* mostra os motores. |
| L6 | Baixa | Comentários desatualizados (`AvailabilityEndpoints`, `ApplicationRegistration`). | **Corrigido.** |

## 2. Migrações do particionamento e SQL da variante

| # | Gravidade | Achado | Destino |
|---|---|---|---|
| 1 | **Alta** | O estado de cada lugar antes de um instante (UC-04) usava `DISTINCT ON`. Sem *skip scan* no PostgreSQL 16, essa consulta lê todo o histórico do parque; o MongoDB lê cerca de uma entrada por lugar. A comparação seria injusta. | **Corrigido:** um CTE recursivo emula o *skip scan* no índice `(facility_id, spot_id, …)`. Teste `The_skip_scan_state_before_an_instant_equals_the_direct_distinct_on` (atravessa partições, empates e lugares não registados); a mutação da ordem é apanhada. |
| 2 | Média | `CREATE TABLE … PARTITION OF` pede um `ACCESS EXCLUSIVE` sobre a tabela-mãe e não tinha `lock_timeout`. Atrás de uma leitura longa, pararia a ingestão. | **Corrigido:** a função tem `SET lock_timeout = '2s'` e trata `lock_not_available` com um `WARNING`. |
| 3 | Média | Um mês que caia na `DEFAULT` nunca mais pode ter partição, e a `DEFAULT` não é podada. | **Corrigido:** os Workers registam um aviso se a `DEFAULT` tiver linhas, e o script do ensaio regista a contagem. No ensaio de 16-11 foi verificada à mão: 0 linhas. |
| 4 | Média | O limite de 5 s do `AddEnvironmentalColumns` depende de o EF correr a migração numa transação e de a migração ser aplicada sozinha. | **Mantido e medido:** procedimento documentado no ADR. No ensaio, a migração não esperou por nenhum *lock* (ver o diário). |
| 5 | Baixa | A idempotência é por `(event_id, "timestamp")`, mais fraca do que o `ux_eventId` do MongoDB. | **Documentado** no ADR-0012. |
| 6 | Baixa | Ainda pode haver *deadlocks* (a ordem de um `INSERT … SELECT … ORDER BY` não é garantida). | **Mitigado:** `ORDER BY event_id` na inserção, e repetição em 40P01/40001 (já existia). A métrica de repetições fica para a Fase 9. |
| 7 | Baixa | O mesmo que L1 acima. | **Corrigido.** |
| 8 | Baixa | O `Down()` remove o PostGIS mesmo que ele já existisse antes. | **Documentado.** |
| 9 | Baixa | As partições iniciais dependem da data em que a migração é aplicada. | **Documentado.** Os ensaios registam a data. |

**Verificado como correto** (pelas duas revisões):
- a chave primária inclui a chave de partição;
- `ON CONFLICT DO NOTHING` funciona em tabelas particionadas;
- os limites UTC das partições estão certos;
- os índices propagam-se a todas as partições;
- a poda de partições acontece mesmo com parâmetros;
- o `spot_state` é elegível para atualizações HOT;
- a expressão do índice GiST coincide com a da consulta;
- `ADD COLUMN` com nulos e sem valor por omissão não reescreve linhas;
- os erros transitórios dão 503;
- o mesmo número de índices B-tree que o MongoDB (3 contra 4).
