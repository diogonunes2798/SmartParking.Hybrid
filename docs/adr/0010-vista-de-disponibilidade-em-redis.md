# ADR 0010 — Vista de disponibilidade em Redis: projetor em Lua, ordem por (`timestamp`, `eventId`) e reconstrução *offline*

- **Estado:** Aceite (especificação 004 aprovada a 2026-11-27; plano da F5a aprovado a 2026-11-27)
- **Data:** 2026-11-27
- **Requisitos:**
  - RF-03: estado de ocupação em tempo real, lido só do Redis;
  - RF-04: índice GEO dos parques (a pesquisa por raio é da F5b);
  - RNF-03: a vista reconstrói-se a partir das fontes de verdade; a duração do *rebuild* mede-se na F12.
- **Decisão (D1–D10):** D3 no valor por omissão (o registo de eventos no MongoDB é a fonte do *rebuild*). D2 não muda: o projetor não tem transporte na F5, e a porta `IOccupancyProjector` é o ponto de extensão da F6. D10: a vista não é um agregado, por isso não tem repositório; a leitura da configuração usa uma porta própria (`IAvailabilityLayoutReader`) com `AsNoTracking`.
- **Secções da tese:** 5.5.3, 5.7.
- **Especificação:** [004-disponibilidade](../specs/004-disponibilidade.md), secções «Domain Model», «Edge Cases» e AC-1 a AC-12 e AC-22.

## Contexto

A F4 grava os eventos de ocupação no MongoDB, mas o UC-03 precisa de saber quantos lugares estão livres por parque, por zona e por tipo, com uma consulta de elevada frequência que não toque no PostgreSQL. A especificação 004 define a vista em Redis (prefixo `avail:`), a regra de ordem e de idempotência por lugar e a reconstrução a partir do PostgreSQL (configuração) e do MongoDB (último evento de cada lugar). Este ADR regista as escolhas de implementação da F5a que a especificação deixa em aberto.

## Decisão

- **Um script Lua por evento.** Leitura do hash do lugar, comparação do par e escrita dos contadores e do pub/sub correm num só script (`Cache/Availability/ApplyOccupancyEvent.lua`). O Redis executa um script de cada vez, por isso dois eventos do mesmo lugar nunca se intercalam (AC-8) e a ida ao Redis é uma só por evento.
- **`timestamp` em milissegundos Unix.** O MongoDB guarda as datas com precisão de milissegundos. O projetor trunca sempre o `timestamp` para milissegundos antes de comparar e de guardar. Assim, o *rebuild* repõe exatamente o mesmo par que a aplicação em direto (AC-10). Dois eventos no mesmo milissegundo desempatam pelo `eventId`, em direto e no *rebuild*.
- **Desempate pelo texto do UUID em minúsculas.** No Lua, a comparação de cadeias é por bytes. No MongoDB, o `_id` é binário de subtipo 4 (`GuidRepresentation.Standard`, *big-endian*), cuja ordem de bytes coincide com a ordem do texto hexadecimal. A ordenação `{ spotId: 1, timestamp: -1, _id: -1 }` do *rebuild* escolhe, portanto, o mesmo evento que o projetor deixaria aplicado.
- **Chaves do parque e da zona construídas dentro do script.** O script recebe só a chave do lugar e constrói `avail:facility:{id}` e `avail:zone:{id}` a partir do hash. Funciona num Redis isolado, que é o do protótipo; num *cluster*, as chaves teriam de partilhar uma *hash tag*.
- **Pub/sub dentro do script.** A mensagem de `availability:changed` sai só numa transição `Free` ↔ `Occupied` e leva os contadores já atualizados. O *rebuild* chama o projetor sem publicar.
- **Reconstrução em Application.** As subcamadas não se podem referir umas às outras (testes de arquitetura), por isso o *rebuild* é um *handler* (`RebuildAvailability`) que compõe quatro portas: índice e último evento (Documents), configuração (Relational) e vista (Cache). O `tools/ProjectionRebuilder` só monta o *host*, liga com o *login* `smartparking_workers` e invoca o *handler*. Um teste de arquitetura confirma que a ferramenta não usa nenhuma biblioteca de base de dados diretamente.
- **Repetição dos últimos eventos em paralelo.** O *rebuild* aplica os últimos eventos com até 32 chamadas simultâneas ao projetor. Como cada lugar tem um só último evento, a ordem entre lugares não importa.
- **Índice do MongoDB à inicialização, sem derrubar o arranque.** Um serviço em segundo plano de `Documents/` cria o índice quando a Api ou o Workers arrancam em modo híbrido; uma falha fica registada em `Warning` e o arranque continua. O *rebuild* cria o índice antes de ler, e aí uma falha termina a execução.
- **Falhas.** Uma falha do Redis, do MongoDB ou do PostgreSQL propaga-se ao chamador. Na ferramenta, termina com o código de saída 4, e a solução é voltar a correr o *rebuild*, que é idempotente.

- **Latitude limitada no domínio (revisão da F5a, 2026-11-27).** O índice GEO do Redis só aceita latitudes em [−85,05112878, 85,05112878], o limite da projeção Web Mercator. Um parque fora desse intervalo faria falhar o `GEOADD` e, com ele, todos os *rebuilds*, deixando a vista parcial. Por decisão do Diogo, o `GeoCoordinate` passou a aceitar só esse intervalo, e a especificação 001 foi revista. Nenhum parque real fica de fora.

## Alternativas consideradas

- **`WATCH`/`MULTI` em vez de Lua.** Também é atómico, mas obriga a repetir a transação quando há conflito e precisa de pelo menos duas idas ao Redis por evento.
- **Guardar o `timestamp` com a precisão de .NET (*ticks*).** A ordem em direto seria mais fina, mas o *rebuild* não a conseguiria reproduzir a partir do MongoDB, e o AC-10 falharia em eventos com fração de milissegundo.
- **Publicar no `IEventBus` já na F5.** Seria decidir D2 antes da F6.
- **Troca atómica da vista durante o *rebuild*** (escrever num prefixo novo e trocar no fim). Fica para rever se as medições do RNF-03 na F12 mostrarem que o estado parcial durante o *rebuild* é um problema (especificação 004, ronda 2).
- **Deixar fora do índice GEO os parques acima do limite**, com um aviso, em vez de limitar o domínio. Manteria a especificação 001, mas esses parques ficariam invisíveis à pesquisa por raio sem que o operador o soubesse.
- **Orquestrar o *rebuild* na própria ferramenta**, com acesso direto às três bibliotecas. Seria mais curto, mas duplicaria o código das subcamadas e tiraria o *rebuild* do alcance dos testes de arquitetura.

## Consequências

- A vista só é correta com o projetor parado durante o *rebuild*. Até à F6 nada invoca o projetor, por isso a premissa verifica-se.
- As alterações de configuração (lugar criado, desativado ou mudado de zona) só chegam à vista no *rebuild* seguinte. Até lá, um evento de um lugar novo dá `unknown_spot`.
- O desempate por `eventId` é arbitrário em relação ao tempo real, mas é determinístico e igual nos dois caminhos.
- A duração do *rebuild* (`availability.rebuild.duration`) e os resultados do projetor (`availability.projector.events`) ficam no `Meter` `SmartParking.Availability`. A ferramenta regista também a duração numa linha de *log*; ainda não exporta métricas por OTLP. Nenhum valor de desempenho é escrito aqui: vem das experiências da F12.
