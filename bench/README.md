# bench — medições da Fase 9

Infraestrutura de medição dos cenários da secção 6 da especificação. Um comando por cenário gera um CSV comparável entre a variante híbrida e a monolítica. Os scripts **só recolhem** dados: não calculam médias nem tiram conclusões. Os números da tese saem dos CSV, calculados pelo Diogo ou por um script versionado.

## Comando

```bash
./bench/run.sh <cenario> [--variant hybrid|monolithic] [--runs 5] [--warmup 60] [--allow-dirty]
```

Pré-requisitos:
- Docker Desktop a correr;
- Git Bash (no Windows);
- Python 3;
- `dotnet tool restore` feito uma vez;
- as portas 5080 e 5090 livres (a API e os Workers não podem estar já a correr).

**Atenção:** o passo 1 faz `docker compose down -v`, que **apaga os volumes** do PostgreSQL, do MongoDB e do Redis.

Com alterações por fazer *commit*, o script recusa-se a correr, porque o *commit* registado não descreveria o código medido. Para ensaios de desenvolvimento, `--allow-dirty` deixa correr, e a lista de alterações fica no `meta.json`.

## O que faz em cada execução

1. `docker compose down -v` e `docker compose up -d --wait`, para começar de um estado limpo.
2. Aplica as migrações (`dotnet ef database update`) e arranca a API e os Workers a partir das DLL de Release, com:
   - `ASPNETCORE_ENVIRONMENT=Benchmark` (`appsettings.Benchmark.json`: sem o registo de cada instrução SQL do ambiente Development, sem Swagger, e com `Maximum Pool Size=40` por processo);
   - o `Persistence__Mode` da variante pedida.

   Depois espera pelo `/health/ready` dos dois processos.
3. Carrega os dados base do cenário D8 (`scenarios/d8-reference.json`: 20 parques e 5 000 lugares e sensores) através do simulador, por RF-06, e espera até o *read model* conhecer todos os lugares.
4. Faz o aquecimento (`--warmup`, 60 s por omissão), que não é medido.
5. Corre a parte medida do cenário e recolhe as métricas.
6. Acrescenta as linhas a `results/<cenario>/<AAAA-MM-DD_HHMM>.csv` e para a API e os Workers.

## Ficheiros produzidos

| Ficheiro | Conteúdo | Versionado |
|---|---|---|
| `<data>.csv` | linhas da §6: `cenario,variante,metrica,valor,unidade,execucao,configuracao`; durante a sessão chama-se `<data>.csv.partial` e só é renomeado quando todas as execuções terminam (uma sessão falhada deixa `<data>.failed.csv`) | sim |
| `<data>.run<n>.k6.json` | resumo bruto do k6 (`--summary-export`) | sim |
| `<data>.meta.json` | comando, *commit* (`git rev-parse HEAD`), alterações por fazer *commit* e o respetivo *diff*, variáveis de ambiente que mudam o comportamento (as credenciais ficam só como `<set>`), imagens (*tag* + *digest*), versões do .NET, do Docker e do Python, máquina (CPU, memória, recursos do Docker) e, por execução, a duração da carga base, as horas de início e de fim de cada ferramenta e os códigos de saída | sim |
| `<data>.logs/` | saída dos processos, do k6, do simulador e das migrações | não (`.gitignore`) |

A preparação da máquina (aplicações fechadas, carregador ligado, atualizações automáticas desligadas) não é detetável pelo script, por isso regista-se à mão no `docs/DIARIO.md`.

## Métricas recolhidas

| Prefixo | Origem | Unidades | Notas |
|---|---|---|---|
| `k6_*` | `lib/k6_to_csv.py` sobre o resumo do k6 | `ms`, `requests`, `requests/s`, `ratio`, `samples`, `bytes`, `vus` | Cada métrica dá uma linha por estatística (`avg`, `min`, `med`, `max`, `p90`, `p95`, `p99`). Um sub-métrico com etiqueta leva a chave e o valor no nome: `k6_http_req_duration_name_availability_p95`. |
| `sim_*` | resumo do simulador (`--summary-csv`), reetiquetado por `lib/relabel_csv.py` com o cenário e a execução do *bench* | as do simulador | A configuração do simulador (`Describe()`) vai no fim da coluna `configuracao`. |
| `*_exit_code` | código de saída do k6 e do simulador em cada execução | `code` | Um código diferente de 0 indica que a carga não foi limpa. No simulador: 1 = eventos recusados ou com erro; 4 = carga oferecida não mantida. Também fica no `meta.json`. |
| `storage_<fase>_*` | `lib/retention_stats.py storage` (cenário `retention`): no híbrido, `db.occupancy_events.stats()` (`count`, `size`, `storageSize`, `freeStorageSize`, `totalIndexSize`, `indexSizes`); no monolítico, as partições de `monolith.occupancy_events` (`pg_table_size`, `pg_indexes_size`) | `events`, `bytes`, `partitions` | Fases: `after_load`, `after_retention`, `after_compact` (só híbrido). `_total_bytes` = `_storage_bytes` + `_index_bytes`. O compressor em vigor vai na coluna `configuracao` (`compressor=`). |
| `cpu_<fase>_*` | `lib/retention_stats.py cpu-diff`: `usage_usec` do cgroup de cada contentor (`cpu.stat`) e tempo de CPU da API e dos Workers no anfitrião | `s` | Fases: `idle` (linha de base), `load`, `retention`. Com `--events`, também `cpu_<fase>_total_seconds_per_1e6_events`. |
| `archive_*`, `events_*` | cenário `retention` | `files`, `bytes`, `events` | Ficheiros e bytes do arquivo (`*.ndjson.gz`); eventos guardados, elegíveis, removidos e restantes. |
| `e2e_*` | `lib/e2e_latency.py`: diferença do histograma D7 (`smartparking_ingestion_e2e_latency_seconds`) entre o início e o fim da janela medida, somando a API e os Workers | `events`, `ms` | Os percentis são limites superiores de *bucket* (daí o nome `*_bucket_upper`); a média é exata (`_sum / _count`). No híbrido, a medição vai até à atualização do Redis; no monolítico, até ao *commit* (ADR-0012). |

## Cenários

Um cenário é um ficheiro `scenarios/<nome>.sh`, que o `run.sh` lê com `source`. Define três funções:
- `scenario_config`: texto da configuração do cenário;
- `scenario_warmup`: carga de aquecimento;
- `scenario_run <n>`: parte medida da execução n.

Pode ainda definir `scenario_seed`, se a carga base não for a do D8. As funções auxiliares (`k6_run`, `k6_collect`, `sim_run`, `sim_collect`, `e2e_snapshot`, `e2e_collect` e `run_meta`) estão descritas no cabeçalho do `run.sh`.

| Cenário | Estado | O que faz |
|---|---|---|
| `smoke` | Fase 9a | Prova a cadeia de recolha; **não é uma medição para a tese**. Durante 30 s, junta leituras do UC-03 com o k6 (20/s) e eventos do UC-01 com o simulador (5/s), e lê a janela D7. O aquecimento acaba com todos os lugares livres e espera pelo escoamento dos eventos em curso. A parte medida não envia a rajada final de FREED (`--free-at-end false`), para que esta não caia na janela D7. |
| `availability-read` | por fazer | UC-03 a taxa crescente (RNF-01, p95 < 200 ms) |
| `session-payment` | por fazer | UC-02: abrir, fechar e pagar (RNF-01, p95 < 2 s) |
| `ingest-scale` | por fazer | simulador em pico com 1, 2 e 4 consumidores (RNF-02, D7) |
| `schema-evolution` | por fazer | rampa da geração 2 (híbrido) e migração (monolítico) (RNF-04) |
| `failover` | por fazer | parar um contentor a meio da carga (RNF-03) |
| `retention` | Fase 10 | RNF-05 (ADR-0013): carrega `RETENTION_EVENTS` (10⁶) eventos pela API, com o tempo dos sensores comprimido em `RETENTION_DAYS` (4) dias UTC no passado. Agrega esses dias, aplica a retenção uma vez (arquiva e remove a primeira metade) e, no híbrido, faz `compact`. Mede o CPU (repouso, carga, retenção) e o espaço depois de cada passo. `RETENTION_COMPRESSOR=zstd` muda o compressor da coleção no híbrido; o monolítico usa partições diárias. Correr com `--warmup 0`. Variáveis: `RETENTION_RATE` (500/s), `RETENTION_IDLE_SECONDS`, `RETENTION_SETTLE_SECONDS`, `RETENTION_TTL_WAIT`. Os ficheiros do arquivo são contados e depois apagados. |

## Testes dos scripts

```bash
python -m unittest discover -s bench/tests -v
```
