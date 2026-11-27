---
paths:
  - "src/SmartParking.Infrastructure/**"
  - "src/SmartParking.Workers/**"
  - "tools/ProjectionRebuilder/**"
---
# Persistência híbrida (Cap. 5.4 a 5.8 da tese)

## PostgreSQL (grupo 1, PC/EC)
- Três agregados: utilizador (utilizador, veículos, meios de pagamento), infraestrutura (parques, lugares, sensores, tarifários) e sessão (sessões, reservas, pagamentos). Normalização até à 3.ª forma normal.
- Restrições explícitas: chaves estrangeiras, unicidade e `CHECK`. As tabelas de auditoria e a outbox não têm chaves estrangeiras.
- Auditoria só por acréscimo (RNF-06): sem `UPDATE` nem `DELETE`; garantir também com permissões do papel da aplicação.
- Cálculo do valor e registo do pagamento na mesma transação (UC-02). O evento de domínio vai para a outbox nessa transação.
- Consultas quentes compiladas (`EF.CompileAsyncQuery`) e com `AsNoTracking` quando só leem.
- RF-04: PostGIS (`geography(Point, 4326)`) com índice GiST.

## MongoDB (grupo 2, PA/EC)
- Coleção do registo de eventos só por acréscimo (Event Sourcing mínimo, D3). Nunca alterar um evento: um facto novo é um evento novo.
- Índice composto `{ facilityId: 1, timestamp: 1 }`, índice único em `eventId` e índice `2dsphere` na localização.
- Cada fase cria o índice da consulta que introduz (especificação 003, ronda 6). F5: `{ spotId: 1, timestamp: -1, _id: -1 }` para o último evento por lugar do *rebuild*, criado de forma idempotente (`OccupancyEventIndexes`). O `_id` já é o `eventId`, por isso não há índice único extra.
- Chave de *sharding* prevista: parque + janela temporal (5.6.1). Não usar a zona.
- Retenção (5.6.3, RNF-05): eventos com mais de 90 dias são agregados, comprimidos e arquivados; o índice TTL só apaga eventos já arquivados.
- Correr em replica set (também no `compose.yaml`) para o cenário de falha de nó (RNF-03).

## Redis (grupo 2, PA/EL)
- Hash por lugar com o estado atual; contadores de lugares livres por parque e por zona; índice GEO dos parques; canal pub/sub de alterações; Redis Streams no barramento (D2).
- O Redis não é fonte de verdade: tudo o que lá está tem de poder ser reconstruído a partir do MongoDB e do PostgreSQL (`tools/ProjectionRebuilder`).
- Sem política de retenção: cada evento substitui o estado anterior.
- Vista de disponibilidade (especificação 004, ADR 0010): todas as chaves com o prefixo `avail:` (lugar, parque, zona, conjuntos de zonas e de parques do operador, GEO). Nenhuma outra funcionalidade escreve nesse prefixo; o *rebuild* apaga-o por `SCAN` + `UNLINK` e não toca noutras chaves.
- Ordem por lugar: aplica-se um evento só se o par (`timestamp` em milissegundos, `eventId` em minúsculas) for estritamente maior do que o guardado. Leitura, comparação e escrita num único script Lua por evento; nunca ler e escrever em idas separadas.
- Contadores (`free`, `free:{SpotType}`) só mudam numa transição `Free` ↔ `Occupied`; os totais só mudam no *rebuild*.
- Canal `availability:changed`: uma mensagem por transição, publicada no mesmo script. O *rebuild* não publica.

## Sincronização (5.6.2)
- Outbox → leitor da outbox → `IEventBus` → projetores. Marcar o evento como publicado só depois da publicação.
- Projetores idempotentes: guardar o último `eventId` ou a versão processada por chave e ignorar repetidos.
- Medir a janela de consistência (do commit ou do `timestamp` do sensor à atualização no Redis) com OpenTelemetry. Objetivo: < 1 s no p95 (RNF-01).

## Modo monolítico (Cap. 7.5)
- Mesmas portas, implementações em PostgreSQL: eventos numa tabela particionada por tempo (`PARTITION BY RANGE`), disponibilidade por consulta, UC-05 com migração de esquema.
