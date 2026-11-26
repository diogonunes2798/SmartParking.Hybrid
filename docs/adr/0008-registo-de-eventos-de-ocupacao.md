# ADR 0008 — Registo de eventos de ocupação: escrita síncrona no MongoDB, só por acréscimo

- **Estado:** Aceite (especificação 003 aprovada a 2026-11-25; plano da F4a aprovado a 2026-11-25)
- **Data:** 2026-11-25
- **Requisitos:**
  - RF-01: os eventos válidos são persistidos sem perda; os repetidos (mesmo `eventId`) são ignorados; os eventos sem os campos mínimos são rejeitados;
  - RNF-03: o sensor só recebe a confirmação depois de o evento estar gravado de forma durável;
  - prepara o UC-05 (`payload` extensível).
- **Decisão (D1–D10):** D3 (registo imutável de eventos em MongoDB), no valor por omissão. D2 não muda: a ingestão não usa o `IEventBus`, e a propagação para o Redis decide-se na F6.
- **Secções da tese:** 5.5 e 5.9.4.
- **Especificação:** [003-ingestao](../specs/003-ingestao.md), secções «Registo de eventos no MongoDB», «Integrations», «Edge Cases» e AC-1 a AC-18.

## Contexto

A F4 liga o *gateway* de cada parque ao registo de eventos. A especificação pede três garantias:
- o *gateway* só recebe a confirmação depois de o evento estar gravado de forma durável;
- o `eventId` decide sozinho a idempotência;
- o registo nunca altera nem apaga um evento.

O briefing da sessão de implementação propunha outro desenho, que a especificação aprovada não segue:
- a Api publicaria num Redis Stream e responderia 202;
- um *worker* consumiria o *stream* e gravaria no MongoDB;
- haveria índices `{facilityId, timestamp}`, um índice único em `eventId` e um índice `2dsphere`.

Por decisão do Diogo, a especificação prevaleceu. Este ADR regista porquê.

## Decisão

- **Escrita síncrona antes da resposta.** O `POST /api/ingest/occupancy-events` valida o lote contra o registo e grava os eventos aceites com um `insertMany` antes de responder 200. Por lote há uma consulta ao PostgreSQL e uma escrita no MongoDB.
- **`_id = eventId`.** A unicidade vem do índice obrigatório do `_id`, sem índice extra. Um erro E11000 numa escrita conta como `duplicate`. Os repetidos dentro do mesmo lote são retirados antes da escrita, porque um `insertMany` sem ordem não garante a ordem de inserção, e a especificação diz que ganha o primeiro pela ordem do pedido.
- **`insertMany` sem ordem (`ordered: false`).** Um duplicado não impede a gravação dos outros eventos do lote. Qualquer outro erro, ou um erro de *write concern*, dá 503 com `Retry-After` ao lote todo. Os eventos já gravados voltam como `duplicate` quando o *gateway* repetir o envio.
- **Durabilidade: *write concern* `majority` com *journal*,** fixado na coleção que a ingestão usa (`OccupancyEventsCollection`, AC-14). O custo desta escolha não foi medido; mede-se na F12.
- **Coleção normal, não *time-series*,** porque as coleções *time-series* não permitem índices únicos.
- **Só o índice `_id` na F4.** Cada fase cria o índice da consulta que introduz, para que o custo de escrita de cada índice fique atribuído à fase que o justifica:
  - F5/F6: por lugar e tempo;
  - F7: por zona e período;
  - F10: o TTL.

  A regra geral em `.claude/rules/persistencia.md` lista os índices finais do registo; a especificação 003 (ronda 6) define em que fase cada um entra.
- **Só por acréscimo, verificado no código.** A porta `IOccupancyEventLog` só tem `AppendAsync`. Um teste de arquitetura lê com Mono.Cecil o IL da subcamada `Documents/` e falha se houver uma chamada a `Update*`, `Replace*`, `Delete*`, `FindOneAnd*` ou `BulkWrite*` da API da coleção (AC-15).
- **Contexto do lugar no momento da ingestão.** O documento guarda o `facilityId` e o `zoneId` do lugar lidos na consulta de validação. O histórico não muda quando o lugar passa para outra zona (AC-10).
- **Modo monolítico.** Até à F9, o *endpoint* responde 501 antes de qualquer leitura ou escrita.

## Alternativas consideradas

- **Redis Streams entre a Api e um *worker* consumidor**, que é o desenho do briefing.
  - A favor: a Api responderia antes da escrita no MongoDB, e o pico seria absorvido pelo *stream*.
  - Contra: a confirmação ao sensor chegaria antes da escrita durável no registo, o que contraria o RNF-03 tal como a especificação o lê. A durabilidade passaria a depender da persistência do Redis (AOF e `fsync`), e haveria um segundo ponto de idempotência, o *ack* do consumidor.
  - Fica disponível como alternativa a medir na F12 se o RNF-01 falhar; seria uma revisão da especificação 003.
- **Publicar no `IEventBus` além de gravar.** A especificação deixa para a F6 o mecanismo pelo qual os projetores leem os eventos novos (*change streams* ou outro). Publicar já seria decidir D2 para este fluxo antes do tempo.
- **Índice único em `eventId` com `_id` gerado.** Daria um segundo índice com custo de escrita sem nenhuma consulta que o use.
- **Coleção *time-series*.** Compressão e organização por tempo, mas sem índice único: a idempotência teria de ser verificada à parte.
- **`insertMany` ordenado.** Pararia no primeiro duplicado e obrigaria a repetir a escrita dos restantes.

## Consequências

- A latência da ingestão inclui a escrita com `majority` e *journal*. O histograma `ingestion.append.duration` do `Meter` `SmartParking.Ingestion` mede-a; os valores vêm das experiências da F12 e não deste ADR.
- Uma falha a meio do lote pode deixar parte dos eventos gravados. A repetição é segura, mas a resposta a essa repetição conta-os como `duplicate`, e não como `accepted`.
- O *replica set* local tem um nó, por isso `majority` corresponde a esse nó. O cenário de falha de nó (RNF-03) só se exercita com mais membros.
- O `payload` é convertido para BSON estruturalmente (`BsonPayload`), sem `BsonDocument.Parse`. Essa função lê JSON estendido do MongoDB e transformaria `{"$date": …}` numa data, ou falharia com 500 em chaves repetidas. Por isso, a Api só aceita `payload` em JSON portável (I-JSON, RFC 7493): nomes únicos e números que caibam num `double` finito, sem nomes começados por `$` ou com o carácter nulo. O resto é uma falta de esquema (400). Os inteiros ficam `int32` ou `int64`, e os outros números ficam `double`.

## Referências

- Especificação 003, rondas 1 a 7 do `/spec`.
- ADR 0003 (D10: portas por subcamada e por `Persistence:Mode`).
