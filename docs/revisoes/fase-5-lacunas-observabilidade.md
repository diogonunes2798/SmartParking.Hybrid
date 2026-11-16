# Fase 5 — O que falta medir para as secções 6.2 e 6.5

- **Data:** 16-11-2026
- **Método:** passo 6 do guião da Fase 5, «observability-gap-finder». O agente com esse nome não está disponível nesta sessão. A análise foi feita por um subagente genérico com essa pergunta, que leu a especificação, o RASTREABILIDADE, o ADR-0002, o `ServiceDefaults`, as classes `*Metrics`, o `RedisStreamConsumer`, o compose e o `prometheus.yml`, incluindo as alterações da Fase 5 ainda sem commit. Não alterou ficheiros.
- **Estado do repositório na altura:** `bench/`, `baseline/` e `tools/` ainda não existem; `results/` está vazia.

## Requisito/cenário

| Requisito/cenário | Medido hoje | Em falta | Onde (fase/componente) | Prioridade |
|---|---|---|---|---|
| **RNF-01 UC-03** (consulta, p95 < 200 ms) | `http.server.request.duration` (*buckets* finos até 2 s, sondas excluídas), *label* `http_route`; alvo em `smartparking_target_seconds{objective="uc03_availability_query_p95"}` | Script k6 `availability-read` com taxa de chegada constante. Valores em bruto do k6 exportados para CSV. Métricas do servidor Redis (CPU, `instantaneous_ops_per_sec`, latência). Não há instrumentação do StackExchange.Redis, só do Npgsql. | Fase 9 (bench), infra (`redis_exporter`) | Alta |
| **RNF-01 UC-01** (ponta a ponta < 1 s) | `smartparking.ingestion.e2e_latency` (sensor → Redis), `read_model.bus_lag` (API → Redis), `occupancy_log.lag` (API → MongoDB) | Simulador (`tools/SensorSimulator`, Fase 7). Não há *gauge* do atraso do *stream* (XLEN, XPENDING ou `lag` do XINFO GROUPS) por grupo. Sem isso, não se separa uma latência alta de um *backlog* acumulado. | Fase 7 (simulador), código híbrido (Messaging) | Alta |
| **RNF-01 UC-02** (escrita transacional < 2 s) | Duração HTTP por rota; spans e métricas Npgsql e EF Core | Script k6 `session-payment`. Contador de conflitos de concorrência otimista (409) por rota. Estatísticas do PostgreSQL (`pg_stat_database`, *locks*, *commits*/s, WAL). | Fase 9, infra (`postgres_exporter`) | Alta |
| **Janela de consistência** (Cap. 4.5) | `smartparking.consistency.window{type}`, `outbox.publish_lag{type}`, `outbox.pending`, `outbox.batches{outcome}` | O alvo está vazio (`TODO(Diogo)` em `LatencyTargets.cs`). Para sessões e pagamentos só existe a primeira parte da janela (`publish_lag`) até à Fase 6. Na variante monolítica a janela não existe e tem de ser declarada como 0 ou «não aplicável». | Diogo (alvo), Fase 6, Fase 8 | Média |
| **RNF-02** (débito com 1, 2 e 4 consumidores) | `smartparking.bus.consumer.messages{group,outcome}`, `occupancy_log.events`, `read_model.events` | Cada processo Workers tem um consumidor por grupo e usa a porta fixa 5090. O Prometheus só recolhe `host.docker.internal:5090`, por isso com 2 ou 4 processos há conflito de porta e só um é recolhido. Falta uma *label* `consumer` (só existe `group`). Falta medir CPU e memória por contentor ou processo, para mostrar onde está o estrangulamento. | Código híbrido (porta e *label*), infra (alvos dinâmicos, cAdvisor), Fase 9 | Alta |
| **RNF-03** (falha de um nó) | Prontidão por motor, `consumer.failures`, `consecutive_failures`, `outbox.batches{outcome="failed"}` | A medida principal é «sem perda de eventos». Falta reconciliar o número de eventos enviados pelo simulador com o número persistido (MongoDB e Redis) no fim da execução. Falta também o tempo de recuperação (até `/health/ready` voltar a 200 e o *backlog* voltar a 0) e os erros do lado do k6 durante a falha. | Fase 9 (script de reconciliação), código híbrido (*gauge* do *backlog*) | Alta |
| **Comparação 6.5** (híbrido vs. monolítico) | Nada (Fase 8 por fazer) | `Persistence:Mode` não existe. Faltam pontos finais comparáveis (ver nota abaixo), a *label* `variant` nas métricas e no CSV, recursos por motor (CPU, memória, disco, IOPS) e espaço ocupado (`pg_total_relation_size`, `dbStats` do MongoDB, `used_memory` do Redis). | Fase 8 (baseline), infra (cAdvisor, *exporters*), Fase 9 | Alta |
| **CSV** `results/<cenario>/<AAAA-MM-DD_HHMM>.csv` | Adiado (ADR-0002, ponto 3) | Script que junta o resumo do k6 e as consultas ao Prometheus no formato pedido (cenário, variante, métrica, valor, unidade, n.º de execução, configuração), com pelo menos 5 execuções. A coluna de configuração deve guardar o commit (`service.version`) e os *digests* das imagens. | Fase 9 (`bench/run.sh`) | Alta |

**Nota sobre a comparabilidade.** A métrica `ingestion.e2e_latency` (sensor → Redis) não tem equivalente direto no monolítico, que não tem *bus* nem Redis. Proposta de pontos finais comparáveis:

- **Frescura observável:** tempo entre o `timestamp` do sensor e o momento em que `GET /api/availability` reflete o evento. Mede-se da mesma forma nas duas variantes, com uma sonda k6 que escreve um evento e consulta até o ver.
- **Persistência durável:** tempo entre a receção na API e o *commit* do evento (MongoDB no híbrido, tabela particionada no monolítico).

O `bus_lag` e a `consistency.window` ficam como decomposição interna do híbrido e não entram na comparação. O UC-03 só é comparável se o k6 usar os mesmos parâmetros, o mesmo conjunto de dados e a mesma ocupação de partida.

## Ameaças à validade

- **Relógios:** `e2e_latency` inclui o desvio do relógio do sensor. A `consistency.window` compara o relógio da API com o dos Workers e só é válida na mesma máquina (ADR-0009, ponto 5). Tem de ficar fixado na Fase 9: tudo no mesmo *host*, ou NTP verificado.
- **Omissão coordenada:** com VUs fechados, o k6 subestima a cauda. Usar `constant-arrival-rate` e registar `dropped_iterations`.
- **Aquecimento:** o JIT, os *pools* (Npgsql; ver a nota da Fase 9 sobre o *pool* face a `max_connections`) e a *cache* do MongoDB enviesam o início. Descartar uma janela inicial e registar a sua duração. No teste manual da Fase 5, a primeira janela medida foi 1,32 s e a segunda 0,08 s.
- **Resolução dos *buckets*:** o p95 do Prometheus é interpolado. Entre 1 e 2 s há só 3 limites (1,25, 1,5 e 2), pouco para o alvo de 2 s do UC-02. O ADR-0002 já diz que o valor de referência vem das amostras em bruto do k6.
- **O *backlog* distorce os histogramas:** depois de uma recuperação, atrasos de minutos entram no p95, apesar de os duplicados e os eventos antigos serem excluídos. Separar os períodos de carga estável e de recuperação, por tempo ou por uma *label* de fase.
- **Recolha a cada 5 s com execuções curtas:** o `rate()` e o `increase()` ficam pouco precisos. Ler os contadores logo antes e logo depois da execução, e usar execuções bem mais longas do que o intervalo de recolha.
- ***Traces* sempre recolhidos:** não há *sampler* (custo de CPU, ADR-0002). Prever um interruptor de configuração, desligado nas duas variantes durante os *benchmarks*.
- **O mesmo *hardware*:** a API e os Workers correm com `dotnet run` no *host* e os motores em Docker. A contenção de CPU tem de ser igual nas duas variantes, idealmente com limites `cpus` e `mem_limit` no compose.

## As 5 ações por ordem de prioridade

1. **Fase 9, `bench/`:** k6 com taxa de chegada constante e exportação para CSV no formato da spec §6. Inclui o commit e os *digests* das imagens, 5 execuções e a janela de aquecimento descartada.
2. **Infra:** cAdvisor, `postgres_exporter`, `redis_exporter` e `mongodb_exporter` no perfil `observability`, recolhidos pelo Prometheus. Limites de CPU e memória por contentor.
3. **Código híbrido (Messaging e Workers):** *gauge* do *backlog* e do atraso do *stream* por grupo, *label* `consumer`, e porta e alvo do Prometheus configuráveis para correr 1, 2 ou 4 processos (RNF-02).
4. **Fase 8, baseline:** `Persistence:Mode=Monolithic` com o ponto final comparável «frescura observável», a *label* e a coluna `variant`, e as mesmas métricas HTTP.
5. **Decisões para o Diogo e scripts:** alvo da janela de consistência (`TODO(Diogo)`), método de medição do tempo de recuperação, e script de reconciliação de contagens para o RNF-03 (enviados contra persistidos).
