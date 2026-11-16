# SensorSimulator

Gerador de carga reprodutível para o UC-01 (ingestão) e o UC-05 (evolução do esquema dos sensores), conforme a secção 8 da especificação. Fala com a API só por HTTP e não referencia nenhum projeto de `src/` (ADR-0011).

## Pré-requisitos

A API e os Workers têm de estar a correr:

```bash
docker compose up -d
dotnet run --project src/SmartParking.Api
dotnet run --project src/SmartParking.Workers
```

Os Workers são precisos por duas razões:
- o relay da outbox e o projetor de configuração levam ao read model os lugares que o simulador cria;
- sem eles, o simulador espera até `--wait-read-model` segundos e termina com o código 3.

## Utilização

```bash
dotnet run --project tools/SensorSimulator -- --facilities 2 --spots 50 --rate 5 --duration 60 --seed 42
dotnet run --project tools/SensorSimulator -- --scenario bench/scenarios/schema-evolution-pilot.json
dotnet run --project tools/SensorSimulator -- --scenario bench/scenarios/smoke.json --dry-run > seq.ndjson
```

Primeiro lê-se o ficheiro de cenário (`--scenario`). Depois, cada opção da linha de comandos sobrepõe-se ao valor do ficheiro.

| Opção | JSON | Por omissão | Significado |
|---|---|---|---|
| `--facilities` | `facilities` | 1 | n.º de parques; os lugares são repartidos em blocos contíguos |
| `--spots` | `spots` | 100 | n.º **total** de lugares (um sensor cada) |
| `--rate` | `rate` | 1 | taxa média fora de pico, em eventos/s para a cidade toda (chegadas de Poisson) |
| `--peak-factor` | `peakFactor` | 1 | multiplicador da taxa dentro das janelas de pico |
| — | `peakWindows` | `[]` | `[{ "startSeconds", "durationSeconds" }]`; se o fator for maior que 1 e não houver janelas, usa os 10 % centrais da execução |
| `--duration` | `durationSeconds` | 60 | duração em segundos |
| `--gen2-ratio` | `gen2Ratio` | 0 | proporção final de sensores da geração 2 (aceita `0.5` ou `50%`) |
| `--gen2-initial-ratio` | `gen2InitialRatio` | 0 | proporção antes da rampa |
| `--gen2-ramp-start` | `gen2RampStartSeconds` | — | início da rampa; sem este valor, a proporção é constante e igual a `gen2Ratio` |
| `--gen2-ramp-duration` | `gen2RampDurationSeconds` | 0 | duração da rampa linear; 0 = mudança em degrau |
| `--seed` | `seed` | 42 | semente |
| `--run` | `run` | 1 | n.º da execução: muda só os `eventId` (repetições na Fase 9 sem duplicados) |
| `--batch-size` | `batchSize` | 500 | eventos por pedido (máximo da API: 1 000) |
| `--batch-window-ms` | `batchWindowMs` | 100 | período de recolha dos eventos devidos |
| `--concurrency` | `concurrency` | 4 | pedidos em simultâneo |
| `--request-timeout` | `requestTimeoutSeconds` | 10 | prazo de cada pedido; quando expira, os eventos contam como «sem resposta» |
| `--provision` | `provision` | true | cria por RF-06 os parques, lugares e sensores em falta (nomes `sim-<cenário>-F000`, `SIM-00000`) |
| `--wait-read-model` | `waitReadModelSeconds` | 120 | espera até o read model conhecer todos os lugares (0 = não espera) |
| `--free-at-end` | `freeAtEnd` | true | no fim, envia FREED para cada lugar ocupado; cada execução começa e acaba com tudo livre |
| `--dry-run` | — | false | escreve a sequência em NDJSON (com índices em vez de ids) e não envia nada |
| `--api` | `api` | `http://localhost:5080` | URL base da API |

## Regras de geração

- **Reprodutibilidade:** o gerador é o xoshiro256** com uma semente SplitMix64, e não o `System.Random`, cuja sequência não é garantida entre versões do .NET. A mesma semente gera exatamente a mesma sequência: tempos, lugares, tipos, `eventId`, geração e leituras.
- **Fluxos independentes:** as chegadas usam um fluxo, e os limiares, as leituras e os ids usam outros. Mudar a proporção da geração 2 não muda a carga; só alguns eventos passam a ter `payload` mais rico.
- **Coerência:** cada evento alterna o estado do lugar (FREED → OCCUPIED → FREED…). Todos os lugares começam livres, como no read model (ADR-0008).
- **Geração 2:** cada sensor recebe um limiar fixo e passa a gen2 quando a proporção corrente o ultrapassa. Uma vez gen2, nunca volta atrás. O `payload` leva `sensorGeneration` (1 ou 2) e, na geração 2, `temperature` (°C), `airQuality` (índice de 0 a 150) e `noise` (dB(A)). Os valores decimais têm sempre uma casa decimal, para o MongoDB os guardar sempre como double.
- **Tempo:** o `timestamp` é o início da execução mais o desvio virtual do evento. Nunca está no futuro, porque o evento só é enviado depois desse instante.

## Resumo e código de saída

No fim, o simulador imprime:
- os eventos enviados, por geração;
- os aceites (202), os rejeitados (RF-01) e os erros, separando os eventos com resposta diferente de 202/400 dos eventos sem resposta;
- a latência dos pedidos;
- o atraso máximo de despacho e o tempo de espera com o canal cheio;
- o **CPU e a memória do próprio simulador**. Com o simulador e a API na mesma máquina, estes valores mostram se foi o simulador o limite.

Códigos de saída: 0 sem rejeições nem erros; 1 com rejeições ou erros; 2 argumentos inválidos; 3 falha na preparação (provisão ou read model).
