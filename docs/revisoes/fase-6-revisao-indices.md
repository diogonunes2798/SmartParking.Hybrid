# Fase 6 — Revisão dos índices do relatório (UC-04)

- **Data:** 16-11-2026
- **Método:** skill `ef-index-advisor` (plugin `dotnet-ai-toolkit` 1.3.0). O plugin não estava ativo na sessão, por isso li o `SKILL.md` e apliquei as regras à mão:
  - igualdade antes de intervalo;
  - colunas lidas no `INCLUDE`;
  - nada de índices redundantes;
  - confirmação com `EXPLAIN ANALYZE`.
- **Consultas analisadas:** as duas variantes de `EfOccupancyReportQueries` (com e sem `zone`). O *worker* não lê a tabela de resumo, só a escreve com *upsert*.
- **Dados:** script `bench/analytics/explain-report.sql`. Insere um ano sintético de 438 000 linhas (50 parques em 5 zonas × 8 760 horas), faz `ANALYZE`, corre três planos e termina com `ROLLBACK`, por isso a base de dados fica como estava. PostgreSQL 16 do `docker compose`, numa execução local. Os tempos são indicativos, não medições da tese.

## Padrões de consulta

| Consulta | Predicado | Colunas lidas | Índice ideal (regra) |
|---|---|---|---|
| Q1: uma zona | `zone = $1 AND hour_start >= $2 AND hour_start < $3` | `hour_start`, `event_count`, `occupied_seconds`, `available_seconds` | `(zone, hour_start)`: igualdade primeiro, intervalo depois; `INCLUDE` das três somas (índice de cobertura) |
| Q2/Q3: todas as zonas | `hour_start >= $1 AND hour_start < $2` | as mesmas, mais `zone` | um índice que comece por `hour_start` |
| *Upsert* do *worker* | `ON CONFLICT (facility_id, hour_start)` | — | um índice único sobre esse conjunto de colunas, por qualquer ordem |

## Achado e alteração

A primeira versão tinha a chave primária `(facility_id, hour_start)` e o índice `(zone, hour_start)`. Nenhum dos dois serve a consulta sem zona, porque o PostgreSQL 16 não faz *skip scan*. O plano mostrou um *Parallel Seq Scan* que descartou 437 mil linhas para devolver 1 200.

Criar um terceiro índice em `hour_start` custaria em cada *upsert*. A alternativa escolhida foi **reordenar a chave primária para `(hour_start, facility_id)`**:
- a unicidade é a mesma;
- o `ON CONFLICT (facility_id, hour_start)` continua a encontrar o índice, porque o PostgreSQL compara o conjunto de colunas e não a ordem;
- a chave passa a servir os intervalos de horas.

Ao índice da zona juntou-se `INCLUDE (event_count, occupied_seconds, available_seconds)`. As alterações estão em `Infrastructure.Relational/Analytics/AnalyticsTables.cs` e na migração `AddOccupancyHourlySummary`. A migração foi gerada de novo, porque ainda não estava em nenhum commit.

## Planos (antes → depois)

| Consulta | Antes | Depois |
|---|---|---|
| Q1: zona `synthetic-1`, `day`, março de 2025 (7 440 linhas) | *Bitmap Index Scan* em `ix_occupancy_hourly_zone_hour_start`; 7,4 ms | igual, com o índice de cobertura; 8,3 ms |
| Q2: todas as zonas, `hour`, um dia (1 200 linhas) | *Parallel Seq Scan*, 437 004 linhas descartadas, 5 500 *buffers*; **28,3 ms** | *Index Scan* em `pk_occupancy_hourly` (`Index Cond` no intervalo de `hour_start`), 262 *buffers*; **2,4 ms** |
| Q3: todas as zonas, `month`, um ano (438 000 linhas) | *Seq Scan* e ordenação *external merge* (28 MB em disco); 912 ms | igual; 945 ms |

## Leitura dos resultados

- **Q1** não chega a ser *index-only*. O script corre numa transação desfeita no fim e o `VACUUM` não pode correr lá dentro, por isso o mapa de visibilidade fica por marcar e o PostgreSQL vai à tabela. Em funcionamento normal, as horas antigas já não mudam e o *autovacuum* marca-as, e o `INCLUDE` evita essas leituras. Não ficou medido (`TODO`: confirmar na Fase 9 com `Heap Fetches: 0`).
- **Q3** lê a tabela inteira, como deve: o intervalo é tudo o que existe. O custo está na ordenação para o `count(DISTINCT hour_start)` por grupo, que passa o `work_mem` por omissão (4 MB) e vai para disco. Um relatório mensal de um ano inteiro, com todas as zonas, é o pior caso do UC-04, e não houve otimização prematura. Se a Fase 9 mostrar que é relevante, há duas opções:
  - calcular as horas a partir de uma contagem por hora já agregada;
  - aumentar o `work_mem` da sessão do relatório.
- **Custo de escrita:** a tabela fica com dois índices (a chave e o da zona). Com os 50 parques sintéticos do *script* (a D8 só fixa os lugares, não o número de parques), são 50 linhas por hora, mais as 2 horas reagregadas, o que dá um custo de escrita desprezável.
- **Sem chave estrangeira** para `parking_facilities`: é de propósito, para o isolamento do schema `analytics` (ADR-0010). Está verificado por `MigrationTests.Analytics_tables_have_no_foreign_keys`.

## Recomendações não aplicadas

- **Índice em `facility_id`:** não se aplica, porque nenhuma consulta de produção filtra por parque (só os testes, com poucas linhas).
- **Particionamento por tempo:** fica para a Fase 10 (retenção, RNF-05), se a tabela crescer além de alguns anos.
