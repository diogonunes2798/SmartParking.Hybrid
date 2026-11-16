# ADR-0010 — D4: tabelas de resumo em PostgreSQL para a analítica (UC-04)

- **Estado:** aceite (valor por omissão da D4, «EM ABERTO»)
- **Data:** 16-11-2026
- **Fase:** 6 (analítica)
- **Relaciona-se com:**
  - ESPECIFICACAO.md: §2 (D4, D8), §3 (mapeamento «Agregados históricos»), §4 (UC-04), §9 (Fase 6);
  - ADR-0001 (camadas), ADR-0003 (modelo relacional), ADR-0007 (log de eventos em MongoDB, D3), ADR-0008 (ordem dos eventos de um lugar);
  - revisões `docs/revisoes/fase-6-revisao-indices.md` e `docs/revisoes/fase-6-revisao-codigo.md`.

> Nota de numeração: o guião da Fase 6 pedia «ADR 0008», mas esse número já é o do *read model* (Fase 4). Este registo é o 0010. É a quarta fase seguida em que o número do guião está ocupado.

## Contexto

O UC-04 (RF-05) pede um relatório de ocupação por zona e período, com a granularidade de hora, dia, semana ou mês. A especificação diz que o relatório lê das tabelas de resumo e nunca das tabelas operacionais (§4). Diz também que os agregados históricos ficam em PostgreSQL, por isolamento analítico (§3).

A D4 está «EM ABERTO»:
- o valor por omissão são agregados periódicos em PostgreSQL, atualizados por um *worker*;
- a alternativa é um *data lake* ou *lakehouse*, se o orientador quiser manter a secção 2.4.5.

A fonte dos dados é o log imutável de eventos de ocupação no MongoDB (D3, ADR-0007), com o índice `(facilityId, timestamp)`. No cenário D8 entram cerca de 1,4 milhões de eventos por dia.

Antes de escrever código ficaram decididas três coisas com o Diogo:
1. a definição de taxa de ocupação;
2. o fuso horário dos períodos;
3. o denominador da taxa.

## Decisão

### 1. Uma tabela horária por parque, num schema próprio

`analytics.occupancy_hourly` tem uma linha por parque e por hora UTC fechada. A chave primária é `(hour_start, facility_id)`. As colunas são:
- `zone`, copiada do parque no momento da agregação;
- `spot_count`;
- `occupied_seconds`;
- `available_seconds`, que é `spot_count × 3600`;
- `event_count`;
- `aggregated_at`.

O checkpoint do *worker* fica em `analytics.aggregation_checkpoints`.

A tabela fica no schema `analytics` e não tem chaves estrangeiras para as tabelas operacionais (verificado por teste). Assim, o relatório não precisa de *joins*, e as permissões e a carga da analítica ficam separadas das operacionais, o que é útil para o RBAC do RNF-06.

A taxa não é guardada. Guardam-se as somas, e a taxa de qualquer período é a razão das somas.

### 2. Definição da taxa de ocupação

> **taxa de ocupação (zona, período) = Σ minutos-lugar ocupados ÷ Σ minutos-lugar disponíveis**,
> em que os minutos-lugar disponíveis são o n.º de lugares registados × a duração do período.

Para um dia, uma semana ou um mês, somam-se os numeradores e os denominadores das horas; não se faz a média das taxas horárias. Assim, um dia de 23 ou 25 horas e uma hora com poucos lugares pesam o que devem.

- Nas linhas guardam-se segundos inteiros (arredondamento ao segundo, por parque e por hora). A API mostra minutos com duas casas decimais e a taxa com quatro.
- **`TODO(Diogo):`** confirmar esta definição no Cap. 3. Não está na especificação, e o Cap. 3 não está neste repositório.

### 3. Denominador: lugares registados

O denominador é o número de lugares registados no parque (RF-06) no momento da agregação. O campo `Capacity` (valor declarado) não é usado, porque pode não bater certo com os lugares que têm sensor.
- Esse número fica guardado na linha (`spot_count`), para que o valor possa ser explicado mais tarde.
- Só o tempo dos lugares registados conta no numerador, por isso a taxa nunca passa de 1.
- O `event_count` conta todos os eventos do parque na hora, incluindo os de lugares que já não estão registados.

### 4. Regras do cálculo (`OccupancyIntervalCalculator`, código puro)

1. **Estado inicial.** O estado de cada lugar no início do intervalo é o tipo do seu último evento antes desse início, por mais antigo que seja. Só um lugar que nunca teve eventos começa livre. Não há limite para trás: com um limite (a primeira versão tinha 24 h, `StateLookback`), a mesma hora podia ficar ocupada numa agregação e livre noutra, conforme o início da janela, e um carro estacionado um fim de semana inteiro deixava de contar (revisão de código, achado 2).
2. **Ordem dos eventos.** Os eventos de um lugar são ordenados por `timestamp` e depois por `eventId`, a mesma ordem do *read model* (ADR-0008). Dois `OCCUPIED` seguidos contam como uma ocupação contínua; um `FREED` com o lugar livre não muda nada.
3. **Fronteiras.** Um evento exatamente na fronteira de uma hora pertence à hora que começa nesse instante. Uma ocupação que atravessa horas é repartida entre elas.
4. **Ocupação em aberto.** Um lugar ainda ocupado no fim do intervalo conta até ao fim.

Como é código puro, a mesma entrada dá sempre a mesma saída. Isso, junto com o *upsert*, torna a reagregação idempotente.

### 5. *Worker* por janelas fechadas, com *upsert*

O `OccupancyAggregator` (Application) corre a cada `Analytics:Interval` (1 min) no `OccupancyAggregationService` (Workers):

- **Horas fechadas.** Uma hora fica fechada `AllowedLateness` (2 min) depois de acabar. **`TODO(Diogo):`** confirmar com as medições da Fase 9. A justificação é o atraso sensor → MongoDB (`smartparking.occupancy_log.lag` mais o troço até à API), e não a D7, que termina no Redis (revisão de código, achado 4).
- **Só quando fecha uma hora.** Se nenhuma hora nova fechou desde o checkpoint, a execução termina sem ler nada (revisão de código, achado 1). Antes desta correção, as últimas 2 h eram relidas a cada minuto: com a D8, cerca de 94 milhões de documentos por hora, a competir com a ingestão.
- **Reagregação.** Quando fecha uma hora nova, a execução agrega-a e repete as `ReaggregateHours` (2) anteriores, para apanhar eventos atrasados.
  - Um evento com mais de cerca de 2 h de atraso só entra com o comando `aggregate-occupancy` (ver as consequências).
- **Atraso acumulado.** No máximo, uma execução agrega `MaxHoursPerRun` (24) horas. Se houver mais atraso, a execução seguinte começa logo.
- **Primeiro arranque.** Sem checkpoint, agrega as últimas `InitialHours` (24) horas.
- **Escrita.** `INSERT … SELECT unnest(…) ON CONFLICT (facility_id, hour_start) DO UPDATE … WHERE occupancy_hourly.aggregated_at <= EXCLUDED.aggregated_at`, com as linhas e o checkpoint na mesma transação. O checkpoint só avança (`GREATEST`).
  - Várias instâncias dos Workers podem correr ao mesmo tempo: repetir uma janela dá as mesmas linhas, o checkpoint nunca recua, e uma instância lenta não escreve por cima de um resultado mais recente (achado 11).
- **Leitura do MongoDB.** Por parque, duas consultas:
  - estado inicial: `$match` (`timestamp < from`) + `$sort` + `$group`/`$first` sobre um **índice novo** no log, `ix_facilityId_spotId_timestamp_eventId` (`{facilityId: 1, spotId: 1, timestamp: -1, eventId: -1}`). O MongoDB responde com um `DISTINCT_SCAN`, cerca de uma entrada de índice por lugar (verificado por teste com `explain`);
  - eventos do intervalo: um `find` com projeção dos quatro campos, sobre o índice `(facilityId, timestamp)`.
  - O índice novo altera o ADR-0007, que tinha só dois índices no log: cada inserção (cerca de 1,4 milhões por dia com a D8) passa a atualizar três. **O custo fica por medir na Fase 9** (RNF-01 da ingestão, RNF-05 espaço). Se for relevante, a alternativa é guardar o estado de cada lugar no fim de cada janela (`analytics.spot_state`), sem tocar no log.
- **Comando manual.** `dotnet run --project src/SmartParking.Workers -- aggregate-occupancy --from … --to …` agrega um intervalo de horas já fechadas, sem mexer no checkpoint. Recusa horas abertas ou futuras (achado 9). Divide o intervalo em fatias de `MaxHoursPerRun` horas, cada uma com a sua transação, para limitar a memória (achado 3). Serve para preencher dados passados e para os *benchmarks* (Fase 9).

### 6. O relatório só lê `analytics.occupancy_hourly`

`GET /api/reports/occupancy?zone=&from=&to=&granularity=hour|day|week|month`:

- **Pedido.** `from` é inclusivo e `to` exclusivo; os dois têm de ser horas inteiras em ISO 8601 (sem fuso, são UTC). `granularity` é `hour` por omissão. `zone` é opcional: sem zona, o relatório devolve uma série por zona.
- **Limites.** O intervalo vai até 31 dias com `hour` e até cerca de 3 anos nas outras granularidades. **`TODO(Diogo):`** valores provisórios.
- **Consulta.** Uma única consulta SQL (`EfOccupancyReportQueries`), com duas variantes (com e sem zona):
  `SELECT zone, date_trunc(unit, hour_start, tz), count(DISTINCT hour_start), sum(…) FROM analytics.occupancy_hourly WHERE … GROUP BY zone, 2`.
- **Resposta.** Cada período devolve o início, o fim, `hours` (horas agregadas), `expectedHours` (horas do período dentro de `[from, to)`), `events`, `occupiedMinutes`, `availableMinutes` e `occupancyRate`. Quando `hours < expectedHours`, o período ainda não está todo agregado.
- **Isolamento verificado.** `OccupancyReportTests.The_report_reads_only_the_summary_tables` capta os eventos `CommandExecuted` do EF Core e falha se aparecer outra tabela; a mutação que junta `parking_spots` à consulta faz o teste falhar. O teste manual faz o mesmo no log (`Microsoft.EntityFrameworkCore.Database.Command`).

### 7. Fuso horário: horas UTC, calendário de Lisboa

- **Armazenamento.** Tudo é guardado em UTC, e cada linha é uma hora UTC.
- **Hora, dia, semana e mês.** São cortados no calendário de `Reports:TimeZone`, que é **Europe/Lisbon** por omissão. A semana é ISO e começa à segunda-feira. O início usa `date_trunc(unit, ts, 'Europe/Lisbon')` e o fim usa `date_add(início, '1 unit', 'Europe/Lisbon')`, ambos do PostgreSQL 16, para que venham da mesma base de dados de fusos (achado 6: no Windows, o `TimeZoneInfo` de Lisboa segue as regras de «GMT Standard Time» e diverge, por exemplo, em 1993).
- **Mudança de hora.** Lisboa só muda de hora em horas inteiras, por isso cada dia local é um conjunto exato de horas UTC:
  - 29-03-2026 tem 23 horas;
  - 25-10-2026 tem 25 horas.

  Com a razão das somas, a taxa desses dias fica exata. Há testes para os dois dias.
- **Arranque.** Um fuso cujo desvio não seja em horas inteiras é recusado no arranque.

## Alternativas consideradas

| Alternativa | Porque não (por agora) |
|---|---|
| *Data lake* ou *lakehouse* (Parquet em armazenamento de objetos, com Spark ou DuckDB) | Acrescenta um motor e um formato que o resto do protótipo não usa, para um volume (uma linha por parque e por hora; 50 parques sintéticos dão 50 linhas por hora) que uma tabela relacional trata em milissegundos. Continua possível atrás da mesma interface, `IOccupancyReportQueries`, se o orientador quiser manter a secção 2.4.5. É a alternativa da D4. |
| Calcular o relatório no momento, a partir do MongoDB | Contraria a especificação («nunca lê… o MongoDB»), põe a analítica a competir com a ingestão no mesmo motor, e cada relatório anual teria de percorrer cerca de 500 milhões de eventos. |
| Vista materializada em PostgreSQL | Os eventos não estão no PostgreSQL. Mesmo que estivessem, o `REFRESH` recalcula tudo e não trata janelas fechadas nem eventos atrasados. |
| *Continuous aggregates* do TimescaleDB | Seria uma extensão nova só para isto; os eventos estão no MongoDB (D3), não numa *hypertable*. |
| Guardar a taxa horária e fazer a média | A média das taxas está errada em dias de 23 ou 25 horas e em parques com tamanhos diferentes. |
| Tabelas diárias, semanais e mensais, além da horária | É mais escrita e mais estado para manter coerente. As consultas sobre a tabela horária, com índices, chegam (revisão de índices). Se a Fase 9 mostrar o contrário, ficam como ponto de extensão. |
| Agrupar dias em UTC | É mais simples, mas o «dia» deixa de ser o dia do município. Fica disponível mudando `Reports:TimeZone` para `UTC`. |

## Consequências

- **Positivas:**
  - o relatório não toca nas tabelas operacionais nem no MongoDB (isolamento pedido pelo RF-05 e pelo §4), e isso é verificado por teste;
  - a reagregação é idempotente e várias instâncias dos Workers podem correr sem coordenação;
  - a taxa tem uma definição única e explicável (`spot_count` guardado), exata nos dias de mudança de hora;
  - cada período mostra se está completo (`hours` vs. `expectedHours`).
- **Limitações (para a tese):**
  1. **Configuração atual.** O denominador e a zona são os do momento da agregação, e o mesmo vale para um preenchimento manual de dados passados: um lugar criado hoje conta para as horas de ontem se essas horas forem agregadas de novo. Os dados de configuração não têm histórico.
  2. **Sensor avariado.** Um lugar cujo sensor deixa de emitir fica com o último estado conhecido para sempre (um `OCCUPIED` sem `FREED` conta como ocupado). Não há forma de o distinguir de uma estadia longa sem *heartbeats*. **`TODO(Diogo):`** confirmar se os sensores (e o simulador da Fase 7) emitem só mudanças de estado ou também *heartbeats*.
  3. **Eventos muito atrasados.** Um evento com mais de `AllowedLateness` + `ReaggregateHours` de atraso só entra com o comando `aggregate-occupancy`, e não há métrica que o assinale (achado 4). No cenário `failover` (RNF-03), o *script* da Fase 9 tem de reagregar o período afetado.
  4. **Memória.** Os eventos de um parque numa fatia de 24 h são lidos para memória: com um parque de 1 000 lugares e a D8, cerca de 290 000 eventos. Não se fez leitura em *streaming* (achado 3, em parte).
  5. **Períodos sem dados.** Um período sem nenhuma linha não aparece na resposta, e uma zona inexistente dá uma lista vazia (achado 8). Um período com `hours < expectedHours` está incompleto.
  6. **Denominador.** Contam todos os lugares registados, com ou sem sensor `Active` (achado 10). **`TODO(Diogo):`** decidir no Cap. 3.
  7. **Frescura.** Um evento só aparece no relatório entre cerca de 3 e 63 minutos depois de acontecer (fim da hora, mais a margem de 2 min, mais o intervalo de 1 min). É medido pelo *gauge* `smartparking.analytics.staleness`.
- **Métricas (meter `SmartParking`):**
  - `smartparking.analytics.runs` (`outcome`);
  - `.rows`;
  - `.duration` (custo da camada analítica, secções 6.2 e 6.5);
  - `.staleness`.
- **Ponto de extensão da D4:** `IOccupancySummaryStore`, `IOccupancyReportQueries` e `IAnalyticsFacilitySource` (Application). Trocar para um *lakehouse* obriga a novas implementações, mas o *endpoint* e o cálculo ficam como estão.
- **Fora desta fase:** o consumidor de `smartparking:domain-events` (sessões e pagamentos), que a Fase 5 tinha deixado para a Fase 6. Por decisão do Diogo (16-11-2026), o UC-04 é sobre a ocupação. O stream continua a acumular; ver o diário.
