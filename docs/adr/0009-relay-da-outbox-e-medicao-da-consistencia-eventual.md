# ADR-0009 — *Relay* da outbox e medição da consistência eventual

- **Estado:** aceite
- **Data:** 16-11-2026
- **Fase:** 5 (sincronização e medição)
- **Relaciona-se com:** ESPECIFICACAO.md §2 (D2, D7), §4 (UC-01 passo 5, UC-02, RF-06), §5 (outbox, idempotência), §6 (RNF-01), §9 (Fase 5); ADR-0002 (observabilidade), ADR-0004 (outbox transacional), ADR-0006 (Redis Streams), ADR-0008 (*read model*); revisão `docs/revisoes/fase-2b-revisao-desempenho-bd.md`, achado 2

> Nota de numeração: o guião da Fase 5 pedia «ADR 0007», mas esse número já estava atribuído à D3 (log de eventos). Este registo é o 0009. É a terceira fase seguida em que isto acontece.

## Contexto

Desde a Fase 2b, os comandos do UC-02 (sessões e pagamentos) e, desde a Fase 4, os do RF-06 (parques e lugares) gravam eventos de domínio na tabela `outbox_messages`, na mesma transação da alteração (ADR-0004). Até esta fase, nada os publicava: a coluna `processed_at` ficava vazia e um parque criado pela API só chegava ao *read model* com uma reconstrução manual (ADR-0008, ponto 4).

A especificação pede três coisas:

1. um *worker* que leia a outbox e publique os eventos, para que o *read model* e os agregados sejam atualizados (§4, UC-02);
2. a medição da **janela de consistência**, o tempo durante o qual o *read model* ainda não reflete uma alteração já confirmada no relacional (Cap. 4.5);
3. a medição da **latência ponta a ponta do UC-01**, do `timestamp` do sensor até à atualização no Redis, com o alvo D7 (< 1 s no p95, RNF-01).

Há quatro restrições:
- o *relay* tem de poder correr em várias instâncias dos Workers (RNF-02, RNF-03), sem publicar a mesma linha duas vezes em funcionamento normal;
- uma paragem a meio de um lote não pode perder eventos (RNF-03) nem aplicar um evento duas vezes no *read model*;
- a revisão da Fase 2b (achado 2) pediu uma decisão sobre as linhas já publicadas e avisou que o *poller* não pode usar um cursor em `occurred_at`;
- cada medição tem de ser justificável na tese, incluindo o relógio usado no início e no fim.

## Decisão

### 1. Reserva de lotes com `FOR UPDATE SKIP LOCKED`

Cada iteração do `OutboxRelay` (Application) faz, numa transação do PostgreSQL (`NpgsqlOutboxStore`, Infrastructure.Relational):

```sql
SELECT id, type, aggregate_id, payload::text, occurred_at, trace_parent
FROM outbox_messages
WHERE processed_at IS NULL
ORDER BY occurred_at, id
LIMIT $1
FOR UPDATE SKIP LOCKED;
-- publicar no IEventBus (D2: Redis Streams)
UPDATE outbox_messages SET processed_at = $1 WHERE id = ANY($2);
COMMIT;
```

- **A transação fica aberta durante a publicação.** Os bloqueios de linha impedem outra instância de reservar as mesmas linhas. `SKIP LOCKED` faz com que essa instância passe às seguintes em vez de esperar. Assim, as instâncias repartem o *backlog*.
- **A leitura é sempre por `processed_at IS NULL`.** Nunca se usa um cursor «`occurred_at` > último lido», porque `occurred_at` é anterior ao *commit*: uma transação mais lenta pode tornar visível uma linha mais antiga do que outras já publicadas (achado 2 da revisão 2b). O índice parcial `ix_outbox_messages_unprocessed` serve a consulta.
- **Entrega «pelo menos uma vez».** Se o processo parar entre a publicação e o `COMMIT`, a transação é desfeita e as linhas voltam a ser publicadas por quem as reservar a seguir. Os consumidores já são idempotentes: o *read model* guarda o `changedAt` de cada parque e de cada lugar e ignora eventos com instante igual ou anterior (ADR-0008).
- **Depois de publicar, o `COMMIT` corre sem cancelamento.** Se o encerramento for pedido durante a publicação, o lote é marcado à mesma, o que evita republicar tudo no arranque seguinte.
- **Ordem.** Dentro de um lote, as linhas seguem `occurred_at`. Entre instâncias concorrentes não há ordem garantida. Isto é aceitável porque cada evento de configuração leva o estado completo e um evento mais antigo que chegue depois é ignorado (ADR-0008).
- **Ciclo** (`OutboxRelayService`, Workers): lotes seguidos enquanto vierem cheios; depois, espera de `Outbox:PollInterval` (100 ms, o mesmo valor dos consumidores do Redis, ADR-0006). Em caso de falha (PostgreSQL ou Redis em baixo), regista o aviso, conta a falha e tenta outra vez ao fim de `Outbox:RetryDelay` (1 s). A configuração é versionada em `appsettings.json` (`Outbox:Enabled`, `BatchSize`, `PollInterval`).

### 2. Linhas processadas: `UPDATE processed_at`, sem `DELETE`

As linhas publicadas ficam na tabela com `processed_at` preenchido. São o registo que permite verificar e medir o *relay* (o teste manual da fase consulta-as) e auditar o que foi propagado. O custo identificado pela revisão 2b (a atualização não é HOT e o *heap* cresce sem limite) fica para a retenção da Fase 10 (RNF-05): apagar as linhas processadas há mais de N dias.

### 3. Encaminhamento por tipo de evento

| Tipos | Tópico (stream) | Consumidor |
|---|---|---|
| `FacilityChanged`, `FacilityDeleted`, `SpotChanged`, `SpotDeleted` | `smartparking:configuration-events` | `ConfigurationProjector` → *read model* (Fase 4) |
| `SessionStarted`, `SessionClosed`, `PaymentRegistered` e qualquer tipo futuro | `smartparking:domain-events` | nenhum por agora; tabelas de resumo da Fase 6 (UC-04, D4) |

O formato da mensagem é o mesmo para todos (`ConfigurationEventMessage`): a linha da outbox tal como está. O plano aprovado dizia `smartparking:session-events`. O nome ficou `domain-events` porque o tópico também leva pagamentos e é o destino por omissão de qualquer tipo novo.

### 4. Janela de consistência

**Definição operacional:** para cada evento da outbox aplicado ao *read model*,

> janela = instante em que o *script* Lua que atualiza o Redis devolve (Workers) − `occurred_at` da linha da outbox (API).

- **Instrumento:** histograma `smartparking.consistency.window` (Prometheus `smartparking_consistency_window_seconds`), com a etiqueta `type`. É registado no `ConfigurationProjector` **só quando o resultado é `Applied`**. Uma reentrega (`Stale`) não conta, porque somaria o tempo até à reentrega a uma atualização que já tinha acontecido.
- **Decomposição:** o histograma `smartparking.outbox.publish_lag` (`smartparking_outbox_publish_lag_seconds`) mede `occurred_at` → publicação no *bus*, para todos os tipos. Para os eventos de sessão e de pagamento, que ainda não têm consumidor, é a única parte medida até à Fase 6.
- **O que está incluído:**
  1. o tempo entre `occurred_at` e o *commit* (o instante do domínio é fixado no comando, antes do `SaveChanges`);
  2. a espera até ao *poll* seguinte do *relay* (até 100 ms);
  3. a publicação;
  4. a espera até ao *poll* do consumidor (até 100 ms);
  5. a escrita no Redis.

  O ponto 1 sobrestima ligeiramente a janela no sentido conservador: a janela real começa no *commit*.
- **Outras métricas do *relay*:** `smartparking.outbox.published` (por `type`), `smartparking.outbox.batches` (por `outcome`) e `smartparking.outbox.pending` (*backlog*, atualizado a cada 5 s).
- **Vieses conhecidos** (revisão de código da Fase 5, achado 8):
  - um evento ultrapassado por outro mais recente do mesmo agregado, por reordenação entre instâncias ou por uma reconstrução que já o incluiu, dá `Stale` e não entra na janela. São os eventos mais lentos, por isso, sob contenção, o p95 é enviesado para baixo;
  - uma linha publicada, desfeita e publicada outra vez entra no `publish_lag` pela segunda publicação, embora o consumidor possa ter aplicado a primeira. O atraso fica sobrestimado.

  Nenhum dos dois casos ocorre com uma só instância e sem falhas, que é a condição das medições de referência.
- **Alvo:** a especificação não define um alvo para a janela. `Observability:Targets:ConsistencyWindowP95` fica vazio. `TODO(Diogo)`.

### 5. Relógios usados nas duas medições

| Medição | Início | Fim |
|---|---|---|
| Janela de consistência | `occurred_at`: `TimeProvider.System` (relógio UTC do sistema) no processo da **API** | `TimeProvider.System` no processo dos **Workers** |
| Latência ponta a ponta do UC-01 (D7) | `timestamp` enviado pelo **sensor** (simulador) | `TimeProvider.System` no processo dos **Workers** |

- **No protótipo**, a API, os Workers e o simulador correm no mesmo anfitrião (`dotnet run`, sem contentores para estes processos). Leem todos o mesmo relógio do sistema, por isso a diferença entre relógios é zero e a medição não precisa de correção.
- **Numa implantação distribuída**, ambas as medições ficariam afetadas pelo desvio entre relógios. Seria preciso sincronização (NTP ou PTP) e reportar o erro máximo, ou então medir num único relógio. Para a D7, o histograma `smartparking.read_model.bus_lag` (receção na API → Redis, Fase 4) é uma medida auxiliar que não depende do relógio do sensor.
- **Condição para a Fase 9** (revisão de código, achado 7): a API e os Workers correm do mesmo lado, ambos no anfitrião ou ambos em contentores. Com o Docker Desktop no Windows, os contentores usam o relógio da VM do WSL2, que se pode desviar do anfitrião (por exemplo, depois de suspender). Um desvio deslocaria a janela e poderia torná-la negativa; as amostras negativas cairiam sem aviso no primeiro *bucket*.
- **Alternativa considerada para a janela:** usar o instante de *commit* do PostgreSQL (`track_commit_timestamp` e `pg_xact_commit_timestamp`). Usaria o relógio da base de dados, um terceiro relógio, e obrigaria a ativar uma opção do servidor. Fica como possível refinamento, se a sobrestimação do ponto 4 se revelar relevante nas medições.

### 6. Latência ponta a ponta do UC-01 e alvo D7 em configuração

- O histograma criado na Fase 4 como `smartparking.read_model.lag` passa a chamar-se `smartparking.ingestion.e2e_latency` (Prometheus `smartparking_ingestion_e2e_latency_seconds`). A medição é a mesma; o nome passa a dizer o que mede, e os *scripts* da Fase 9 dependem dele.
- **Os alvos de latência ficam em configuração** (`Observability:Targets`, nos `appsettings.json` da API e dos Workers):
  - `IngestionEndToEndP95` = 1 s (D7);
  - `AvailabilityQueryP95` = 200 ms;
  - `SessionWriteP95` = 2 s;
  - `ConsistencyWindowP95`: vazio.

  São exportados como o *gauge* `smartparking.target` (Prometheus `smartparking_target_seconds{objective=…}`), para que os painéis e os *scripts* comparem o p95 medido com o mesmo valor. Se a tese fixar a D7 em 2 s, muda-se um valor de configuração.
- Os novos histogramas usam os *buckets* explícitos de 5 ms a 60 s (`ServiceDefaults.PipelineLagBucketsSeconds`), como os da Fase 4.

### 7. Propagação do trace (W3C `traceparent`)

1. **Pedido HTTP:** o `OutboxSaveChangesInterceptor` grava o `Activity.Current` do pedido na nova coluna `outbox_messages.trace_parent` (migração `AddOutboxTraceParent`).
2. **Relay:** abre um span `outbox publish` (`Producer`) por linha, filho desse contexto, e envia o seu próprio `traceparent` no campo homónimo da entrada do stream (ADR-0006).
3. **Consumidor:** o `ConfigurationProjector` abre um span `apply configuration event` (`Consumer`) por evento, filho do span do *relay*. O span por lote do `RedisStreamConsumer` continua a usar *links*, como recomendam as convenções do OpenTelemetry para mensagens em lote.
4. **Sem encadeamento acidental** (revisão de código, achado 2): o `Activity.Current` é limpo à volta de cada span do *relay*. Uma linha sem contexto começa o seu próprio trace, em vez de ficar filha da linha anterior. Os comandos de *polling* do *relay* (marcados com `/* outbox-relay-poll */`) são excluídos dos traces do Npgsql. Sem isso, o *poll* vazio criaria cerca de 10 traces por segundo por instância.
5. **Exportação:** os traces só saem do processo quando `OTEL_EXPORTER_OTLP_ENDPOINT` está definido. O perfil `observability` do compose passa a ter o Jaeger (`jaegertracing/jaeger:2.11.0`, fixado por *digest*; UI em `http://localhost:16686`, OTLP em 4317/4318). Sem a variável, nada muda nas medições.

## Alternativas consideradas

| Alternativa | Porque não foi escolhida |
|---|---|
| `DELETE` das linhas depois de publicar | Perde o registo usado para verificar e medir o *relay*. A retenção com prazo (Fase 10) resolve o crescimento sem perder a auditoria recente. |
| Cursor em `occurred_at` (ler «depois do último publicado») | Perde linhas que ficam visíveis fora de ordem (revisão 2b, achado 2). |
| Uma só instância eleita (*advisory lock* ou *leader election*) | Mais simples quanto à ordem, mas não escala e cria um ponto único de falha (RNF-02, RNF-03). |
| `LISTEN/NOTIFY` em vez de *polling* | Reduz a latência do *poll* (até 100 ms), mas as notificações perdem-se se o *worker* estiver em baixo, por isso o *polling* seria preciso à mesma. Fica como otimização possível, se a janela medida o justificar. |
| CDC com replicação lógica (por exemplo, Debezium) | Exige mais infraestrutura (conector, Kafka ou Debezium Server), fora do valor por omissão da D2. |
| Marcar como processado **antes** de publicar | Perde eventos numa paragem («no máximo uma vez»). A mutação correspondente foi testada: o teste de paragem falha. |
| Medir a janela desde o *commit* (relógio do PostgreSQL) | Ver o ponto 5: terceiro relógio e mais uma opção do servidor; fica como refinamento. |

## Consequências

**Positivas**
- Um parque ou lugar criado pela API chega ao *read model* sem intervenção. O RF-06 deixa de depender de reconstrução manual.
- A janela de consistência e a latência ponta a ponta ficam medidas como histogramas com *buckets* abaixo de 1 s. As duas são calculáveis no Prometheus como p95 (`histogram_quantile`).
- Várias instâncias dos Workers repartem a outbox sem coordenação adicional.

**Negativas e riscos**
- **Bloqueios durante a publicação.** As linhas reservadas ficam bloqueadas enquanto o Redis responde, no máximo durante o *timeout* do cliente (5 s). Cada instância do *relay* ocupa uma ligação do *pool* durante o lote. Este ponto entra no dimensionamento do *pool* da Fase 9 (revisão 2b, achado 1).
- **Duplicados depois de uma paragem.** São possíveis por desenho. São inofensivos para o *read model*, mas os consumidores da Fase 6 (agregados) também terão de ser idempotentes, por exemplo pelo `id` da linha da outbox.
- **Escrita não HOT e crescimento da tabela** até à retenção da Fase 10.
- **O *backlog* distorce os histogramas.** Linhas antigas publicadas de uma vez (por exemplo, depois de os Workers estarem parados) entram no `publish_lag` com o seu atraso real. Nos *benchmarks* da Fase 9, começar com a outbox vazia e os Workers reiniciados, ou descontar o período de aquecimento.
- **Sem quarentena de linhas.** O *payload* é copiado para a mensagem sem nova análise (`WriteRawValue`; revisão de código, achado 1), por isso não há falhas determinísticas por linha. Se isso mudar, uma linha que falhe sempre bloqueia o lote mais antigo, e passa a ser preciso pô-la de quarentena.
- **O stream `smartparking:domain-events` só cresce** até a Fase 6 ter um consumidor. Nos *benchmarks*, esvaziá-lo antes de cada cenário (revisão de código, achado 9).
- **Alvos exportados pelos dois processos:** há duas séries por objetivo, por isso os *scripts* usam `max by (objective)` (achado 11).
- **O primeiro pedido a frio da API alarga a janela.** No teste manual, a primeira janela medida foi 1,32 s e a segunda 0,08 s (diário da Fase 5). Os *benchmarks* precisam de aquecimento.
