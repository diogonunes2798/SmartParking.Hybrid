# Fase 7 — Revisão de alocações e CPU do SensorSimulator

- **Data:** 16-11-2026
- **Âmbito:** `tools/SensorSimulator` (todos os ficheiros), com foco no caminho por evento, por lote e por pedido.
- **Método:**
  - A skill `memory-allocation-analyzer` pedida no plano da fase não estava instalada nesta sessão. A revisão foi feita por um subagente separado, só com leitura do código.
  - O subagente ficou proibido de compilar ou executar, porque o ensaio piloto do UC-05 estava a correr na mesma máquina.
  - Os custos são **estimativas** feitas a partir do código, não medições. A medição real está no resumo do próprio simulador (diário da Fase 7).

## Conclusão do revisor

Nas cargas previstas, o CPU e as alocações do simulador não são o gargalo. Estimativas do revisor:
- D8 a 167 ev/s: menos de 0,1 % de um núcleo;
- 5 000 ev/s: cerca de 0,5 a 1,5 % de um núcleo.

O gerador e a escrita do JSON não alocam memória gerida em regime estável. Os riscos reais estavam na **forma de medir**.

## Achados e estado

| # | Gravidade | Achado | Estado |
|---|---|---|---|
| 1 | Alta | **Omissão coordenada.** A latência reportada era só o tempo de serviço (desde que o *sender* tira o lote do canal). Ficavam de fora o tempo em fila e o bloqueio do despacho, e com a API saturada o p95 parecia estável. | **Corrigido.** O lote guarda o instante previsto do primeiro evento, e o resumo mostra «Scheduled → answer» (p50/p95/máx), além do tempo de serviço. |
| 2 | Alta (Fase 9) | `Concurrency` limita o débito antes do CPU: cerca de `Concurrency × BatchSize / latência`. Acima disso, o simulador passa a funcionar em ciclo fechado. | **Corrigido em parte.** O resumo marca a execução como «NOT KEPT» (`Saturated`) se o despacho tiver esperado pelo canal ou se o atraso de despacho passar a janela + 50 ms. A fórmula está documentada no `LoadRunner`. Na Fase 9, subir `--concurrency` e descartar as execuções saturadas. |
| 3 | Média | O atraso de despacho era subestimado depois de um bloqueio (`now` lido uma vez por *tick*). | **Corrigido.** `now` é relido depois de cada escrita que bloqueie. |
| 4 | Média | Um *sender* que falhasse (202 com outro corpo) morria em silêncio. Com todos mortos, o despacho ficava bloqueado para sempre no canal cheio. | **Corrigido.** Os erros de análise contam como erros. Uma exceção inesperada num *sender* cancela a execução e fica em «First error». |
| 5 | Média | Ctrl+C perdia o resumo. | **Corrigido.** Imprime um resumo parcial marcado «CANCELLED», e o código de saída não é 0. |
| 6 | Baixa | O lote de FREED final (`FreeAtEnd`, cerca de 2 500 eventos no mesmo instante) entra no máximo da latência e no atraso de despacho. | Documentado. Na Fase 9, usar `--free-at-end false` nas medições, ou separar a fase de limpeza. |
| 7 | Baixa | Os percentis misturam lotes de 1 a 500 eventos e não são comparáveis entre cenários. | Documentado. Nas comparações, usar o mesmo `batchSize` e a mesma janela. |
| 8 | Baixa | `Console.Out.WriteLine` síncrono no despacho: o QuickEdit do Windows pode congelá-lo. | Documentado (desativar o QuickEdit nas execuções de medição). |
| 9 | Info | O pico de memória inclui a provisão; o CPU, as alocações e o GC não incluem. | Documentado no resumo («process lifetime, includes provisioning»). |
| 10 | Info | A reutilização do *buffer* em `SendLoopAsync` é segura (o pedido termina antes de o *buffer* ser reutilizado). | Sem ação. |
| 11 | Info | A fila de latências cresce com o n.º de pedidos (cerca de 288 KB/h com os valores por omissão). | Sem ação. Passar a histograma só se for preciso usar `--batch-size 1` a milhares de pedidos/s. |

Segundo o revisor, não compensa corrigir num protótipo: cópias da *struct* de 72 B, um objeto `Batch` por lote, `Uri` por pedido, nomes de propriedades sem `JsonEncodedText`, `JsonDocument` por resposta e a resolução de cerca de 15,6 ms do `PeriodicTimer` (absorvida pela janela de 100 ms). Os `Stopwatch.StartNew()` por pedido foram trocados por `Stopwatch.GetTimestamp()` na correção do achado 1, porque o código já estava a ser alterado.

## Verificação recomendada pelo revisor

Correr o D8 e um cenário de 5 000 ev/s, e confirmar no resumo:
- CPU abaixo de 2 % de um núcleo;
- `waiting on full channel` = 0;
- atraso de despacho no máximo igual à janela + cerca de 20 ms.

O resultado está no diário da Fase 7.
