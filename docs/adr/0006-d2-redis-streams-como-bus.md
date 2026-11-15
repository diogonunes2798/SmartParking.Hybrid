# ADR-0006 — D2: Redis Streams como *bus* de eventos, em vez de Apache Kafka

- **Estado:** aceite (valor por omissão de uma decisão «EM ABERTO»; pode ser revisto após a reunião com o orientador)
- **Data:** 15-11-2026
- **Fase:** 3 (UC-01 até ao MongoDB)
- **Relaciona-se com:** ESPECIFICACAO.md §2 (D2, D7), §4 (UC-01), §5 (idempotência), §6 (RNF-01, RNF-02, RNF-03); ADR-0001 (`IEventBus` no `Application`); ADR-0004 (outbox, a outra metade de D2); ADR-0007 (D3)

## Contexto

A decisão D2 («broker de mensagens para ingestão e sincronização») está «EM ABERTO». O valor por omissão é «outbox em PostgreSQL + Redis Streams»; a alternativa é o Apache Kafka, referido no Cap. 2 e ausente no Cap. 4. A outbox já existe (ADR-0004). Faltava o *bus* propriamente dito, que nesta fase serve o UC-01:

1. a API valida o lote (RF-01), publica os eventos e responde `202`;
2. um consumidor grava-os no log do MongoDB (ADR-0007);
3. na Fase 4, um segundo consumidor atualiza o *read model* no Redis; na Fase 5, o *worker* da outbox publica no mesmo *bus*.

Requisitos que pesam: entrega «pelo menos uma vez» com consumidores idempotentes (§5), nenhuma perda com falha de um nó (RNF-03), escalar consumidores (RNF-02) e latência ponta a ponta < 1 s no p95 (D7).

## Decisão

1. **Contrato no `Application`** (`Abstractions/Messaging.cs`):
   - `IEventBus.PublishAsync(topic, mensagens)`: só retorna depois de o *bus* aceitar todas; senão lança `EventBusUnavailableException` (a API responde `503`);
   - `IMessageBatchHandler.HandleAsync(lote)`: o *bus* só confirma o lote depois de o *handler* retornar. Uma exceção significa «falha transitória, entregar de novo». As mensagens que nunca poderão ser processadas são devolvidas como *poison*.
   Trocar para Kafka é escrever outra implementação destes dois contratos, sem tocar na API, no `Application` nem no MongoDB.

2. **Implementação em Redis Streams** (`Infrastructure.Messaging`), no mesmo servidor Redis do *read model* (sem contentor de *broker*), mas com uma ligação própria, aberta de forma assíncrona (`RedisBusConnection`), para que um Redis inacessível não atrase o arranque dos processos (revisão da Fase 3, achado 2):

   | Aspeto | Como |
   |---|---|
   | Tópico | stream `smartparking:occupancy-events`; uma entrada por evento, com os campos `key` (`eventId`), `body` (JSON) e `traceparent` (W3C) |
   | Publicação | um `XADD` por evento, enviados sem esperar pela resposta de cada um: o *multiplexer* junta-os numa ida ao servidor e o Redis mantém a ordem |
   | Consumo | *consumer group* `occupancy-log`, criado com `XGROUP CREATE … 0 MKSTREAM` (lê também o que foi publicado antes de o grupo existir) |
   | Confirmação | `XACK` só depois de o *handler* retornar (depois da gravação no MongoDB) |
   | Recuperação própria | ao arrancar e depois de cada falha, o consumidor lê primeiro as **suas** pendentes (`XREADGROUP … 0`); só depois as novas (`>`). Por isso, o nome do consumidor tem de ser estável entre reinícios |
   | Recuperação alheia | de 30 em 30 s, `XAUTOCLAIM` das entradas paradas há mais de 60 s noutros consumidores (que morreram) |
   | *Poison* | copiada para `<stream>:dead` e confirmada. Uma falha transitória (MongoDB em baixo) **nunca** leva à *dead letter*, por mais vezes que se repita (RNF-03) |
   | Espera | *polling* de 100 ms quando não há nada novo (ver «Consequências») |
   | Retenção | o stream não é aparado (`MAXLEN`) nesta fase: aparar podia apagar entradas que um grupo lento ainda não leu. Fica para a Fase 10 (RNF-05) |

3. **A ingestão não passa pela outbox.** A outbox protege uma escrita relacional e a publicação do evento correspondente (ADR-0004). No UC-01 não há escrita relacional: o `XADD` **é** o ponto de persistência do evento à entrada. Se falhar, a API responde `503` e o sensor volta a tentar; como a gravação é idempotente por `eventId` (ADR-0007), repetir é seguro. Uma outbox no PostgreSQL só para a ingestão poria o motor relacional no caminho de 170 eventos/s em pico (D8), contra a separação proposta no Cap. 4.

4. **Idempotência.** A garantia final está no índice único de `eventId` no MongoDB (ADR-0007). A API só remove duplicados dentro do mesmo lote; não filtra entre pedidos (seria uma ida ao Redis ou ao MongoDB por evento, e não dispensaria o índice).

## Alternativas consideradas

- **Apache Kafka.**
  - A favor: modelo de log particionado com retenção configurável, grupos de consumidores com *offsets*, replicação entre *brokers*; é o padrão de facto para ingestão de grande volume. Escalar consumidores por partição dá paralelismo com ordem por chave.
  - Contra, no contexto do protótipo:
    - um componente a mais (Kafka em modo KRaft, ou com ZooKeeper) que o Cap. 4 não descreve;
    - o volume de referência (D8: cerca de 170 eventos/s em pico) está muito abaixo do ponto em que o Redis Streams deixa de chegar num nó; a medição do RNF-02 (Fase 9) confirma ou desmente isto;
    - o Redis já faz parte da arquitetura (*read model*, pub/sub), por isso o *bus* não acrescenta nenhum motor.
  - Fica como alternativa atrás de `IEventBus`. `TODO(Diogo):` decidir com o orientador se a tese deve justificar a escolha só qualitativamente ou se vale a pena uma medição comparativa.
- **Redis pub/sub.** Rejeitado como *bus*: não guarda as mensagens. Um consumidor parado perde tudo o que foi publicado entretanto, o que contraria o RNF-03. Continua previsto para notificar clientes na Fase 4, onde perder uma notificação não perde dados.
- **Gravar no MongoDB dentro do pedido HTTP (sem *bus*).** Mais simples, mas acopla a latência e a disponibilidade da API ao MongoDB, e obrigaria a API a atualizar também o Redis na Fase 4 (*dual write*). Contraria o passo 2 do UC-01 («publicar no bus e responder 202»).
- **Leituras bloqueantes (`XREADGROUP … BLOCK`).** Eliminariam o *polling*, mas o StackExchange.Redis não as suporta na ligação partilhada (bloqueariam o *multiplexer* para todos os outros comandos). Seria preciso uma ligação dedicada por consumidor. Fica como experiência para a Fase 9, se o *polling* pesar na latência D7.
- ***Dead letter* depois de N entregas.** Era a proposta inicial do plano. Rejeitada durante a implementação: com o MongoDB em baixo, todas as mensagens excederiam N entregas e iriam para a *dead letter*, o que é uma perda de eventos no cenário `failover` (RNF-03). Só vai para a *dead letter* o que o *handler* classifica como impossível de processar (JSON inválido, documento recusado pelo MongoDB).

## Consequências

- **Positivas:**
  - entrega «pelo menos uma vez» verificada por testes: consumidor parado antes do `XACK` e reiniciado (`StreamConsumerRecoveryTests.Consumer_stopped_before_ack_reprocesses_on_restart_without_duplicating`), consumidor morto cujas entradas são reclamadas por outro (`Entry_abandoned_by_a_dead_consumer_is_claimed_by_another`), *poison* que não bloqueia as seguintes (`Malformed_entry_goes_to_the_dead_letter_stream_and_does_not_block_the_next_ones`);
  - grupos independentes: a Fase 4 acrescenta o grupo do *read model* ao mesmo stream sem mexer neste;
  - métricas: `smartparking.bus.consumer.messages` por grupo e resultado (`acked`, `acked_recovered`, `claimed`, `dead_lettered`); `smartparking.bus.consumer.failures` e `smartparking.bus.consumer.consecutive_failures`, que tornam visível um grupo bloqueado por uma falha que se repete (revisão da Fase 3, achado 4); *spans* de consumo ligados aos do produtor pelo `traceparent`.
- **Negativas e limites:**
  - o *polling* acrescenta até 100 ms à latência do UC-01 (metade em média) quando o stream está vazio; sob carga, o consumidor lê sem esperar. A medir na Fase 9 (D7);
  - o stream cresce sem limite até à Fase 10; com 1,4 milhões de eventos por dia (D8), a memória do Redis é o recurso a vigiar;
  - repetir um pedido HTTP volta a publicar o evento (o `XLEN` sobe); a contagem no MongoDB não muda. O custo é um `XADD` e uma escrita recusada pelo índice;
  - a ordem só é garantida por stream e por consumidor; com vários consumidores no mesmo grupo (RNF-02), dois eventos do mesmo lugar podem ser gravados por ordem trocada. O log não depende da ordem (cada documento tem o seu `timestamp`); o *read model* da Fase 4 terá de comparar *timestamps*;
  - **durabilidade num *crash* do motor** (revisão da Fase 3, achado 1): com `appendfsync everysec` (valor por omissão do Redis), o `202` pode sair até cerca de 1 s antes de o `XADD` estar em disco. «Sem perda» (RNF-03) vale para falhas do processo .NET e paragens ordenadas, não para a perda abrupta do contentor do Redis. A medir na Fase 9 com `appendfsync always`;
  - o Redis é ao mesmo tempo *bus* e *read model*: uma falha do Redis para a ingestão (`503`) e as consultas. O cenário `failover` (Fase 9) mede-o.
- `TODO(Diogo):` referências para o Cap. 4 sobre Redis Streams e Kafka (documentação oficial ou literatura); não foram inventadas aqui.
