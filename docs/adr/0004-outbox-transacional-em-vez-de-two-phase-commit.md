# ADR-0004 — Outbox transacional em vez de *two-phase commit*

- **Estado:** aceite
- **Data:** 15-11-2026
- **Fase:** 2b (UC-02 com outbox)
- **Relaciona-se com:** ESPECIFICACAO.md §4 (UC-02), §5 («Outbox transacional», idempotência), §6 (RNF-01, RNF-03); D2; Cap. 4.5 da tese; ADR-0003 (auditoria por *interceptor*); ADR-0005 (D1)

## Contexto

Os comandos do UC-02 (abrir, fechar e pagar uma sessão) alteram dados relacionais que outras subcamadas têm de conhecer:

- o *read model* de disponibilidade em Redis (UC-03, Fase 4);
- os agregados analíticos (UC-04, Fase 6).

O mesmo comando tem, portanto, de gravar no PostgreSQL **e** fazer chegar um evento a outro sistema (o *bus* da decisão D2). Há dois modos de falhar:

1. gravar a sessão e perder o evento: o *read model* fica errado para sempre;
2. publicar o evento e a transação relacional falhar depois: o *read model* mostra uma alteração que não existe.

A especificação (§5) e o Cap. 4.5 excluem as transações distribuídas (*two-phase commit*, 2PC) e pedem uma outbox transacional.

## Decisão

1. **Tabela `outbox_messages` no PostgreSQL**, na mesma base de dados das sessões:

   | coluna | tipo | papel |
   |---|---|---|
   | `id` | `uuid` (UUIDv7) | identidade do evento; chave de idempotência dos consumidores |
   | `type` | `varchar(100)` | `SessionStarted`, `SessionClosed`, `PaymentRegistered` |
   | `aggregate_id` | `uuid` | sessão que originou o evento (ordem por agregado) |
   | `payload` | `jsonb` | evento serializado (camelCase, enums por nome) |
   | `occurred_at` | `timestamptz` | instante do evento: início da janela de consistência (Cap. 4.5) |
   | `processed_at` | `timestamptz NULL` | nula até o evento ser publicado (Fase 5) |

   Índice parcial `ix_outbox_messages_unprocessed (occurred_at) WHERE processed_at IS NULL`: o *publisher* da Fase 5 lê «pendentes, mais antigos primeiro», e o índice só contém as linhas pendentes.

2. **Eventos de domínio levantados pelo agregado.** `ParkingSession` regista `SessionStarted`, `SessionClosed` e `PaymentRegistered` nas transições `Open`, `Close` e `Pay` (interface `IHasDomainEvents`, sem dependências externas no domínio).

3. **Escrita pelo `OutboxSaveChangesInterceptor`.** Antes de o EF Core enviar as alterações, o *interceptor* retira os eventos pendentes de todos os agregados seguidos e acrescenta uma `OutboxMessage` por evento à mesma unidade de trabalho. Cada comando termina num único `SaveChangesAsync`, que o EF Core executa numa transação. A sessão (ou o pagamento), as linhas de auditoria (ADR-0003) e as linhas da outbox ficam gravadas todas, ou nenhuma.
   - O *interceptor* da outbox é registado antes do de auditoria; as linhas da outbox não são auditadas (o próprio evento já é o registo).
   - Não há `EnableRetryOnFailure`: os eventos são retirados do agregado antes da gravação, e uma nova tentativa teria de os reconstruir (como as linhas de auditoria, diário da Fase 2a).

4. **Conteúdo mínimo dos eventos (RNF-06).** Os eventos levam identificadores, instantes e montantes. O utilizador e o veículo ficam de fora: os consumidores conhecidos (*read model* e agregados) só precisam do lugar.

5. **Publicação fora do âmbito desta fase.** O *worker* que lê a outbox, publica no *bus* (D2: Redis Streams por omissão) e preenche `processed_at` é a Fase 5. A entrega será «pelo menos uma vez», e os consumidores deduplicam pelo `id` (§5).

## Alternativas consideradas

- **Transação distribuída (2PC/XA) entre o PostgreSQL e o *broker*.**
  - Rejeitada:
    - o Redis (Streams) não participa em transações XA, e o Kafka também não as suporta como participante XA;
    - o coordenador bloqueia os participantes enquanto espera, o que contraria a latência alvo do UC-02 (RNF-01) e a disponibilidade com falha de um nó (RNF-03);
    - é a opção que a §5 e o Cap. 4.5 excluem explicitamente.
- **Publicar diretamente no *bus* depois do `SaveChanges` (*dual write*).**
  - Rejeitada: uma falha entre a gravação e a publicação (processo terminado, *broker* indisponível) perde o evento sem deixar rasto. É precisamente o modo de falha 1 do contexto.
- **Publicar antes do `SaveChanges`.**
  - Rejeitada: modo de falha 2 (evento de uma alteração que pode não ser gravada).
- **Captura de alterações (CDC) a partir do WAL do PostgreSQL (por exemplo, Debezium).**
  - Vantagem: dispensa a tabela outbox e o *polling*.
  - Rejeitada para o protótipo: acrescenta um componente (Kafka Connect ou equivalente) que não existe no Cap. 4 e não está na decisão D2. Fica como trabalho futuro. Os eventos passariam a ser alterações de linhas, não eventos de domínio.
- **O *handler* chamar explicitamente `IOutbox.Add(evento)`.**
  - Vantagem: mais explícito.
  - Rejeitada: é fácil esquecer num comando novo, e o esquecimento não é detetado por nenhum teste genérico. Com o *interceptor*, qualquer transição que levante um evento chega à outbox.
- **Outbox no MongoDB.**
  - Só faria sentido se as sessões fossem para o MongoDB (D1, ADR-0005). Com o valor por omissão de D1, a outbox tem de estar no mesmo motor das sessões para partilhar a transação.

## Consequências

- **Positivas:**
  - atomicidade sem transação distribuída: provada por `OutboxAtomicityTests` (falha injetada por um *trigger* do PostgreSQL no `INSERT` da outbox e, no teste espelho, no `UPDATE` da sessão: nada fica gravado);
  - concorrência otimista (`xmin`) cobre também os eventos: um fecho que perde a corrida não deixa evento (`SessionEndpointsTests.Concurrent_close_that_loses_on_the_concurrency_token_leaves_no_outbox_event`);
  - `occurred_at` dá o início da janela de consistência eventual que a Fase 5 mede.
- **Negativas e limites:**
  - consistência **eventual**: o *read model* fica atrasado entre o *commit* e a publicação (a medir na Fase 5);
  - a outbox cresce com o volume do UC-02: é preciso purgar ou arquivar as linhas processadas (Fase 10, RNF-05);
  - mais escrita por comando (uma linha `jsonb` por evento) na mesma transação; o custo entra na medição do RNF-01 (cenário `session-payment`, Fase 9);
  - o *payload* é um contrato entre fases: mudar um evento exige versionar o tipo ou manter compatibilidade nos consumidores.
- `TODO(Diogo):` confirmar a referência bibliográfica do padrão «Transactional Outbox» a citar no Cap. 4.5 (não foi inventada aqui).
