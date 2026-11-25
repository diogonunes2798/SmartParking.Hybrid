# Spec 003: Ingestão de eventos de ocupação

**Status:** Approved (2026-11-25)
**Date:** 2026-11-25
**Fase:** F4 · **Secção da tese:** 5.3, 5.5, 5.9.4 · **Requisitos:** UC-01; RF-01 (e preparação do UC-05) · **Depende de:** F2 (spec 001, registo dos sensores)

## Problem

A F2 registou os sensores e os lugares no PostgreSQL, mas o protótipo ainda não recebe nenhum evento de ocupação. A subcamada `Documents/` tem só o cliente MongoDB e o *health check*. Sem ingestão, não há registo de eventos (D3) que alimente a disponibilidade (F5), a sincronização (F6), a analítica (F7) nem as medições (F12). Também não há forma de gerar a carga do cenário de referência (Tabela 9, D8).

**Utilizador:** o *gateway* de um parque, que junta os eventos dos sensores de ocupação e os envia em lote. No protótipo, este papel é feito pelo `tools/SensorSimulator`. Na F4 não há autenticação (F11).

**Critério de sucesso:**
- um evento válido e consistente com o registo fica gravado uma única vez no registo de eventos do MongoDB, mesmo que o *gateway* o envie várias vezes;
- o sensor só recebe a confirmação depois de o evento estar gravado de forma durável;
- o simulador gera, de forma reprodutível, a carga do cenário de referência contra a Api.

## Scope

### In

- *Endpoint* de ingestão de eventos de ocupação (`Occupied`/`Freed`), validados (RF-01) e consistentes com o registo dos sensores.
- A Api grava os eventos aceites diretamente no MongoDB, antes de responder, num registo só por acréscimo (ronda 1). O sensor só recebe a confirmação depois de o evento estar gravado no registo (D3, RNF-03).
- Idempotência por `eventId`.
- `payload` extensível: os campos desconhecidos dentro do `payload` são aceites e guardados (preparação do UC-05).
- Métricas próprias da ingestão (ronda 7), exportadas por OTLP como na F1.
- `tools/SensorSimulator` mínimo (ronda 1), na F4b:
  - eventos `Occupied`/`Freed` com os parâmetros da Tabela 9 (`--spots`, `--mean-interval`, `--peak-factor`, `--seed`), por `IHttpClientFactory`;
  - cada lugar começa num estado sorteado com `--seed` e alterna entre `Occupied` e `Freed` a cada evento, como um sensor real (ronda 7);
  - pico (ronda 6): cada lugar emite com intervalos exponenciais de média `--mean-interval`. Em janelas `--peak-at`/`--peak-duration`, a taxa multiplica por `--peak-factor`. As janelas usadas em cada experiência são escolhidas pelos cenários da F12;
  - envio (ronda 6): um *gateway* virtual por parque junta os eventos e envia quando chega a 500 eventos ou passa 1 s (os dois valores são configuráveis). Repete os 503 depois do `Retry-After`, com os mesmos `eventId`. Não escreve em `results/`: as medições são da F12;
  - modo `--provision` (ronda 5): cria o parque, as zonas, os lugares e os sensores pelos *endpoints* da F2 (`spots/batch`, até 1 000 por pedido) e guarda o mapeamento num ficheiro; depois emite sobre esse mapeamento. É reprodutível com `--seed` e não usa SQL à parte.
- Ficheiro `tests/http/ingest.http` com exemplos executáveis, como nas F2 e F3.

### Out

- Estado do lugar e contadores de lugares livres no Redis: F5 (RF-03) (ronda 1).
- Propagação do registo de eventos para o Redis, projetores e janela de consistência: F6. O p95 < 1 s ponta a ponta (UC-01, RNF-01, D7) mede-se a partir da F5/F6.
- Os «consumidores» do RNF-02 (1, 2 e 4) são os projetores da F5/F6, não a escrita no MongoDB (ronda 1).
- Leituras ambientais e sensores `Environmental`: F8 (UC-05, RNF-04). Na F4, um evento de um sensor `Environmental` é rejeitado (ronda 1).
- Simulador: *payload* ambiental e gerações de sensores ficam para a F8.
- Implementação monolítica do registo de eventos (PostgreSQL): F9 (ronda 5).
- Índices além do `_id`, TTL, compressão e arquivo: F5 a F7 e F10 (ronda 6).
- Exportação das métricas para CSV em `results/`: F12.
- Autenticação do *gateway* e RBAC: F11 (ronda 5).
- Abrir ou fechar sessões a partir dos eventos (spec 002, «Out»).

## Domain Model

### Evento de ocupação

- `OccupancyEvent` (Domain): facto imutável com `eventId`, sensor (`hardwareId` e id), `spotId`, `facilityId`, `zoneId`, tipo (`Occupied`/`Freed`), `timestamp` (relógio do dispositivo), `receivedAt` (relógio do servidor, `TimeProvider`) e `payload`.
- Não é uma raiz de agregado nem tem repositório. Na escrita, usa uma porta própria do registo, só por acréscimo, em `Application/Abstractions`, com uma implementação por subcamada e por `Persistence:Mode` (D10, como os repositórios). Na F4 só existe a implementação MongoDB.

### Identificadores (ronda 2)

- `sensorId` no pedido é o `hardwareId` do sensor (único global desde a F2). O evento gravado guarda também o id (Guid) do sensor resolvido no registo.
- `spotId` no pedido é o id (Guid) do lugar.

### Validação contra o registo (ronda 2)

- Uma consulta compilada por lote, `AsNoTracking`, com todos os `hardwareId` do lote. A consulta devolve o sensor, o lugar, a zona e o parque, com os respetivos estados, por uma porta de leitura própria. Sem *cache*: uma alteração da configuração vale de imediato.
- Uma *cache* só se considera se o RNF-01 falhar na F12, e fica registada no diário.

Regras de aceitação de cada evento (rondas 1, 2 e 3), avaliadas depois do esquema e por esta ordem; vale o primeiro código que falhar:

| # | Regra | Código |
|---|---|---|
| 1 | o sensor existe | `sensor_unknown` |
| 2 | o sensor está `Active` | `sensor_inactive` |
| 3 | o sensor é de ocupação | `sensor_not_occupancy` |
| 4 | o sensor tem lugar | `sensor_unassigned` |
| 5 | o `spotId` do evento é o lugar atual do sensor | `spot_mismatch` |
| 6 | o lugar está `Active` | `spot_inactive` |
| 7 | o parque está `Active` | `facility_inactive` |
| 8 | o `timestamp` não passa de agora + tolerância | `timestamp_in_future` |

A tolerância da regra 8 é configurável (`Ingestion:FutureTolerance`, por omissão 5 min) e lida do `TimeProvider` (ronda 3). Eventos antigos são aceites sem limite, porque os *gateways* podem reenviar depois de estarem sem rede.

### Registo de eventos no MongoDB (rondas 3, 4 e 6)

- Coleção normal `occupancy_events`, com `_id = eventId`. A unicidade vem do índice obrigatório do `_id`, sem índice extra. Não é uma coleção *time-series*, porque essas coleções não permitem índices únicos.
- `eventId` tem de ser um UUID; caso contrário, 400.
- Contexto do lugar (ronda 4): o documento guarda também o `facilityId` e o `zoneId` do lugar no momento da ingestão, já lidos na consulta de validação. A F5 e a F7 agregam por parque e por zona sem voltar ao PostgreSQL, e o histórico não muda se o lugar mudar de zona depois.
- `payload` (ronda 4): objeto JSON opcional, guardado como subdocumento BSON tal como chega, até 4 KB por evento (medidos no JSON do `payload` em UTF-8; acima disso, 400). Os campos desconhecidos no topo do evento são ignorados e não se gravam: o lugar da extensão é o `payload` (UC-05).
- Durabilidade (ronda 4): *write concern* `majority` com *journal*. Só se responde ao sensor depois de o evento estar no *journal* da maioria do *replica set* (RNF-03). Localmente o *replica set* tem um nó; o custo mede-se na F12.
- Índices (ronda 6): só o `_id` na F4. Cada fase cria o índice da consulta que introduz (F5/F6 por lugar e tempo, F7 por zona e período, F10 o TTL) e regista-o na sua especificação, para que o custo de escrita de cada índice fique atribuído à fase que o justifica.
- Sequência (ronda 6): a F4 não verifica a sequência dos eventos de um lugar (`Occupied` seguido de `Occupied`, ou um evento mais antigo do que o último). O registo guarda os factos como chegam (D3); a F5 decide o estado atual pelo `timestamp` mais recente.
- Idempotência (ronda 3): o `eventId` decide sozinho. O primeiro a ser gravado ganha e os seguintes são `duplicate`, sem comparar o conteúdo. Dentro do mesmo lote, ganha o primeiro pela ordem do pedido: os repetidos são retirados antes da gravação, porque um `insertMany` sem ordem não garante a ordem de inserção.
- Só por acréscimo: a ingestão nunca altera nem apaga documentos.

## API Contract

| Verbo | Rota | Pedido | Resposta | Erros |
|---|---|---|---|---|
| POST | `/ingest/occupancy-events` | `events[]` (1 a 500): `eventId` (UUID), `sensorId` (`hardwareId`), `spotId` (Guid), `type` (`Occupied`/`Freed`), `timestamp` (ISO 8601 com fuso), `payload?` (objeto, ≤ 4 KB) | 200 `{ accepted, duplicates, rejected, issues[] }` | 400 esquema; 501 modo monolítico; 503 MongoDB indisponível |

- Os eventos chegam sempre em lote. Um evento isolado é um lote de 1 (ronda 2).
- `issues[]` (ronda 7) lista só os eventos não aceites, pela ordem do pedido: `{ index, eventId, status, code? }`, com `status` igual a `duplicate` ou `rejected` e `code` da tabela de regras quando o evento é rejeitado. Um lote todo rejeitado continua a ser 200.
- Lote misto (ronda 2):
  - uma falta de esquema dá 400 com `ValidationProblemDetails` para o lote todo, e nada fica gravado. As chaves de `errors` indicam a posição (`events[3].eventId`). Contam como falta de esquema: campo obrigatório ausente (RF-01), lote vazio ou acima de 500, `eventId` que não é UUID, `type` fora de `Occupied`/`Freed`, `timestamp` sem fuso, `payload` que não é objeto ou acima de 4 KB;
  - uma inconsistência com o registo dá 200 com o evento em `issues[]`. Um sensor mal registado não bloqueia os eventos dos outros sensores do mesmo *gateway*.
- 503 (ronda 5): ProblemDetails com o cabeçalho `Retry-After`.
- 501 (ronda 5): ProblemDetails a indicar que a ingestão no modo monolítico chega na F9.

## Authorization

- O *endpoint* de ingestão tem `AllowAnonymous` explícito, como nas F2 e F3 (ronda 5). Fica registado como fora do âmbito, não como esquecimento: a identidade do *gateway* e o RBAC por ator pertencem ao RNF-06 (F11).

## Edge Cases & Failure Modes

- **Repetição entre pedidos:** o lote volta com os eventos já gravados como `duplicate`; os documentos gravados não mudam.
- **Repetição no mesmo lote:** ganha o primeiro pela ordem do pedido; os seguintes ficam `duplicate`, mesmo com conteúdo diferente.
- **Vários eventos do mesmo sensor no lote:** todos são avaliados e gravados como factos independentes.
- **Sensor reatribuído entre o envio e a receção:** vale a configuração lida pela consulta do lote; um evento com o lugar antigo fica `spot_mismatch`.
- **Relógio do dispositivo adiantado:** acima da tolerância, `timestamp_in_future`; dentro dela, aceite com o `timestamp` do dispositivo.
- **Modo monolítico (ronda 5):** com `Persistence:Mode=Monolithic`, o *endpoint* devolve 501 com ProblemDetails até à F9 e não lê nem grava nada.
- **Falha do MongoDB a meio do lote (ronda 5):** `insertMany` com `ordered: false`.
  - Um erro de chave duplicada (E11000) conta como `duplicate`.
  - Qualquer outro erro, ou um *timeout*, dá 503 com ProblemDetails e `Retry-After` ao lote todo.
  - Alguns eventos podem ter ficado gravados, mas a repetição é segura: voltam como `duplicate` (idempotência).
- **Falha do PostgreSQL na consulta de validação:** 503 com ProblemDetails e `Retry-After`; nada é gravado no MongoDB.
- **Logs:** uma linha `Information` por lote, só com as contagens; os eventos rejeitados ficam em `Debug`, com `hardwareId` e código. Não há dados pessoais nos eventos de ocupação.

## Non-Functional Requirements

- Sem alvos de desempenho verificados na F4: o p95 < 1 s do UC-01 (D7) é ponta a ponta e mede-se a partir da F5/F6, com as experiências da F12. Nenhum valor de desempenho é escrito à mão.
- Cenário de referência (D8, Tabela 9): o simulador tem de o conseguir gerar (5 000 lugares, 1 evento por lugar a cada 5 min, pico de 10×).
- Idas às bases por lote: uma consulta ao PostgreSQL e um `insertMany` ao MongoDB, independentemente do número de eventos (ronda 2).
- Métricas (ronda 7), `Meter` `SmartParking.Ingestion`:
  - contador de eventos por resultado (`accepted`, `duplicate`, `rejected`, com o código);
  - histogramas do tamanho do lote, da duração da consulta de validação e da duração do `insertMany`.
  Exportação por OTLP (F1); o CSV é da F12.

## Integrations

- A ingestão só grava no MongoDB (ronda 4). Não usa *outbox* (não há escrita no PostgreSQL), não usa o `IEventBus` e não grava auditoria: o próprio registo é só por acréscimo.
- Como a F5/F6 lê os eventos novos (*change streams* ou outro mecanismo) decide-se na F6.
- Leitura do PostgreSQL: só a consulta de validação por lote (sensor, lugar, zona e parque).
- O simulador (F4b) usa os *endpoints* da F2 para o `--provision` e o *endpoint* de ingestão para emitir.

## Acceptance Criteria

Cada AC tem um teste com o seu número no nome. Os testes do registo usam o MongoDB dos Testcontainers.

### F4a: ingestão

- **AC-1 (lote aceite).** Dado um parque, uma zona, três lugares ativos e três sensores de ocupação ativos atribuídos, quando se envia um lote com um evento de cada sensor, então a resposta é 200 com `accepted = 3` e `issues` vazio, e ficam três documentos em `occupancy_events` com `_id = eventId`, o `hardwareId` e o id do sensor, `spotId`, `facilityId`, `zoneId`, `type`, `timestamp` e `receivedAt` igual ao relógio do teste.
- **AC-2 (esquema mínimo, RF-01).** Para cada um dos campos `eventId`, `sensorId`, `spotId`, `type` e `timestamp`, dado um lote de dois eventos em que o segundo não tem esse campo, quando se envia, então a resposta é 400 com `ValidationProblemDetails` e a chave `events[1].<campo>`, e nada fica gravado. Teste em tabela.
- **AC-3 (formato e limites).** Dado um lote vazio, um lote de 501 eventos, um `eventId` que não é UUID, um `type` fora de `Occupied`/`Freed`, um `timestamp` sem fuso, um `payload` que não é objeto e um `payload` acima de 4 KB, quando se envia cada um, então a resposta é 400 com `ValidationProblemDetails` e nada fica gravado. Um lote de 500 eventos válidos é aceite. Teste em tabela.
- **AC-4 (idempotência entre pedidos, RF-01).** Dado um lote já aceite, quando se envia de novo o mesmo lote, então a resposta é 200 com `accepted = 0`, `duplicates` igual ao número de eventos e cada evento em `issues` como `duplicate`; o número de documentos e o seu conteúdo não mudam.
- **AC-5 (repetição no mesmo lote).** Dado um lote com dois eventos com o mesmo `eventId` e lugares ou tipos diferentes, quando se envia, então a resposta tem `accepted = 1` e o segundo (`index = 1`) como `duplicate`, e o documento gravado é o do primeiro.
- **AC-6 (consistência com o registo).** Para cada código da tabela de regras de 1 a 7, dado um lote com um evento que falha só essa regra e um evento válido, quando se envia, então a resposta é 200 com `accepted = 1`, `rejected = 1` e o evento inválido em `issues` com esse `code`, e só o evento válido fica gravado. Inclui o sensor `Environmental` (`sensor_not_occupancy`). Teste em tabela.
- **AC-7 (tolerância do relógio).** Dado o relógio do teste em T e a tolerância de 5 min, quando se enviam eventos com `timestamp` T + 5 min, T + 5 min + 1 s e T − 30 dias, então o primeiro e o terceiro são aceites e o segundo é rejeitado com `timestamp_in_future`.
- **AC-8 (precedência dos códigos).** Dado um evento de um sensor `Inactive` com `timestamp` no futuro, quando se envia, então o código é `sensor_inactive` (o primeiro da tabela que falha).
- **AC-9 (`payload` extensível, preparação do UC-05).** Dado um evento com `payload` `{ "battery": 87, "firmware": { "v": "2.1" } }` e um campo desconhecido no topo, quando se envia, então o evento é aceite, o documento tem o `payload` igual ao enviado (incluindo o objeto aninhado) e não tem o campo desconhecido do topo. Um evento sem `payload` também é aceite.
- **AC-10 (contexto no momento da ingestão).** Dado um evento aceite com o lugar na zona Z1, quando o lugar passa para a zona Z2 e chega um novo evento do mesmo sensor, então o primeiro documento continua com `zoneId = Z1` e o segundo tem `zoneId = Z2`.
- **AC-11 (uma consulta por lote).** Dado um lote de 50 eventos de 50 sensores diferentes, quando se envia, então a Api executa exatamente um comando SQL de validação (contado por um intercetor do EF Core no teste).
- **AC-12 (MongoDB indisponível).** Dado o contentor do MongoDB parado, quando se envia um lote válido, então a resposta é 503 com ProblemDetails e `Retry-After`. Depois de o MongoDB voltar, o mesmo lote é aceite.
- **AC-13 (PostgreSQL indisponível).** Dada uma falha da consulta de validação, quando se envia um lote válido, então a resposta é 503 com ProblemDetails e `Retry-After`, e nada fica gravado no MongoDB.
- **AC-14 (durabilidade).** Dada a Api em modo `Hybrid`, quando se resolve a coleção `occupancy_events` usada pela ingestão, então o *write concern* é `majority` com *journal*.
- **AC-15 (só por acréscimo).** Dado o *assembly* de Infrastructure, quando os testes de arquitetura correm, então nenhum tipo de `Infrastructure.Documents` chama métodos de atualização, substituição ou remoção do `IMongoCollection` (`Update*`, `Replace*`, `Delete*`, `FindOneAnd*`, `BulkWrite`), e a porta do registo só expõe a operação de acréscimo.
- **AC-16 (modo monolítico).** Dada a Api em `Persistence:Mode=Monolithic`, quando se envia um lote válido, então a resposta é 501 com ProblemDetails.
- **AC-17 (métricas).** Dado um lote com um evento aceite, um duplicado e um rejeitado com `sensor_unknown`, quando se envia, então o `Meter` `SmartParking.Ingestion` regista uma unidade em cada resultado, com o código no rejeitado, e um valor no histograma do tamanho do lote.
- **AC-18 (ficheiro `.http`).** Dado `tests/http/ingest.http`, quando o teste de ficheiros `.http` o executa contra a Api de teste, então cada pedido devolve o estado indicado no ficheiro.

### F4b: simulador

- **AC-19 (`--provision`).** Dada a Api de teste, quando o simulador corre `--provision --spots 20 --seed 42`, então ficam registados um parque, as zonas, 20 lugares e 20 sensores de ocupação atribuídos, todos pelos *endpoints* da F2, e o ficheiro de mapeamento lista os 20 pares `hardwareId`–`spotId`.
- **AC-20 (reprodutibilidade).** Dados o mesmo mapeamento, a mesma `--seed` e o mesmo relógio falso, quando o simulador gera os eventos de uma janela de tempo duas vezes, então as duas sequências são iguais (incluindo os `eventId`, o tipo e o `timestamp`).
- **AC-21 (alternância).** Dada uma corrida do simulador, quando se agrupam os eventos por lugar e se ordenam pelo `timestamp`, então os tipos alternam entre `Occupied` e `Freed`.
- **AC-22 (taxa e pico).** Dados `--mean-interval`, `--peak-factor` e uma janela de pico, quando o gerador corre sobre um tempo simulado longo, então a razão entre a taxa de eventos dentro e fora da janela fica dentro de um intervalo derivado da distribuição de Poisson para esses parâmetros, calculado no teste e não escrito à mão.
- **AC-23 (envio).** Dado o relógio falso, quando o *gateway* virtual de um parque acumula eventos, então envia um lote ao chegar a 500 eventos ou ao passar 1 s, o que acontecer primeiro, e nenhum pedido leva mais de 500 eventos.
- **AC-24 (repetição após 503).** Dada uma Api de teste que responde 503 com `Retry-After: 1` ao primeiro pedido, quando o simulador envia um lote, então espera o `Retry-After` e reenvia o mesmo lote com os mesmos `eventId`.

## Deferred Decisions

- D1–D10: ver a spec 000. A F4 usa o valor por omissão de D3 (registo imutável de eventos em MongoDB) e de D8 (cenário de referência no simulador). Não toca em D2: a ingestão não publica no `IEventBus` (ronda 4); o mecanismo de propagação para o Redis decide-se na F6.
- D7 (p95 < 1 s do UC-01): fica por verificar até à F5/F6 e à F12, porque é ponta a ponta.
- *Cache* da validação contra o registo: não se faz (ronda 2). Revê-se só se o RNF-01 falhar na F12.

## Implementação em duas partes

Ronda 7, critério do índice das especificações (plano acima de cerca de 15 passos):
- **F4a — ingestão:** porta do registo e porta de leitura da validação, implementação MongoDB, *endpoint*, validação, métricas, `ingest.http` e testes (AC-1 a AC-18).
- **F4b — simulador:** `tools/SensorSimulator` (`--provision`, gerador, pico, envio e repetição) contra a F4a já integrada (AC-19 a AC-24).

Cada parte tem o seu plano e a sua entrada no diário, como as F2 e F3.

## Open Questions

Nenhuma.
