# ADR 0007 — Reservas: cumprimento na transação da abertura e expiração por *worker*

- **Estado:** Aceite (especificação 002 aprovada a 2026-11-25; plano 002b aprovado a 2026-11-25, pontos 1 a 3 e propostas 8, 10, 11 e 15)
- **Data:** 2026-11-25
- **Requisitos:** RF-02 (cada transição de reserva é atómica; o cumprimento na mesma transação da abertura; concorrência detetada pelo `xmin`); RNF-06 (auditoria só por acréscimo, com o actor `system` no *worker* e os Workers ligados como papel da aplicação).
- **Decisão (D1–D10):** D1 (reservas em PostgreSQL) e D2 (*outbox* em PostgreSQL), nos valores por omissão; D10 (um repositório por raiz de agregado, ADR 0003). Nenhuma decisão D1–D10 é alterada.
- **Secções da tese:** 5.4.1 e 5.4.2.
- **Especificação:** [002-sessoes-pagamentos](../specs/002-sessoes-pagamentos.md), secções «Ciclo da reserva», «Edge Cases», «Integrations» e AC-21 a AC-30; plano [002b](../plans/002b-reservas-worker.md). Estende o [ADR 0006](0006-auditoria-por-entidade-alterada.md).

## Contexto

A especificação 002 pede que a reserva passe a `Fulfilled` «na mesma transação da abertura» da sessão, e que uma reserva `Active` passe a `Expired` quando agora > `startsAt` + tolerância, por um *worker*, «cada reserva vencida na sua própria transação, com a auditoria (actor `system`) e o evento». Pede também que, se o *worker* e um cumprimento correrem em simultâneo, o *worker* ignore o conflito de `xmin`.

A regra geral do projeto (ADR 0003) é uma raiz de agregado por transação. A `ParkingSession` e a `Reservation` são duas raízes distintas do agregado de sessão (Tabela 15 da tese). Até à F3a, os Workers não tinham nenhum *background service* e ligavam ao PostgreSQL como dono das tabelas, um ponto em aberto desde a F2.

## Decisão

- **Duas raízes numa transação no cumprimento.** O `ParkingSession.Open` recebe a reserva, chama `Reservation.Fulfil` (interno ao Domain, sem evento próprio) e levanta um único `SessionOpened.v1` com as entidades alteradas `sessão, reserva` (ADR 0006): uma `OutboxMessage` e duas `AuditEntry` com o mesmo `outboxMessageId`. A reserva é lida com *tracking* e fica `Modified`, por isso a gravação verifica também o `xmin` dela. É uma exceção consciente à regra de uma raiz por transação, limitada a este caso.
- **Concorrência sem `If-Match`.** Cancelar, cumprir e expirar leem a reserva, verificam o estado e gravam com o `xmin` lido. Um `xmin` desatualizado dá 412 na unidade de trabalho, que os *handlers* convertem em 409 (`Reservation.ConcurrentChange` ou `Session.ConcurrentChange`). O AC-27 prova que, de um cancelamento e um cumprimento sobre o mesmo `xmin`, um tem êxito, o outro dá 409 e há um só evento da reserva.
- **Janela de cumprimento fechada nos dois extremos e expiração estritamente depois.** `[startsAt − EarlyArrival, startsAt + ExpiryGrace]` e `IsDue` ⇔ agora > `startsAt + ExpiryGrace`. O cumprimento e a expiração nunca se sobrepõem no tempo; só a concorrência os pode cruzar, e essa é resolvida pelo `xmin`.
- **Expiração por *worker*, uma transação por reserva.** A passagem lista os identificadores vencidos (`IReservationReader.ListDueIdsAsync`, `AsNoTracking`) e corre `ExpireReservation` para cada um no seu próprio *scope* (um `DbContext` e uma transação por reserva). O comando relê a reserva com *tracking* e ignora-a sem gravar se já não estiver `Active` ou ainda não estiver vencida. Um 409 de `xmin` conta como conflito e não para a passagem: a reserva fica como o outro pedido a deixou (AC-28). O `ReservationExpiryWorker` corre uma passagem ao arrancar e depois uma a cada `Reservations:ExpiryScanSeconds`; uma falha da base de dados numa passagem é registada como aviso e a passagem seguinte tenta de novo.
- **Actor `system`.** Os Workers registam `SystemCurrentActor` antes do `AddInfrastructure`, que só acrescenta o `anonymous` quando não há outro actor. A Api não muda.
- **Login próprio dos Workers** (plano 002b, ponto 1). Os Workers ligam como `smartparking_workers`, membro de `smartparking_app`, como a Api com `smartparking_api`. A auditoria fica só por acréscimo também para eles (AC-29), e o `pg_stat_activity` distingue os dois processos. O `scripts/db-app-user.sh` cria os dois logins.
- **Varredura sem índice próprio.** `ListDueIdsAsync` filtra por `status = 'Active' AND starts_at < corte`; o índice do `EXCLUDE` é por lugar e intervalo e não serve esta consulta. Não se acrescenta índice nesta fase (sem migrações na F3b).

| Transição | Raiz que levanta | Evento | Auditorias |
|---|---|---|---|
| Criar reserva | `Reservation` | `ReservationCreated.v1` | reserva |
| Cancelar | `Reservation` | `ReservationCancelled.v1` | reserva |
| Abrir com reserva | `ParkingSession` | `SessionOpened.v1` (com `reservationId`) | sessão e reserva |
| Expirar (*worker*) | `Reservation` | `ReservationExpired.v1` | reserva, actor `system` |

## Alternativas consideradas

- **Cumprimento assíncrono pela *outbox*:** a sessão abre sozinha e um consumidor do `SessionOpened.v1` passa a reserva a `Fulfilled` noutra transação. Respeitaria uma raiz por transação, mas contradiz a especificação («na mesma transação da abertura») e deixaria uma reserva `Active` depois de cumprida: a proteção do lugar e o *worker* teriam de lidar com esse intervalo (o *worker* podia expirar uma reserva já cumprida, e outro veículo seria recusado num lugar já ocupado pelo dono da reserva). A publicação da *outbox* só chega na F6.
- **Um evento próprio do cumprimento (`ReservationFulfilled.v1`).** Daria um evento por raiz, mas contradiz «exatamente um evento na *outbox* por transição» (AC-21) e o `SessionOpened.v1` com `reservationId` da especificação.
- **Uma unidade de trabalho para toda a passagem do *worker*.** Menos transações, mas um conflito de `xmin` numa reserva desfaria a expiração de todas as outras, contra o AC-28; o teste de mutação correspondente confirma-o.
- **Expirar com um `UPDATE … WHERE status = 'Active' AND starts_at < corte` em lote.** Uma só instrução, mas sem auditoria por entidade nem evento por transição, a não ser que fossem gerados por SQL fora da unidade de trabalho do ADR 0004.
- **Reutilizar o login `smartparking_api` nos Workers.** Menos alterações, mas o nome engana e os dois processos ficam indistinguíveis na base, o que prejudica as medições de ligações por processo (F12).

## Consequências

- A abertura com reserva bloqueia a linha da reserva durante a transação da abertura. O AC-27 prova que o perdedor de uma corrida recebe 409 e que não fica nada a meio (o `INSERT` da sessão perdedora é desfeito).
- A janela de cumprimento (Api) e a expiração (Workers) leem a mesma chave de configuração em dois processos. Com valores diferentes de `Reservations:ExpiryGraceMinutes`, uma reserva pode expirar dentro da janela que a Api ainda aceita (o cumprimento dá 409 pelo estado, sem gravar nada errado) ou ficar `Active` depois da janela. Os dois `appsettings.json` levam os mesmos valores; uma configuração partilhada fica para a F13, se for precisa.
- O custo da varredura sem índice não foi medido. Se as medições da F12 o justificarem, um índice parcial é uma alteração ao esquema e a especificação volta a «In Review».
- Uma reserva que começa às `opensAt` tem a margem de chegada antecipada fora do horário: abrir nesse período dá 409 `Session.FacilityClosed`, porque as pré-condições da F3a continuam a valer.
- O *worker* não publica nada: as mensagens ficam com `published_at = null` até à F6.

## Referências

- Especificação 002, secções «Ciclo da reserva», «Edge Cases» e «Integrations» (rondas 3, 4, 5, 7, 8, 9 e 10 do `/spec`).
- ADR 0003 (repositório por raiz de agregado), ADR 0004 (papel da aplicação, auditoria e *outbox* na unidade de trabalho), ADR 0006 (auditoria por entidade alterada).
