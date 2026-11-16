# Fase 7 — Revisão independente do código (`af2725a..HEAD`)

- **Data:** 16-11-2026
- **Revisor:** subagente separado, só com leitura do código. Correu `dotnet build`, os testes do simulador (30/30) e várias execuções `--dry-run`, sem enviar carga à API.
- **Verificação pelo Claude da sessão principal:** reproduzi M1, M2 e M3 de forma independente (`--dry-run` e procura no código; ver a coluna «Verificação»).

## Achados

| # | Gravidade | Achado | Verificação |
|---|---|---|---|
| M1 | Média | **Dois eventos do mesmo lugar no mesmo milissegundo.** O gerador garante a alternância de estado, mas não uma distância mínima no tempo. O read model ordena por `ts` em ms e desempata pelo `eventId` (aleatório). Um FREED que fique no mesmo ms do OCCUPIED anterior pode ser tratado como «stale», deixando o lugar ocupado no Redis até ao evento seguinte. | Reproduzido: `d8-reference.json` (semente 42) tem os pares seq 38852→38853 (0,291 ms) e 49192→49193 (0,431 ms). `schema-evolution-pilot.json`: nenhum par abaixo de 1 ms. O efeito no Redis não foi reproduzido (plausível pelo código do `ApplyOccupancy`). |
| M2 | Média | **As fases do piloto estão desfasadas.** `pilot_latency.py` usa a última amostra anterior a cada fronteira. Com amostras de 15 s e deriva, as fases efetivas foram cerca de 0–285 s, 285–345 s e 345–630 s, e não 0–300, 300–360 e 360–630 s, como diz o diário. | Reproduzido: os cortes em 285/345 s dão 4 739 / 986 / 6 747 eventos; o CSV tem 4 745 / 986 / 6 741. Com 300/360 s seriam 4 973 / 993 / 6 506. |
| M3 | Média | **`--provision false` não faz nada:** a provisão corre sempre. | Confirmado: `Provision` só aparece em `ScenarioOptions` (linhas 77 e 239). |
| M4 | Média | **Todos os cenários põem os parques nos mesmos pontos.** O `F000` de cada cenário fica em (40.2033, -8.4103), o mesmo ponto usado nos testes de integração. O UC-03 da Fase 9 contaria parques de cenários anteriores. | Plausível (`Topology.Location` depende só do índice). |
| L1 | Baixa | Exceções sem tratamento: `"peakWindows": null`, `--seed` fora do intervalo de `long`, Ctrl+C durante a provisão. | Indicado pelo revisor como reproduzido. |
| L2 | Baixa | Uma rampa a descer (`gen2InitialRatio` > `gen2Ratio`) é aceite, e os sensores voltam à geração 1, o que contradiz o README e o ADR-0011. | Indicado pelo revisor como reproduzido. |
| L3 | Baixa | `--scenario` sem valor é ignorado em silêncio. | Código. |
| L4 | Baixa | O código de saída é 0 mesmo numa execução «NOT KEPT». | Código. |
| L5 | Baixa | «Accepted» conta os 202 da API, não o que ficou guardado: com o mesmo `--run` repetido, o log descarta os eventos como duplicados. | Código. |
| L6 | Baixa | O teste *golden* não fixa a geração nem as leituras da geração 2. | Código. |
| L7 | Baixa | As contagens do RNF-04 (0 erros, 4 864 pedidos, 3 576 com os 3 campos) só estão no diário, não num CSV versionado. O nome `2026-11-16_pilot.csv` não segue `<AAAA-MM-DD_HHMM>` (§6). | Confirmado. |

## Afirmações nos documentos

- **FREED de fecho:** o diário diz «cerca de 2 470» e a revisão de alocações «cerca de 2 500». O revisor contou 2 449 no `--dry-run`.
- **RNF-04 em `RASTREABILIDADE.md`:** a linha ainda diz «A medição (0 s de indisponibilidade) é a Fase 7», o que contradiz o estado «Parcial» da mesma linha.
- **«0 s» não está demonstrado:** nenhum valor mede indisponibilidade em segundos. O que existe é «0 erros e 0 pedidos sem resposta num único ensaio».
- **D8:** o total de «1,44 milhões/dia» ignora os picos. Convém dizê-lo no Cap. 3.

## O que o revisor considera correto e a manter

- **Fluxos aleatórios:** são independentes (as leituras e os ids não afetam as chegadas).
- **Algoritmos:** *thinning* de Lewis–Shedler, xoshiro256** com SplitMix64, `NextInt` de Lemire e GUID v4.
- **Timestamps:** nunca ficam no futuro.
- **Envio:** despacho com canal limitado, «Scheduled → answer» sem omissão coordenada, e contagem 202/400/erros.
- **Provisão:** idempotente, com paginação estável.
- **MongoDB:** a regra de uma casa decimal evita misturar tipos.
- **Teste de ponta a ponta:** o `SchemaEvolutionTests` usa o próprio simulador.
- **Diário:** os registos honestos («n = 1», «não é possível atribuir a diferença à geração 2») devem manter-se.

## Estado

O Diogo pediu para corrigir todos (16-11-2026). Todos foram corrigidos antes do PR:

| # | Correção | Teste |
|---|---|---|
| M1 | Intervalo mínimo de 1 ms entre eventos do mesmo lugar (`MinGapSeconds`); chegadas mais próximas são descartadas; os FREED de fecho ficam 1 ms depois do fim | `Two_events_of_one_spot_are_at_least_a_millisecond_apart` (cenário D8, semente 42); mutação sem o intervalo → apanhada |
| M2 | `pilot_latency.py` lê o histograma exatamente nas fronteiras (t0 com resolução de 0,2 s) e regista os cortes reais (`cut_*`). O CSV do primeiro piloto mudou de nome (`2026-11-16_1416.csv`) e os rótulos das fases passaram a ser os cortes reconstruídos (0–285, 285–345, 345–630 s). Os valores não mudaram. | Piloto repetido (diário) |
| M3 | `--provision false` só procura e termina com o código 3 se faltar algo | `Provision_false_only_looks_up_and_stops_when_something_is_missing` |
| M4 | Um bloco do mapa por cenário (FNV-1a do nome), longe do ponto dos testes. A espera pelo read model usa a localização real devolvida pela API, para os parques já criados | `Each_scenario_gets_its_own_block_of_the_map` |
| L1 | `peakWindows` nulo é reportado; `--seed` com `TryParse`; a preparação trata `OperationCanceledException`, `InvalidOperationException` e `NotSupportedException` | `Null_peak_windows_are_reported_not_thrown`, `Bad_arguments_are_argument_errors` |
| L2 | Rampa a descer recusada na validação | `Descending_gen2_ramp_is_rejected` |
| L3 | `--scenario` sem valor é um erro | `Bad_arguments_are_argument_errors` |
| L4 | Código de saída 4 para uma execução limpa mas saturada | — (código trivial) |
| L5 | O resumo e o README explicam que «aceite» é o 202 da API | — |
| L6 | O teste *golden* fixa também um evento da geração 2 (leituras) | `Seed_42_produces_the_recorded_sequence` |
| L7 | `--summary-csv` escreve o resumo no formato da §6 (erros, sem resposta, pedidos, CPU…) | `Summary_csv_has_one_header_and_the_rnf04_counts` |
| Docs | RNF-04 na rastreabilidade, «0 s» substituído por «0 erros e 0 pedidos sem resposta», 1,44 milhões/dia «sem contar os picos», nome do cenário validado | — |

**Problema novo encontrado durante a correção:** com a mesma semente e o mesmo `--run`, duas execuções com parâmetros diferentes geravam os mesmos `eventId`. O segundo piloto (`--run 2`) repetiu os ids da verificação a 5 000 ev/s (também `--run 2`): a API aceitou os 12 472 eventos e o MongoDB guardou **0**. Os ids passaram a depender também de todos os parâmetros (`ScenarioOptions.Describe()`), com um teste que confirma que os ids da verificação e os do piloto não se cruzam.

**Piloto de referência:** `results/schema-evolution-pilot/2026-11-16_1514.csv` (limpo: 10 023 eventos guardados por `eventId`, 0 erros, p95 D7 igual nas três fases). Os pilotos das 14:58 e das 15:00 sobrepuseram-se (duas sessões a correr ao mesmo tempo) e a sua latência D7 está contaminada. Bateria completa depois das correções: 255/255. Pormenores no diário da Fase 7.
