# ADR-0007 — D3: log de eventos de ocupação imutável no MongoDB, sem reconstrução de agregados

- **Estado:** aceite (valor por omissão de uma decisão «EM ABERTO»; pode ser revisto após a reunião com o orientador)
- **Data:** 15-11-2026
- **Fase:** 3 (UC-01 até ao MongoDB)
- **Relaciona-se com:** ESPECIFICACAO.md §2 (D3), §3 (OccupancyEvent, mapeamento), §4 (UC-01, UC-05), §5 (idempotência, particionamento), §6 (RNF-04, RNF-05); Cap. 2 (2.4.4, «Event Store + Read Store»); ADR-0006

## Contexto

A decisão D3 («Event Sourcing nos eventos de ocupação») está «EM ABERTO». O valor por omissão é um log de eventos imutável em MongoDB (append-only), sem reconstrução de agregados: uma versão mínima do «Event Store + Read Store» do Cap. 2 (2.4.4).

O log recebe o UC-01 a partir do *bus* (ADR-0006), que entrega cada mensagem **pelo menos uma vez**. Tem de:
- nunca guardar o mesmo evento duas vezes (§5);
- permitir consultas por parque e intervalo de tempo (§5, particionamento; UC-04 na Fase 6);
- guardar o `payload` extensível sem migrações (UC-05, RNF-04).

## Decisão

1. **Coleção `occupancy_events`**, base de dados `smartparking` (configurável em `Mongo:Database`). Só se fazem `insertMany`; não há `update`, `replace` nem `delete` (a retenção é a Fase 10). Contrato no `Application`: `IOccupancyEventLog.AppendAsync`.

2. **Forma do documento:**

   | campo | tipo BSON | notas |
   |---|---|---|
   | `_id` | ObjectId | gerado pelo *driver* |
   | `eventId`, `sensorId`, `spotId`, `facilityId` | Binary subtipo 4 (UUID) | legível no `mongosh` como `UUID("…")` |
   | `type` | string | `OCCUPIED` ou `FREED` |
   | `timestamp` | **Date** | relógio do sensor, convertido para UTC; início da medição D7 |
   | `payload` | documento | o objeto JSON do sensor, tal como chegou |
   | `receivedAt` | Date | instante em que a API aceitou o evento |
   | `storedAt` | Date | instante da gravação; `storedAt − receivedAt` é o troço *bus* + log da latência D7 |

   O `timestamp` é gravado como **Date, nunca como texto**. Como texto, a ordenação seria lexical: `2026-11-14T11:00:00+01:00` (10:00 UTC) ficaria depois de `2026-11-14T10:30:00Z`, e o mesmo se passaria com precisões diferentes (`…10:00:00Z` vs `…10:00:00.5Z`). As consultas por intervalo devolveriam resultados errados, mesmo usando o índice composto. O teste `Range_query_by_facility_and_time_uses_the_compound_index_and_orders_by_date` confirma o `IXSCAN` sem fase `SORT`.

3. **Índices**, criados antes da primeira gravação (o *log* espera por eles; se a criação falhar, a gravação falha e o *bus* volta a entregar):
   - `ux_eventId`: `{ eventId: 1 }`, **único**. É a garantia final da idempotência: a gravação é `insertMany` não ordenado, e o erro de chave duplicada (código 11000) conta como «já gravado», não como falha;
   - `ix_facilityId_timestamp`: `{ facilityId: 1, timestamp: 1 }`, o índice de particionamento da §5.

4. **Erros por documento vs. falhas transitórias.** Num `insertMany`, um erro de escrita de um documento concreto (por exemplo, um nome de campo que o motor recuse dentro do `payload`) repete-se em todas as tentativas. Esses eventos são devolvidos como recusados e o *bus* manda-os para a *dead letter* (ADR-0006). Tudo o resto (rede, eleição de primário, *write concern*) é lançado como exceção, e o lote inteiro é repetido.

5. **Sem reconstrução de agregados.** O estado atual de cada lugar não é obtido relendo o log. Vem do *read model* no Redis (Fase 4), alimentado pelo mesmo *bus*. O log serve a auditoria, a analítica (Fase 6) e a retenção (Fase 10).

## Alternativas consideradas

- **`_id` = `eventId`** (sem índice adicional). Poupa um índice: menos espaço (RNF-05) e menos uma atualização de índice por escrita. Rejeitado por agora porque o pedido da fase exige um índice único explícito em `eventId`, e o `_id` gerado pelo *driver* mantém a ordem aproximada de inserção. É uma variante a medir na Fase 10 (armazenamento por 10⁶ eventos).
- ***Event Sourcing* completo** (estado do lugar reconstruído a partir dos eventos, *snapshots*, versão por agregado). Rejeitado: o Cap. 3 não pede reconstrução, a disponibilidade já está no Redis, e a concorrência por versão de agregado obrigaria a serializar escritas por lugar, o que é desnecessário para eventos de sensores.
- ***Time series collection* do MongoDB.** Interessante para o RNF-05 (compressão por intervalo), mas este tipo de coleção não aceita índices únicos fora dos metadados, e a idempotência por `eventId` deixaria de estar no motor. Fica para avaliar na Fase 10.
- **`payload` mapeado para uma classe** (campos conhecidos por geração de sensor). Rejeitado: cada geração nova (UC-05) obrigaria a alterar código. Com o JSON tal como chega, a alteração de esquema não exige migração nem paragem (RNF-04).
- **Tabela particionada no PostgreSQL.** É exatamente a variante monolítica (§7), que serve de termo de comparação na secção 6.5.

## Consequências

- **Positivas:**
  - idempotência no motor, independente do *bus*: o mesmo `eventId` enviado duas vezes dá um documento (`IngestionEndpointTests.Same_eventId_posted_twice_is_stored_once`, `StreamConsumerRecoveryTests.Appending_the_same_record_twice_reports_a_duplicate_not_an_error`);
  - o UC-05 não exige alterações: o teste `Stored_document_has_date_timestamp_uuid_ids_and_the_payload_as_received` grava `temperature`, `airQuality` (aninhado) e `noise` (lista) sem qualquer mudança de código;
  - `receivedAt` e `storedAt` permitem medir um troço da latência D7 sem mudar o formato na Fase 4.
- **Negativas e limites:**
  - **precisão de milissegundos**: o BSON Date não guarda microssegundos (o domínio usa microssegundos, `Guard.Utc`). Para eventos de sensores é suficiente; fica registado para a comparação com a variante monolítica (`timestamptz` guarda microssegundos);
  - **tipos numéricos no `payload` seguem o texto JSON**: `43` fica Int32 e `43.5` fica Double. Um mesmo campo pode ter tipos diferentes entre eventos (o `System.Text.Json` escreve `43.0` como `43`). Quem agregar campos do `payload` (Fases 6 e 7) tem de converter (`$toDouble`) e não pode assumir um tipo único;
  - **durabilidade num *crash* do motor** (revisão da Fase 3, achado 1): o MongoDB do compose é *standalone* com `w:1` e sem `j:true`. O `XACK` pode seguir antes de a escrita estar no *journal* (até cerca de 100 ms). A medir na Fase 9 com `j:true`;
  - dois índices por escrita: custo a medir no `ingest-scale` (Fase 9) e no `retention` (Fase 10);
  - nomes de campos com `$` ou `.` dentro do `payload` dependem das regras do MongoDB 7; se forem recusados, o evento vai para a *dead letter* e não bloqueia os outros.
- `TODO(Diogo):` referência para o padrão *Event Store* / *Event Sourcing* a citar no Cap. 2.4.4 e no Cap. 4; não foi inventada aqui.
