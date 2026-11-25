# Spec 002: Sessões, reservas e pagamentos

**Status:** Approved (2026-11-25)
**Date:** 2026-11-25
**Fase:** F3 · **Secção da tese:** 5.4.1, 5.4.2 · **Requisitos:** UC-02; RF-02 · **Depende de:** F2 (spec 001)

## Problem

A F2 deixou o agregado de sessão (`ParkingSession`, `Reservation`, `Payment`) só com estrutura e tabelas. O condutor ainda não consegue abrir, fechar nem pagar uma sessão, nem reservar um lugar. Os utilizadores, os veículos e os meios de pagamento existem no esquema, mas não têm *endpoints*.

A tese (Tabela 13, RF-02) exige três garantias:
- cada mudança de estado é atómica;
- uma falha a meio do pagamento não deixa a sessão num estado intermédio;
- as atualizações concorrentes da mesma sessão são detetadas.

A F3 é a fase que demonstra e testa estas garantias no lado relacional (5.4.1, 5.4.2).

**Utilizador:** o condutor (`User`, com `Vehicle` e `PaymentMethod`), por pedidos à API. Na F3 não há autenticação (F11).

**Critério de sucesso:**
- o ciclo `Open → Closed → Paid` faz-se todo pela API, e uma sessão paga não volta atrás;
- cada transição grava a alteração, a auditoria e exatamente uma `OutboxMessage` na mesma transação;
- uma falha simulada a meio do pagamento não deixa estado intermédio;
- de duas atualizações concorrentes da mesma sessão ou reserva, uma falha com 409.

## Scope

### In

- *Endpoints* mínimos de `User`, `Vehicle` e `PaymentMethod`, com auditoria e *outbox* (ronda 1, ronda 7).
- Abrir, fechar e pagar sessões, só por pedidos à API (ronda 1). O fecho calcula o valor com a versão de tarifário guardada na sessão (ADR 0005) e o `TimeProvider`.
- Pagamento através da porta `IPaymentGateway`, com uma implementação falsa e determinística.
- Reservas: criar; cancelar; cumprir ao abrir uma sessão com `reservationId`; expirar automaticamente por um *worker* (ronda 1).
- Consultas `GET /api/sessions/{id}` e `GET /api/reservations/{id}`, com portas de leitura próprias (D10).
- Consultas quentes compiladas (`EF.CompileAsyncQuery`): a sessão aberta de um veículo e o tarifário em vigor de um parque.
- Uma migração aditiva: o índice único parcial das sessões abertas por veículo.
- `tests/http/sessions.http`, com um pedido por *endpoint*.
- Extensão da fundação: `Error.PaymentDeclined` (402) (ronda 6).
- Exceções às pastas do pedido, autorizadas pelo Diogo:
  - `src/SmartParking.Workers`: *worker* de expiração e ligação como papel da aplicação (rondas 1 e 8);
  - `src/SmartParking.Application/Abstractions`: `Error.PaymentDeclined`, `IPaymentGateway` e as portas novas;
  - os *endpoints* ficam em `src/SmartParking.Api/Features/<Recurso>`, que é a convenção da F2 (o pedido dizia `Api/Endpoints`).

### Out

- Abrir ou fechar sessões a partir dos eventos de ocupação dos sensores (`Occupied`/`Freed`): depende da F4.
- Propagação para o Redis e publicação da *outbox* (F6).
- Pagamentos reais e reembolsos.
- Listas de sessões e de reservas (por veículo, por lugar, por utilizador).
- Valor estimado de uma sessão aberta (ronda 9).
- Autenticação, autorização real e cifra em repouso do `providerToken` (F11).
- Testes funcionais no modo `Monolithic` (F9).
- Medições de desempenho (F12).
- Dependências NuGet novas; transações distribuídas; alterações não aditivas ao esquema da F2.

## Domain Model

As entidades e as tabelas são as da spec 001 («Agregado de utilizador» e «Agregado de sessão»). Instantes do `TimeProvider`, normalizados ao microssegundo (`Timestamps.Normalize`).

### Alteração aditiva ao esquema

- Índice único parcial `parking_sessions (vehicle_id) WHERE status = 'Open'`: no máximo uma sessão aberta por veículo (ronda 3). Não há unicidade por lugar.
- Mantêm-se da F2: o índice único parcial de `payments (session_id) WHERE status = 'Succeeded'` e o `EXCLUDE` das reservas `Active` sobrepostas no mesmo lugar.

### Ciclo da sessão

`Open → Closed → Paid`. Não há outras transições: uma sessão `Paid` nunca volta a `Closed` nem a `Open`; uma sessão `Closed` nunca volta a `Open`.

- **Abrir:** `startedAt` = agora. A sessão guarda a versão de tarifário em vigor nesse instante (`tariffId`) e usa-a até ao fim, mesmo que entre uma versão nova antes do fecho (ronda 3; ADR 0005). Pré-condições, todas com 409 e sem gravar nada:
  - o parque do lugar tem tarifário em vigor;
  - o lugar e o parque estão `Active`;
  - agora está dentro do horário de funcionamento do parque, no fuso do parque; um dia sem linha é fechado (ronda 9);
  - o veículo não tem outra sessão `Open`;
  - as regras de reserva de «Ciclo da reserva».
- **Fechar:** só uma sessão `Open`; `endedAt` = agora; `amount` = valor calculado. Pode fechar-se fora do horário.
- **Pagar:** ver «Pagamento».

### Cálculo do valor (ronda 2)

Função pura no Domain. Entradas: a versão de tarifário da sessão e a duração `endedAt − startedAt`.

1. **Minutos grátis como limiar:** se a duração ≤ `freeMinutes`, o valor é 0. Acima disso, paga-se a duração toda.
2. **Incremento sempre para cima:** qualquer fração de bloco conta como um bloco inteiro de `billingIncrementMinutes`. O valor de um bloco é `billingIncrementMinutes / 60 × pricePerHour`.
3. **Máximo diário por períodos de 24 h** a contar de `startedAt`, independentes do fuso e das mudanças de hora. Cada período paga, no máximo, `dailyMaximum`, quando existe. O último período pode ser parcial e os seus blocos também se arredondam para cima.
4. **Arredondamento:** 2 casas decimais, `MidpointRounding.AwayFromZero`, aplicado uma só vez no fim, depois do máximo diário.

Exemplos (1,20 €/h, incremento de 15 min, 15 minutos grátis, máximo diário de 10,00 €):
- 10 min → 0,00 €;
- 15 min → 0,00 €;
- 40 min → 3 blocos → 0,90 €;
- 31 h → 10,00 € (primeiras 24 h, com o máximo) + 28 blocos × 0,30 € = 8,40 € → 18,40 €.

### Pagamento (ronda 6 e ronda 7)

- **Pré-condições**, verificadas antes do gateway (409, sem chamar o gateway nem gravar nada):
  - a sessão está `Closed` (uma `Open` ou `Paid` dá 409; não há «fechar e pagar» num só pedido);
  - o `PaymentMethod` pertence ao dono do veículo da sessão.

  A validade do meio (`expiresOn`) não se verifica.
- **Ordem:**
  1. chamada à porta `IPaymentGateway`, fora da transação, com a chave de idempotência (`sessionId`, `paymentMethodId`). Repetir com o mesmo meio devolve o resultado anterior; outro meio é uma tentativa nova;
  2. uma só transação grava o `Payment`, a sessão `Paid` (quando aprovado), a auditoria e a `OutboxMessage`.

  Se a gravação falhar depois da aprovação, a sessão fica `Closed` e sem `Payment`. Repetir o pedido não cobra duas vezes.
- **Recusa do gateway:** uma transação grava um `Payment` `Failed`, a auditoria e um `PaymentFailed.v1`. A sessão fica `Closed` e pode pagar-se de novo. A resposta é 402 (`Error.PaymentDeclined`).
- **Valor 0,00 €:** a sessão fica `Closed` no fecho. O pagamento grava um `Payment` `Succeeded` de 0,00 € sem chamar o gateway e passa a sessão a `Paid`.
- **Gateway falso e determinístico:** recusa quando o `providerToken` do meio começa por `decline_` e aprova os outros. Guarda em memória o resultado por chave de idempotência e conta as cobranças por chave, para que os testes possam verificar que não houve cobrança dupla. O estado perde-se ao reiniciar a Api: é aceitável num gateway simulado.

### Ciclo da reserva

`Active → Cancelled | Fulfilled | Expired`. As três são finais.

- **Criar** (ronda 4, ronda 5, ronda 10):
  - `startsAt` ≥ agora → senão 400;
  - `endsAt − startsAt` ≤ `Reservations:MaxDurationHours` (por omissão 24) → senão 400;
  - `startsAt` ≤ agora + `Reservations:MaxAdvanceDays` (por omissão 7) → senão 400;
  - [`startsAt`, `endsAt`) cabe num só intervalo de funcionamento de um dia, no fuso do parque → senão 400;
  - o lugar e o parque estão `Active` → senão 409;
  - o veículo pertence ao utilizador da reserva → senão 409;
  - não se sobrepõe a outra reserva `Active` do mesmo lugar (`EXCLUDE` da F2) → senão 409.
- **Cancelar** (ronda 5): qualquer reserva `Active`, antes ou depois de `startsAt`. Uma reserva `Cancelled`, `Fulfilled` ou `Expired` dá 409; cancelar de novo não é idempotente.
- **Cumprir** (ronda 4): abrir uma sessão com `reservationId` exige três condições, senão 409:
  - o mesmo lugar e o mesmo veículo da reserva;
  - a reserva `Active`;
  - agora dentro da **janela de cumprimento** [`startsAt` − `Reservations:EarlyArrivalMinutes` (por omissão 15), `startsAt` + `Reservations:ExpiryGraceMinutes` (por omissão 15)].

  A reserva passa a `Fulfilled` na mesma transação da abertura.
- **Proteção do lugar** (ronda 4, ronda 9, ronda 10): abrir uma sessão sem `reservationId` num lugar que tem uma reserva `Active` de outro veículo dá 409 quando agora está dentro da janela de cumprimento dessa reserva. Fora da janela, o lugar está livre, mesmo que o *worker* ainda não tenha expirado a reserva.
- **Expirar** (ronda 3, ronda 7): uma reserva `Active` passa a `Expired` quando agora > `startsAt` + tolerância sem ter sido cumprida. Ver «Integrations».

## API Contract

Todas as rotas estão sob `/api` e são `AllowAnonymous`. Erros em ProblemDetails (contrato da spec 000).

**Erros comuns:**
- 400: validação;
- 402: pagamento recusado pelo gateway;
- 404: o recurso não existe;
- 409: regra de negócio, unicidade, integridade referencial ou conflito de concorrência (`xmin`).

**Concorrência sem `If-Match`** (ronda 7): os comandos de transição (`/close`, `/pay`, `/cancel`) leem a entidade, verificam o estado e gravam com o `xmin` lido. Um `xmin` desatualizado na gravação dá 409, como nas versões de tarifário (ADR 0005). Os pedidos não levam `If-Match` e as respostas não levam `ETag`.

| Verbo | Rota | Pedido | Resposta | Erros específicos |
|---|---|---|---|---|
| POST | `/users` | name, email | 201 + Location | 400; 409 email repetido |
| GET | `/users/{id}` | — | 200 (id, name, email) | 404 |
| POST | `/users/{id}/vehicles` | licensePlate | 201 + Location | 400; 404 utilizador; 409 matrícula repetida |
| GET | `/users/{id}/vehicles` | page, pageSize | 200 lista | 404 utilizador |
| DELETE | `/users/{id}/vehicles/{vehicleId}` | — | 204 | 404; 409 se referido por sessões ou reservas |
| POST | `/users/{id}/payment-methods` | type, providerToken, maskedNumber, expiresOn? | 201 + Location | 400; 404 utilizador |
| GET | `/users/{id}/payment-methods` | page, pageSize | 200 lista, sem `providerToken` | 404 utilizador |
| DELETE | `/users/{id}/payment-methods/{paymentMethodId}` | — | 204 | 404; 409 se referido por pagamentos |
| POST | `/sessions` | spotId, vehicleId, reservationId? | 201 + Location (sessão) | 400; 404 lugar, veículo ou reserva; 409 pré-condições |
| GET | `/sessions/{id}` | — | 200 (id, spotId, vehicleId, tariffId, reservationId, startedAt, endedAt, status, amount, currency) | 404 |
| POST | `/sessions/{id}/close` | — | 200 (sessão com endedAt e amount) | 404; 409 não `Open` ou concorrência |
| POST | `/sessions/{id}/pay` | paymentMethodId | 200 (sessão `Paid` e paymentId) | 404; 402 recusado (ProblemDetails com `paymentId`); 409 não `Closed`, meio de outro utilizador ou concorrência |
| POST | `/reservations` | spotId, userId, vehicleId, startsAt, endsAt | 201 + Location | 400 regras de criação; 404 lugar, utilizador ou veículo; 409 regras de criação ou sobreposição |
| GET | `/reservations/{id}` | — | 200 (id, spotId, userId, vehicleId, startsAt, endsAt, status) | 404 |
| POST | `/reservations/{id}/cancel` | — | 200 (reserva `Cancelled`) | 404; 409 não `Active` ou concorrência |

`amount` é nulo enquanto a sessão está `Open`. As listas seguem a paginação da F2 (`page`, `pageSize` 1–100, por omissão 20; `{ items, page, pageSize, totalCount }`).

## Authorization

- `AllowAnonymous` explícito em cada grupo, como na F2 (ronda 8). Fica registado como fora do âmbito (F11), não como esquecimento.
- A auditoria usa `ICurrentActor`: `anonymous` na Api e `system` no *worker*.
- Na F11: o condutor só vê e altera os seus dados, sessões e reservas.
- Na base de dados, a Api e os Workers ligam pelo utilizador `LOGIN` membro de `smartparking_app` (ronda 8).
- Dados pessoais (nome, email, matrícula) nunca em *logs* de nível `Information` ou inferior. O `providerToken` nunca aparece em respostas, auditoria, *outbox* nem *logs*.

## Edge Cases & Failure Modes

- **Atomicidade:** cada transição é uma só chamada a `SaveChanges`, numa só transação, com a alteração de negócio, a auditoria e a *outbox*. Se uma das gravações falhar, nada fica gravado. Nunca 2PC.
- **Falha a meio do pagamento:** o gateway aprova e a gravação falha. A sessão continua `Closed`, sem `Payment`, sem auditoria nem evento novos, e a resposta é 500. Repetir o pedido com o mesmo meio dá 200 e o gateway regista uma só cobrança para essa chave.
- **Fechos concorrentes da mesma sessão:** um dá 200 e o outro dá 409. Fica um só `SessionClosed.v1`.
- **Pagamentos concorrentes da mesma sessão:** um dá 200 e o outro dá 409 (por `xmin` ou pelo índice único dos pagamentos `Succeeded`). Fica um só `Payment` `Succeeded`. Com o mesmo meio, o gateway regista uma só cobrança.
- **Cancelar e cumprir a mesma reserva em simultâneo:** uma operação tem êxito e a outra dá 409.
- **Duas aberturas em simultâneo para o mesmo veículo:** o índice único parcial deixa passar uma só (409 na outra).
- **Worker e cumprimento em simultâneo:** o *worker* ignora o conflito de `xmin` e a reserva fica como o outro pedido a deixou.
- **Violações da base de dados** traduzidas como na F2: `23505` → 409; `23503` → 409; `23P01` → 409; `23514` → 400.
- **Pedidos repetidos:** os `POST` não são idempotentes. Repetir `/close` ou `/cancel` dá 409, porque o estado já mudou. Repetir `/pay` numa sessão `Paid` dá 409.
- **Sessão longa:** a fórmula cobre durações de vários dias; o `numeric(10,2)` limita o valor a 99 999 999,99.

## Non-Functional Requirements

- Sem alvo de desempenho na F3 (ronda 10). O UC-02 < 2 s no p95 (RNF-01) mede-se na F12, com k6. Nenhum valor de desempenho é escrito à mão.
- Consultas quentes compiladas com `EF.CompileAsyncQuery`: a sessão aberta de um veículo e o tarifário em vigor de um parque.
- As consultas de leitura usam `AsNoTracking` e projeções.
- Os dois modos (`Hybrid` e `Monolithic`) registam as mesmas implementações relacionais.

## Integrations

- **Exatamente um evento na *outbox* por transição** (ronda 8). Quando uma transição altera duas entidades, o evento leva as duas: `SessionOpened.v1` leva `reservationId`; `SessionPaid.v1` leva `paymentId` e `amount`.
- **Auditoria: uma linha por entidade alterada** (rondas 8 e 9), todas com o `outboxMessageId` do evento da transição. Abrir com reserva → 2 linhas (sessão e reserva); pagar → 2 linhas (pagamento e sessão); recusa → 1 linha (pagamento).
  - O mecanismo da F2 (uma auditoria por evento) estende-se às entidades alteradas sem evento próprio.
  - A versão de tarifário fechada no ADR 0005 continua sem auditoria própria. É uma exceção da F2 que fica registada e não se altera na F3.
- **Eventos** (*records* `sealed` no Domain; `type` com o nome e a versão):
  - `UserRegistered.v1` (sem email nem nome no *payload*: só id);
  - `VehicleRegistered.v1`, `VehicleRemoved.v1`;
  - `PaymentMethodAdded.v1`, `PaymentMethodRemoved.v1` (sem `providerToken`);
  - `SessionOpened.v1`, `SessionClosed.v1` (com `endedAt` e `amount`), `SessionPaid.v1`;
  - `PaymentFailed.v1` (com `paymentId`, `sessionId`, `amount`);
  - `ReservationCreated.v1`, `ReservationCancelled.v1`, `ReservationExpired.v1`.
- **Worker de expiração** (ronda 7): corre a cada `Reservations:ExpiryScanSeconds` (por omissão 60) e expira cada reserva vencida na sua própria transação, com a auditoria (actor `system`) e o evento.
- **Workers:** passam a ligar ao PostgreSQL com o utilizador `LOGIN` membro de `smartparking_app`, como a Api (ronda 8). Isto fecha o ponto em aberto da F2.
- Só PostgreSQL. Não toca no MongoDB nem no Redis. A *outbox* fica com `publishedAt = null` até à F6.

## Acceptance Criteria

Cada AC tem um teste com o seu número no nome (`…_ACn`). Os testes de integração correm no modo `Hybrid`, com o PostgreSQL do Testcontainers, a Api ligada pelo utilizador da aplicação e um `FakeTimeProvider`.

**F3a — utilizadores, sessões e pagamento**

- **AC-1 (migração aditiva).** Dado o esquema da F2, quando se aplica a migração nova, então existe o índice único parcial `parking_sessions (vehicle_id) WHERE status = 'Open'`. Nenhuma tabela, coluna ou restrição da `Initial` é removida ou alterada.
- **AC-2 (utilizador).** Quando se cria um utilizador, então:
  - a resposta é 201 com `Location`;
  - o email fica em minúsculas;
  - ficam uma `AuditEntry` e um `UserRegistered.v1`.

  Um email repetido (com outra capitalização) dá 409.
- **AC-3 (veículo).** Quando se regista um veículo com `aa-12 bb`, então:
  - a matrícula fica `AA12BB`;
  - a resposta é 201;
  - ficam uma auditoria e um `VehicleRegistered.v1`.

  Uma matrícula repetida dá 409. Apagar um veículo referido por uma sessão dá 409 e não grava nada.
- **AC-4 (meio de pagamento sem token exposto).** Quando se adiciona um meio de pagamento, então:
  - o GET da lista não contém o `providerToken`;
  - nem a `AuditEntry` (`before`/`after`) nem a `OutboxMessage` contêm o valor do `providerToken`.
- **AC-5 (abrir).** Dado um lugar `Active` num parque `Active`, dentro do horário e com tarifário em vigor, quando se abre uma sessão, então:
  - a resposta é 201;
  - a sessão está `Open`, com `startedAt` = agora e `tariffId` = a versão em vigor;
  - ficam exatamente uma `AuditEntry` e um `SessionOpened.v1`.
- **AC-6 (pré-condições da abertura).** Abrir dá 409, sem sessão, auditoria nem evento novos, em cada caso:
  - parque sem tarifário em vigor;
  - lugar `Inactive`;
  - parque `Inactive`;
  - fora do horário de funcionamento;
  - num dia sem horário.
- **AC-7 (uma sessão aberta por veículo).** Dado um veículo com uma sessão `Open`, quando se abre outra (no mesmo lugar ou noutro), então a resposta é 409.
- **AC-8 (cálculo do valor).** Para a versão de tarifário de referência (1,20 €/h, 15 min, 15 minutos grátis, máximo de 10,00 €), o cálculo dá os valores dos exemplos de «Cálculo do valor»: 10 min → 0,00; 15 min → 0,00; 40 min → 0,90; 31 h → 18,40. Também cobre:
  - um caso de arredondamento a meio (o valor `x,xx5` sobe);
  - um tarifário sem `dailyMaximum`.

  Teste unitário do Domain, com os casos em tabela.
- **AC-9 (fechar).** Dada uma sessão `Open` com 40 min, quando se fecha, então:
  - a resposta é 200, com `status = Closed`, `endedAt` = agora e `amount = 0.90`;
  - ficam exatamente uma `AuditEntry` e um `SessionClosed.v1`.

  Fechar uma sessão `Closed` dá 409.
- **AC-10 (versão nova a meio da sessão).** Dada uma sessão aberta com a versão V1, quando entra em vigor uma V2 com outro preço e a sessão fecha, então o valor é calculado com V1.
- **AC-11 (fechos concorrentes).** Dada uma sessão `Open`, quando dois pedidos de fecho concorrentes gravam sobre o mesmo `xmin`, então:
  - um dá 200 e o outro dá 409 com ProblemDetails;
  - fica um só `SessionClosed.v1`.
- **AC-12 (pagar).** Dada uma sessão `Closed`, quando se paga com um meio do dono do veículo, então:
  - a resposta é 200, a sessão fica `Paid` e há um `Payment` `Succeeded` com o valor da sessão;
  - ficam exatamente um `SessionPaid.v1` e duas `AuditEntry` (pagamento e sessão) com o mesmo `outboxMessageId`;
  - a sessão, o pagamento e as linhas novas têm o mesmo `xmin`, o que prova que é a mesma transação.
- **AC-13 (recusa).** Dado um meio cujo `providerToken` começa por `decline_`, quando se paga, então:
  - a resposta é 402 com ProblemDetails e `paymentId`;
  - fica um `Payment` `Failed`, uma `AuditEntry` e um `PaymentFailed.v1`;
  - a sessão continua `Closed`.

  Um pagamento seguinte com outro meio dá 200.
- **AC-14 (falha a meio do pagamento).** Dada uma falha forçada na inserção em `outbox_messages` (um *trigger* criado só no teste), quando se paga e o gateway aprova, então:
  - a resposta é 500;
  - a sessão continua `Closed`, sem `Payment`, auditoria nem evento novos.

  Depois de remover o *trigger*, repetir o pagamento com o mesmo meio dá 200 e o gateway regista uma só cobrança para a chave.
- **AC-15 (sessão paga não volta atrás).** Dada uma sessão `Paid`:
  - fechar dá 409;
  - pagar de novo dá 409;
  - a sessão continua `Paid`, sem eventos novos.

  O Domain não tem nenhum método que leve `Paid` a `Closed` ou a `Open`; teste unitário.
- **AC-16 (pagamentos concorrentes).** Dada uma sessão `Closed`, quando dois pagamentos concorrentes com o mesmo meio gravam, então:
  - um dá 200 e o outro dá 409;
  - fica um só `Payment` `Succeeded`;
  - o gateway regista uma só cobrança.
- **AC-17 (pré-condições do pagamento).** Pagar dá 409, sem chamar o gateway nem gravar nada:
  - numa sessão `Open`;
  - com um meio de outro utilizador.
- **AC-18 (valor zero).** Dada uma sessão fechada dentro dos minutos grátis (`amount = 0.00`), quando se paga, então:
  - o gateway não é chamado;
  - fica um `Payment` `Succeeded` de 0,00 €;
  - a sessão fica `Paid`;
  - fica um `SessionPaid.v1`.
- **AC-19 (consulta da sessão).** `GET /api/sessions/{id}` devolve 200 com `amount` nulo enquanto `Open` e com o valor depois do fecho. Um id inexistente dá 404.
- **AC-20 (*sessions.http*).** `tests/http/sessions.http` tem um pedido por *endpoint* da tabela «API Contract», verificado como o AC-23 da F2.
- **AC-21 (um evento por transição).** Para cada transição deste documento (abrir, abrir com reserva, fechar, pagar, recusa, criar reserva, cancelar, expirar), a transação grava exatamente uma `OutboxMessage`, do tipo indicado em «Integrations». Teste em tabela.

**F3b — reservas e *worker***

- **AC-22 (criar reserva).** Dado um lugar `Active` num parque aberto, quando se cria uma reserva válida, então:
  - a resposta é 201 e a reserva está `Active`;
  - ficam uma `AuditEntry` e um `ReservationCreated.v1`.
- **AC-23 (regras de criação).** Cada regra de «Ciclo da reserva → Criar» tem um caso: `startsAt` no passado, duração acima do máximo, antecedência acima do máximo ou fora do horário dão 400; lugar ou parque `Inactive`, veículo de outro utilizador ou sobreposição com outra reserva `Active` dão 409.
- **AC-24 (cancelar).** Dada uma reserva `Active`, quando se cancela, então:
  - a resposta é 200 e a reserva fica `Cancelled`;
  - fica um `ReservationCancelled.v1`.

  Cancelar de novo dá 409.
- **AC-25 (cumprir).** Dada uma reserva `Active` e agora dentro da janela de cumprimento, quando o veículo da reserva abre uma sessão com `reservationId`, então:
  - a resposta é 201 e a reserva fica `Fulfilled`;
  - ficam um `SessionOpened.v1` com `reservationId` e duas `AuditEntry` (sessão e reserva) com o mesmo `outboxMessageId`.

  Antes da margem, depois da tolerância, com outro veículo ou com outro lugar, dá 409 e não grava nada.
- **AC-26 (proteção do lugar).** Dada uma reserva `Active` do veículo A, quando o veículo B abre uma sessão sem `reservationId` no mesmo lugar:
  - dentro da janela de cumprimento, dá 409;
  - depois de `startsAt` + tolerância, dá 201.
- **AC-27 (cancelar e cumprir em simultâneo).** Dada uma reserva `Active` dentro da janela, quando um cancelamento e uma abertura com `reservationId` gravam sobre o mesmo `xmin`, então uma operação tem êxito, a outra dá 409, e há um só evento da reserva.
- **AC-28 (expiração).** Dadas três reservas: uma `Active` vencida, uma `Active` dentro da tolerância e uma `Cancelled`. Quando o *worker* corre uma passagem, então:
  - só a primeira fica `Expired`;
  - ficam uma `AuditEntry` com `actor = system` e um `ReservationExpired.v1`;
  - um conflito de `xmin` numa reserva não impede a expiração das outras.
- **AC-29 (Workers como papel da aplicação).** Ligado com a *connection string* dos Workers, `UPDATE` em `audit_entries` falha com `42501`.
- **AC-30 (consulta da reserva).** `GET /api/reservations/{id}` devolve 200 com o estado atual. Um id inexistente dá 404.

## Deferred Decisions

- D1–D10: ver a spec 000. A F3 usa os valores por omissão de D1 (sessões em PostgreSQL) e de D2 (*outbox* em PostgreSQL; a publicação é da F6).
- Abrir e fechar sessões a partir dos eventos dos sensores: fica fora da F3. Reabre-se depois da F4, se a demonstração (F13) o pedir.

## Implementação em duas partes

A especificação é uma só. Se o plano passar de cerca de 15 passos, a implementação divide-se em duas sessões (ronda 10):

- **F3a — utilizadores, sessões e pagamento:** AC-1 a AC-21 (no AC-21, só as transições de sessão e de pagamento).
- **F3b — reservas e *worker*:** AC-22 a AC-30, e as transições de reserva no AC-21. Sem migrações novas; se a F3a revelar um erro no esquema, a especificação volta a «In Review».

## Open Questions

Nenhuma.
