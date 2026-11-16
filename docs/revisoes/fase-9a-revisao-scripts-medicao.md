# Fase 9a — revisão independente dos *scripts* de medição

- **Data:** 16-11-2026.
- **Âmbito:** `git diff 621ecd7..487ba91`, ramo `phase-9a-bench-infra`. Inclui `bench/run.sh`, `bench/scenarios/smoke.sh`, `bench/k6/availability-smoke.js`, `bench/lib/*.py`, `appsettings.Benchmark.json`, o *pool* do Npgsql e a imagem do k6 no `docker-compose.yml`.
- **Revisor:** subagente geral, só de leitura. Não arrancou processos nem contentores, porque estava a decorrer uma medição. Correu apenas os testes Python (10, todos a passar).
- **Foco:** erros nos *scripts*, ameaças à validade das medições, lacunas de reprodutibilidade e dimensionamento do *pool*.

O revisor verificou cada achado no código antes de o reportar. A coluna «Estado» indica o que foi feito no *commit* `327b34b`.

| # | Gravidade | Achado | Estado |
|---|---|---|---|
| 1 | média-alta | **A rajada final do simulador entrava na janela D7.** Com `--free-at-end true` (o valor por omissão), no fim da execução o simulador envia um FREED por cada lugar ocupado, todos com o mesmo `timestamp`. Isso acontece antes da leitura «depois» do histograma, por isso estes eventos contavam em `e2e_*` e em `sim_*`. No `smoke` são cerca de 70 eventos. Numa execução D8 de 30 min seriam cerca de 2 500 eventos simultâneos no p95/p99. | Corrigido: a parte medida usa `--free-at-end false`. O estado limpo de cada execução vem do `down -v`. Confirmado no ensaio: `sim_sent` = `e2e_events` = 140. |
| 2 | média | **Eventos do aquecimento entravam na janela medida.** A leitura «antes» era feita logo que o aquecimento terminava, sem esperar pelo escoamento. Os eventos ainda na outbox ou no *stream*, incluindo a rajada final do aquecimento, eram contados como medidos. | Corrigido: no fim do aquecimento espera-se `SMOKE_DRAIN_SECONDS` antes da leitura «antes». |
| 3 | média | **O k6 e o simulador não carregavam o sistema no mesmo intervalo.** O simulador procurava os 20 parques e os 5 000 lugares e esperava de novo pelo *read model* antes de começar os seus 30 s. As duas janelas só se sobrepunham em parte, e as consultas da procura somavam-se à carga medida pelo k6. | Corrigido em parte: `--wait-read-model 0`, e o `meta.json` regista as horas de início e de fim de cada ferramenta. A procura dos lugares (cerca de 0,4 s) mantém-se. Nos cenários mistos da Fase 9 pode ser preciso sincronizar o arranque. |
| 4 | média | **Um processo esquecido nas portas 5080 ou 5090 seria medido em silêncio.** O novo processo não conseguia ocupar a porta e terminava, mas o `/health/ready` respondia na mesma, a partir do processo antigo. Esse processo podia estar noutro modo ou numa compilação anterior. | Corrigido: o `run.sh` recusa-se a arrancar se as portas estiverem ocupadas (`ports_free`), e o `wait_ready` confirma que o PID arrancado continua vivo. A recusa foi testada com um servidor na porta 5080 (código de saída 2). **Este cenário aconteceu durante a fase**, ao interromper a primeira aceitação (ver o diário). |
| 5 | média | ***Pool* do Npgsql.** API 40 + Workers 40 = 80 fica abaixo de `max_connections` = 100 (97 utilizáveis). Mas no `ingest-scale`, com 4 instâncias de Workers, seriam 40 + 4 × 40 = 200. No monolítico, o *pool* da API é o caminho de escrita síncrono, e um teto fixo pode limitar só essa variante. O valor não era registado. | Registado: o *pool* de cada processo vai para a coluna `configuracao` (`pool=api=40/workers=40`). **Fica para o cenário `ingest-scale`:** fixar o *pool* por processo, ou subir `max_connections`, de modo a que a soma caiba, e decidir com o Diogo se o teto da API monolítica é uma ameaça à validade. |
| 6 | média | **Uma sessão falhada deixava um CSV com aspeto de completo.** As linhas eram acrescentadas por execução e por ferramenta, e só o `meta.json` dizia «failed». Os códigos de saída das ferramentas (1 = carga não limpa; 4 = saturada) não chegavam ao CSV. | Corrigido: as linhas vão para `<data>.csv.partial`, que é renomeado no fim. Uma sessão falhada deixa `<data>.failed.csv`. Linhas novas `k6_exit_code` e `sim_exit_code`. |
| 7 | baixa-média | **Lacunas de reprodutibilidade no `meta.json`.** Não ficavam registadas as variáveis de ambiente herdadas (`OTEL_*` liga a exportação de *traces*; `Persistence__*`, `ConnectionStrings__*`), o *diff* quando se usa `--allow-dirty`, nem o próprio `--allow-dirty` no comando. | Corrigido: `environment` (as credenciais ficam só como `<set>`), `uncommitted_diff` e o comando completo. |
| 8 | baixa | **A verificação de alterações por fazer *commit* recusava a segunda sessão**, porque o CSV anterior, ainda sem *commit*, contava como alteração. | Corrigido: `results/` fica de fora (`':!results'`). |
| 9 | baixa | **Um Ctrl+C podia deixar o contentor do k6 a correr.** | Corrigido: a limpeza termina as tarefas em segundo plano e remove os contentores `k6` do projeto. Também aconteceu ao interromper a primeira aceitação. |

**Verificado e correto, segundo o revisor:**
- A recolha do D7 nas duas variantes: no híbrido o histograma é registado nos Workers (`AvailabilityProjector`); no monolítico, na API (`PostgresEventBus`). Somar os dois `/metrics` conta cada evento uma única vez.
- A expressão regular da exposição Prometheus e as contas da janela.
- Os quantis por limite superior de *bucket* e a unidade em ms.
- Os formatos do `--summary-export` do k6 1.x, incluindo `passes` → `true` em `http_req_failed`.
- A exclusão dos pedidos do `setup()` através da etiqueta `name`.
- A formatação `0.###`.
- O tratamento do `set -e` à volta de `||`.
- A paragem dos processos com `winpid` e `taskkill`.
- A conversão de caminhos (`MSYS_NO_PATHCONV`, `pwd -W`).
