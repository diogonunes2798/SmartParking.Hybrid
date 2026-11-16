# ADR-0011 — Evolução do esquema dos sensores: `payload` extensível, sem migrações síncronas (UC-05)

- **Estado:** aceite
- **Data:** 16-11-2026
- **Fase:** 7 (simulador e evolução de esquema)
- **Relaciona-se com:** ESPECIFICACAO.md §3 (OccupancyEvent, `payload` extensível), §4 (UC-05), §6 (RNF-04), §7 (baseline: o UC-05 com migração), §8 (simulador), §2 (D8); ADR-0006 (bus), ADR-0007 (log append-only), ADR-0008 (read model)

> Nota de numeração: o plano da Fase 7 referia «ADR 0009». Esse número já pertence ao relay da outbox (Fase 5), por isso este registo é o 0011.

## Contexto

O UC-05 introduz uma segunda geração de sensores. Além da ocupação, estes sensores enviam `temperature`, `airQuality` e `noise`. O RNF-04 exige que a mudança aconteça sem paragem: 0 s de indisponibilidade, medidos no cenário `schema-evolution` da Fase 9. Para a secção 6.5, a variante monolítica faz a mesma alteração com uma migração de tabela (§7).

Há duas formas de acomodar campos novos:
- **esquema explícito:** colunas ou propriedades tipadas, alteradas por migração antes de os sensores novos entrarem;
- **esquema extensível:** um contentor (`payload`) que os componentes transportam sem o interpretar. Só o leitor que precisa de um campo o lê.

Até à Fase 6, o `payload` já era um objeto JSON opaco em todo o caminho do UC-01. Faltava provar, com carga real e uma transição a meio da execução, que nenhum componente o rejeita, o trunca ou o perde.

## Decisão

1. **O `payload` é o ponto de extensão, e o esquema mínimo (RF-01) não muda.** Os campos de topo validados continuam a ser `eventId`, `sensorId`, `spotId`, `facilityId`, `type` e `timestamp`. O que for específico de uma geração vai dentro de `payload`, e o seu conteúdo não é validado (`OccupancyEventValidator`). As propriedades de topo desconhecidas são ignoradas, por isso um campo novo fora do `payload` não chega ao log. A regra para os fabricantes é pôr os campos novos em `payload`.

2. **Os componentes intermédios não interpretam o `payload`:**

   | Componente | O que faz com o `payload` |
   |---|---|
   | API (`OccupancyEventValidator`) | verifica só que é um objeto JSON |
   | Bus (`OccupancyEventMessage`) | `JsonElement` transportado tal como chegou |
   | Log (`MongoOccupancyEventLog`) | `BsonDocument.Parse` do texto original; sem classe nem mapeamento |
   | Read model (`AvailabilityProjector`) | ignora-o: só usa o lugar, o tipo, o `timestamp` e o `eventId` |
   | Analítica (`OccupancyAggregator`) | ignora-o: as projeções do MongoDB só pedem os campos de topo |

   Uma nova geração de sensores não exige alterações de código nem migrações nesta cadeia. Exige só que os sensores comecem a enviar os campos.

3. **Não há migrações síncronas.** Os documentos antigos (geração 1) ficam como estão, e cada documento traz a forma da sua geração. Um leitor futuro dos campos ambientais (por exemplo, um relatório de qualidade do ar) tem de tratar a ausência do campo como «sensor sem essa medição». Não pode tratar um campo em falta como erro.

4. **A geração vai no `payload`** (`sensorGeneration`: 1 ou 2), escrito pelo simulador. Permite contar e filtrar por geração no log (`db.occupancy_events.countDocuments({"payload.sensorGeneration": 2})`) sem cruzar com o registo de sensores do relacional. O modelo de sensor no RF-06 (`Sensor.Model`) não é alterado durante a transição. Atualizá-lo seria uma alteração de configuração independente da evolução do esquema.

5. **Tipos numéricos estáveis.** O MongoDB guarda `21` como Int32 e `21.0` como Double (diário da Fase 3). O simulador escreve sempre os valores decimais com uma casa decimal, para que cada campo tenha um único tipo BSON. Um leitor de dados de sensores reais não pode assumir o mesmo. Tem de aceitar Int32, Int64 e Double (`$toDouble` nas agregações).

6. **Demonstração com o `tools/SensorSimulator`.** A proporção de sensores da geração 2 sobe durante a execução (`gen2InitialRatio` → `gen2Ratio`, numa rampa). Cada sensor tem um limiar fixo dado pela semente e, uma vez na geração 2, não volta atrás, como numa substituição gradual de sensores. Uma rampa a descer (proporção inicial maior do que a final) é recusada na validação (revisão de código da Fase 7, L2). As chegadas usam um fluxo aleatório separado do das leituras. Assim, a carga (instantes, lugares, tipos, ids) é a mesma qualquer que seja a proporção da geração 2, e a única diferença entre execuções comparáveis é o `payload`.

7. **O simulador não referencia `src/`.** Copia apenas os contratos HTTP de que precisa. Exercita o contrato público, tal como um sensor ou um *gateway* de terceiros.

## Alternativas consideradas

- **Propriedades tipadas para cada geração (`OccupancyEventGen2`) com versão de esquema no topo.** Daria validação forte dos campos ambientais, mas cada geração nova obrigaria a mudar o código da API, do bus e do log e a fazer uma nova implantação coordenada. É o oposto do que o RNF-04 pede. A validação pode ser acrescentada mais tarde num leitor específico, sem tocar no caminho de ingestão.
- **Validação por JSON Schema do MongoDB (`$jsonSchema`) com `validationLevel: moderate`.** Permitiria documentos antigos e novos, mas cada geração nova exigiria um `collMod` antes de os sensores enviarem dados. Se o `collMod` falhasse, o log rejeitaria os eventos novos. Fica como trabalho futuro, apenas com `validationAction: warn`.
- **Migrar os documentos antigos para a forma nova** (preencher `temperature: null`). Não traz informação e custa uma escrita por documento num log que é append-only (ADR-0007).
- **Usar `System.Random` com semente no simulador.** A documentação do .NET não garante a mesma sequência entre versões do runtime. Foi escolhido o xoshiro256**, com uma implementação de cerca de 60 linhas, para que um cenário e a sua semente definam a sequência sem depender da versão do .NET.

## Consequências

- **Positivas:**
  - nenhuma alteração de código, migração ou paragem para receber a geração 2. O teste `SchemaEvolutionTests` prova-o de ponta a ponta com o próprio simulador: a API aceita tudo e os três campos chegam ao MongoDB com valor e tipo;
  - o read model aplica os eventos da geração 2 como os outros;
  - a comparação com a variante monolítica (Fase 8) fica bem definida: a diferença é a migração que a variante monolítica precisa e a híbrida não.
- **Negativas:**
  - erros nos campos novos (unidades, nomes trocados) só aparecem quando alguém os lê, porque não há validação na entrada;
  - o log fica com documentos de formas diferentes, e cada leitor tem de lidar com isso;
  - um campo novo colocado fora do `payload` perde-se em silêncio.
  - Mitigação possível, ainda não implementada: um contador de propriedades de topo desconhecidas na API. `TODO(Diogo)`: decidir se vale a pena para a tese.
- **A medir na Fase 9** (cenário `schema-evolution`, 5 execuções): indisponibilidade (pedidos sem resposta ou recusados durante a transição) e latência D7 antes, durante e depois da rampa, comparadas com a migração da variante monolítica. O ensaio piloto da Fase 7 está no diário e em `results/schema-evolution-pilot/`. Indisponibilidade em segundos ainda não foi medida: o piloto só mostra 0 erros e 0 pedidos sem resposta numa execução.
