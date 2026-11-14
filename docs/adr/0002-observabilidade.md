# ADR-0002 — Observabilidade: OpenTelemetry, Prometheus e *health checks*

- **Estado:** aceite
- **Data:** 14-11-2026
- **Fase:** 1 (Fundação)
- **Relaciona-se com:** RNF-01, RNF-02, RNF-03, ESPECIFICACAO.md §6, decisão D7, CLAUDE.md («Observabilidade»)

## Contexto

Os Caps. 5 e 6 dependem de medições, nomeadamente:
- a latência p95 das consultas e da ingestão ponta a ponta (sensor → Redis, D7);
- o *throughput* ao escalar os consumidores;
- o comportamento com falha de um nó.

A instrumentação tem de existir desde a primeira fase, para que todas as funcionalidades seguintes sejam medidas da mesma forma. Tem também de ser neutra em relação ao fornecedor. O CLAUDE.md fixa o OpenTelemetry, com exportação para Prometheus ou CSV.

## Decisão

1. **OpenTelemetry (.NET 1.21)** para *traces* e métricas, configurado uma única vez na `SmartParking.ServiceDefaults` e aplicado à API e aos Workers.
   - Recursos: `service.name` (`smartparking-api` ou `smartparking-workers`), `service.namespace=smartparking`, `service.version` e `deployment.environment.name`.
   - O `service.version` é a *InformationalVersion*, que inclui o commit git (por exemplo, `1.0.0+c29dc6f…`). Assim, cada medição fica ligada ao código exato que a produziu. Como o commit só identifica o código se a árvore de trabalho estiver limpa, as experiências têm de correr sem alterações por registar.
   - Instrumentação automática: ASP.NET Core, HttpClient e *runtime* (GC, *thread pool*).
   - Fontes próprias: `ActivitySource` e `Meter` com o nome `SmartParking`. Os nomes estão definidos em `Application/Diagnostics/SmartParkingTelemetry.cs`, que usa apenas `System.Diagnostics`, para que os *handlers* possam emitir *spans* e métricas sem dependerem do OpenTelemetry.
   - Os pedidos a `/health/*` e `/metrics` são excluídos dos *traces* (filtro) e do histograma de latência (`DisableHttpMetrics`), para não poluírem as amostras. Só as métricas de *routing* continuam a contá-los.
   - ***Buckets* explícitos** em `http.server.request.duration`: 1 ms a 10 s, com passos de 25 ms entre 100 e 250 ms. Os *buckets* por omissão saltam de 0,1 s para 0,25 s, o que permitiria um erro de interpolação de até cerca de 150 ms no p95 junto ao alvo de 200 ms (RNF-01, UC-03). A medição tem de ser a mesma desde a primeira fase. O instrumento de latência ponta a ponta (D7) terá *buckets* próprios, centrados em 1 s.
2. **Exportação de métricas para Prometheus** no *endpoint* `/metrics` de cada anfitrião (pacote `OpenTelemetry.Exporter.Prometheus.AspNetCore`, ainda em versão *beta*: 1.21.0-beta.1).
   - O Prometheus 3.16.0 corre no `docker compose`, no perfil opcional `observability`, e recolhe a API (porta 5080) e os Workers (porta 5090) a cada 5 s.
3. **Exportação CSV adiada para a Fase 9** (*benchmarks*). Os CSV de `results/` serão gerados pelos cenários de carga a partir das métricas recolhidas.
4. ***Traces* sem exportador nesta fase.** São criados, mas não enviados. O destino (OTLP com *collector*, ou outro) será decidido quando a latência ponta a ponta for implementada (Fases 3 a 5) e registado num ADR próprio.
5. **Separação entre vivacidade e prontidão:**
   - `/health/live` só verifica o próprio processo;
   - `/health/ready` verifica PostgreSQL, MongoDB e Redis, com um *timeout* de 3 s por verificação, e devolve 503 se algum estiver em baixo.
   - Os detalhes dos erros só aparecem no ambiente Development, porque revelam a topologia.
   - O *check* do Redis é próprio (`RedisHealthCheck`), por causa do problema registado no `DIARIO.md` (14-11-2026). Funciona assim:
     - se o *multiplexer* já sabe que está desligado, responde de imediato;
     - caso contrário, faz um PING assíncrono que respeita o *timeout*.

     A ligação ao Redis abre-se no arranque (`RedisConnectionWarmup`) e não na primeira sonda.

## Alternativas consideradas

- **OTLP com *collector* e Jaeger/Tempo desde já.** Teria mais componentes do que os necessários para a Fase 1. Fica em aberto para os *traces*.
- **Azure Monitor / Application Insights.** A cloud está fora de âmbito (§10).
- **Apenas *logs* e `Stopwatch`.** Não é padronizado, não permite comparar a API com os Workers e obrigaria a reinstrumentar.
- **`AspNetCore.HealthChecks.Redis`.** Foi testado e substituído: não respeita o *timeout* (5,25 s medidos com o Redis parado) e foi compilado contra o StackExchange.Redis 2.x, enquanto o projeto usa o 3.x.

## Consequências

- **Positivas:**
  - Qualquer *endpoint* novo fica medido automaticamente (`http_server_request_duration_seconds`).
  - A falha de qualquer um dos três motores fica visível em `/health/ready` dentro do *timeout* de 3 s. Isto está testado para cada motor e é a base do cenário `failover` (RNF-03).
- **Negativas:**
  - O exportador Prometheus para ASP.NET Core é *beta*. Se mudar de API, o impacto fica limitado à `ServiceDefaults`.
  - Mesmo com *buckets* finos, o p95 calculado pelo Prometheus (`histogram_quantile`) é uma interpolação. Na tese, o valor de referência do p95 será o das amostras em bruto do gerador de carga (k6, D6), e o do Prometheus serve como confirmação. Isto será detalhado na Fase 9.
  - Os *traces* são recolhidos em todos os pedidos, mesmo sem exportador, o que tem custo de CPU. Antes dos *benchmarks* (Fase 9), ou quando o exportador for decidido, a recolha de *traces* passa a poder ser desligada por configuração.
