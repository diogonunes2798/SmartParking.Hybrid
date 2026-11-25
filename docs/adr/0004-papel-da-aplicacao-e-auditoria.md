# ADR 0004 — Papel da aplicação e auditoria só por acréscimo

- **Estado:** Aceite (especificação 001 aprovada a 2026-11-24)
- **Data:** 2026-11-25
- **Requisitos:** RNF-06 (auditoria só por acréscimo, verificada por testes de integração); RF-06 (cada alteração de configuração é auditada).
- **Decisão (D1–D10):** D2 (*outbox* em PostgreSQL), no valor por omissão. Nenhuma decisão D1–D10 é alterada.
- **Secções da tese:** 5.4.1, 5.6.2 e 5.9.2.
- **Especificação:** [001-configuracao](../specs/001-configuracao.md) (F2a: AC-4, AC-5, AC-6 e AC-10).

## Contexto

A tese defende que cada alteração de negócio deixa, na mesma transação, um registo de auditoria imutável e um evento na *outbox* (5.4.1 e 5.6.2). A F2 é a primeira fase com tabelas de negócio, por isso é a primeira em que isto se pode demonstrar e testar.

Há dois problemas:

- **Quem impõe a imutabilidade.** Se a auditoria só por acréscimo depender da disciplina do código, nada impede que um `UPDATE` ou um `DELETE` a altere. Um teste que mostre que o código não o faz não prova que a base de dados o recusa.
- **Com que utilizador liga a Api.** O utilizador `smartparking` do `compose.yaml` é superutilizador e dono das tabelas. Com ele, qualquer permissão negada é ignorada e o AC-10 não é verificável.

## Decisão

- **Grupo da aplicação.** A migração `Initial` corre o script versionado `Relational/Migrations/Sql/0001_app_role.sql`, embebido no *assembly*. O script cria o grupo `smartparking_app` (`NOLOGIN`) e dá-lhe:
  - `SELECT, INSERT, UPDATE, DELETE` nas tabelas de negócio dos três agregados;
  - só `SELECT, INSERT` em `audit_entries` (sem `UPDATE`, `DELETE` nem `TRUNCATE`);
  - `SELECT, INSERT, UPDATE` em `outbox_messages` (o leitor da F3/F4 marca as linhas como publicadas; sem `DELETE`);
  - `USAGE` no *schema* `public`, sem `CREATE` (sem DDL).
- **Dois utilizadores.** As migrações correm como dono (`smartparking`): no desenvolvimento, por `dotnet ef database update` com a variável `ConnectionStrings__PostgresMigrations`; nos testes, por `RelationalDatabase.MigrateAsync`. A Api liga como `smartparking_api`, um *login* membro do grupo, criado por `scripts/db-app-user.sh` (desenvolvimento) e pelos contentores de teste (`tests/Common/DatabaseContainers.cs`).
- **Auditoria e *outbox* na unidade de trabalho.** O `SmartParkingDbContext` implementa o `IUnitOfWork`. Antes de gravar, transforma cada evento de domínio pendente numa `OutboxMessage` e numa `AuditEntry` que aponta para ela (`outboxMessageId`). Grava tudo numa só chamada a `SaveChanges`, ou seja, numa só transação, sem 2PC. A auditoria guarda o estado anterior e o seguinte (`before`, `after`, em `jsonb`), lido do ChangeTracker, e o ator vem da porta `ICurrentActor` (`anonymous` até à F11).
- **Sem chaves estrangeiras** em `audit_entries` e `outbox_messages`: a linha auditada pode ser apagada depois, e a auditoria tem de ficar.
- **Violações traduzidas.** A Infrastructure converte as violações do PostgreSQL em `Error`: `23505`, `23503` e `23P01` dão 409; `23514` dá 400; um `xmin` desatualizado dá 412. As mensagens nomeiam a restrição e a tabela, mas nunca copiam o DETAIL do servidor, que traz valores das linhas.

## Alternativas consideradas

- **Ligar como dono e confiar no código:** mais simples, mas a imutabilidade não seria uma propriedade da base de dados e o AC-10 não teria como falhar.
- **Gatilhos (*triggers*) que recusam `UPDATE` e `DELETE` em `audit_entries`:** também impõem a regra na base de dados, mas o dono e o superutilizador podem desativá-los. Somam lógica ao esquema que as permissões já resolvem com uma linha por tabela.
- ***Row-level security*:** útil para filtrar linhas por ator (F11), mas não acrescenta nada à proibição de alterar a tabela inteira.
- **Auditoria fora da transação (MongoDB, ou um leitor de alterações como a replicação lógica):** separa a auditoria do negócio, mas uma falha entre a gravação e a auditoria deixaria alterações por auditar, contra a tese (mesma transação, sem 2PC).
- **Um *interceptor* do EF Core em vez do `SaveChanges` da unidade de trabalho:** funciona da mesma forma, mas esconde num ponto de extensão do EF Core o que a tese apresenta como a regra central da sincronização. No `SaveChanges` do `IUnitOfWork`, a transação fica visível num só sítio.

## Consequências

- A Api precisa de duas ligações: a do dono, só para as migrações, e a da aplicação, para tudo o resto. No desenvolvimento, isso quer dizer correr `scripts/db-app-user.sh` depois das migrações.
- Todos os testes de integração ligam como `smartparking_api`. Os testes da F1 continuam a passar sem superutilizador.
- O papel é global ao *cluster*: o script tolera que já exista, e o `Down` da migração revoga as permissões mas mantém o grupo.
- A imutabilidade protege contra a aplicação, não contra o dono nem contra o superutilizador. Proteger a auditoria contra um administrador da base de dados fica fora do âmbito (RNF-06 na F11).
- Os Workers ainda ligam como dono (`appsettings.json` dos Workers). Revê-se na F3/F4, com o leitor da *outbox*, que precisa só de `SELECT` e `UPDATE` em `outbox_messages`.
- **Restrição retirada da especificação.** A especificação pedia `CHECK` de latitude ∈ [−90, 90] e longitude ∈ [−180, 180] em `parking_facilities`. Na implementação verificou-se que o tipo `geography(Point, 4326)` converte em silêncio as coordenadas fora do intervalo (por exemplo, uma latitude de 95 passa a 90, com um `NOTICE`). Esses `CHECK` nunca poderiam falhar. Com o acordo do Diogo (2026-11-24), saíram da migração `Initial`. O intervalo é garantido pelo `GeoCoordinate` do Domain e pelo validador do pedido (400, AC-17).

## Referências

- Texto das secções 5.4.1, 5.6.2 e 5.9.2 da tese (v16).
- Documentação do PostgreSQL 17 sobre `GRANT`, papéis e o tipo `geography` do PostGIS [VERIFICAR]: as páginas e as versões exatas não foram citadas nesta sessão.
