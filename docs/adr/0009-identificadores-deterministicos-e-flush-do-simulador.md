# ADR 0009 — Simulador: `eventId` determinístico com o instante de arranque e *flush* contado desde o evento mais antigo

- **Estado:** Aceite (pedido pelo Diogo a 2026-11-26, no fecho dos achados da revisão da F4b)
- **Data:** 2026-11-26
- **Requisitos:**
  - RF-01: a idempotência da ingestão depende só do `eventId`;
  - AC-20 (reprodutibilidade) e AC-23 (envio) da especificação 003;
  - reprodutibilidade das medições do Cap. 7: a mesma `--seed` tem de dar a mesma carga em todas as execuções (`.claude/rules/medicoes.md`).
- **Decisão (D1–D10):** nenhuma muda. O cenário de referência (Tabela 9) é o valor por omissão do simulador.
- **Secções da tese:** proposta 5.9.4 e 7.3. `TODO(Diogo):` confirmar.
- **Especificação:** [003-ingestao](../specs/003-ingestao.md), secções do simulador e AC-19 a AC-24.

## Contexto

O simulador (`tools/SensorSimulator`) é a fonte de carga das experiências da F12. Cada cenário corre pelo menos 5 vezes por modo, com a mesma `--seed`. Duas escolhas da F4b não estão explícitas na especificação e condicionam essas medições:

1. **Como se constrói o `eventId`.** A especificação pede identificadores determinísticos «para a mesma *seed* e o mesmo relógio» (AC-20). O registo de eventos ignora um `eventId` repetido (RF-01, ADR 0008). Se o `eventId` dependesse só da *seed*, a segunda execução de um cenário chegaria toda como duplicada: a Api mediria um caminho diferente (só a deteção de duplicados) e o registo não cresceria.
2. **De onde conta o tempo de espera de um lote.** A especificação diz que o *gateway* envia um lote «ao chegar a 500 eventos ou ao passar 1 s» (AC-23), mas não diz desde quando se conta esse segundo. No cenário de referência (cerca de 17 eventos/s em média) um lote de 500 nunca enche, por isso a regra do tempo decide quase todos os envios das corridas reais.

## Decisão

- **`eventId` = UUID versão 8 (RFC 9562) a partir do SHA-256 de `seed|início|hardwareId|sequência`.**
  - `início` é o instante de arranque da corrida em *ticks* UTC, truncado ao milissegundo;
  - `sequência` é o número de ordem do evento dentro do lugar;
  - o texto é formatado com a cultura invariante, para que uma *seed* negativa dê os mesmos identificadores em qualquer máquina (achado 6 da revisão da F4b);
  - ficam os 16 primeiros *bytes* do resumo, com os bits de versão (8) e de variante (10) acertados.

  A mesma *seed* com o mesmo relógio dá a mesma sequência (AC-20). Duas execuções começadas em instantes diferentes não colidem no registo.
- **O *flush* por tempo conta desde o evento mais antigo do lote.** O `BatchAccumulator` guarda o instante em que entrou o primeiro evento do lote e entrega o lote quando passa o `--flush-interval` desde esse instante, ou quando chega ao `--batch-size`, o que acontecer primeiro. Nenhum evento fica no acumulador mais do que o `--flush-interval`. Depois disso, o lote pode ainda esperar na fila do *gateway* atrás de um envio que está a repetir depois de um 503, porque cada *gateway* envia um lote de cada vez. O instante é passado por quem chama. Por isso, a contagem desde o evento mais antigo é testada com o relógio falso (AC-23). Desde o fecho da revisão, um teste em tempo real (`Gateway_LowRate_FlushesByTimeBeforeBatchIsFull_AC23`) confirma também que o `GatewayRunner` envia lotes por tempo antes de terem 500 eventos.

## Alternativas consideradas

- **UUID versão 7 ou versão 4 aleatórios.** Não são reprodutíveis: duas gerações com a mesma *seed* dariam identificadores diferentes, o que contraria o AC-20.
- **`eventId` só da *seed*, sem o instante de arranque.**
  - A favor: a carga seria idêntica byte a byte entre execuções.
  - Contra: a partir da segunda execução todos os eventos chegariam como duplicados. Para medir de novo seria preciso limpar o registo entre execuções, e um esquecimento produziria medições de outro caminho sem nenhum erro visível.
- **Contador global de eventos em vez de `hardwareId` e sequência por lugar.** A sequência de cada lugar deixaria de ser independente dos outros lugares: mudar o número de lugares mudaria os identificadores de todos.
- ***Timer* periódico de *flush* (a cada `--flush-interval`, independente dos eventos).** Um evento que entrasse logo a seguir a um *flush* esperaria quase dois intervalos. Além disso, o teste do AC-23 dependeria do relógio real.

## Consequências

- Repetir uma execução com a mesma *seed* noutro instante gera eventos novos, que a Api aceita. Para reproduzir exatamente uma sequência, é preciso fixar também o relógio, como fazem os testes do AC-20.
- O instante de arranque aparece no *log* do simulador (`Emitting for … from {Start:O}`). Com a *seed* e o mapeamento, permite reconstruir os identificadores de uma corrida.
- O `eventId` não tem significado temporal (não é um UUID versão 7). A ordem temporal vem do `timestamp` do evento.
- A espera de um evento no acumulador está limitada pelo `--flush-interval`; a fila de envio e as repetições depois de um 503 acrescentam-se a essa espera. As métricas de ingestão da Api não incluem nenhuma das duas.

## Referências

- Especificação 003, AC-20 e AC-23.
- ADR 0008 (o `eventId` como `_id` do registo de eventos).
- RFC 9562 (UUID), secção da versão 8.
- `docs/DIARIO.md`, entradas da F4b e do fecho da revisão.
