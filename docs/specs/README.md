# Especificações

Índice das especificações do protótipo, no formato do comando `/spec` do *kit* (dotnet-claude-kit). Corresponde ao ponto 7b do plano da tese.

- Cada especificação fica em `docs/specs/<NNN>-<slug>.md`.
- Ciclo de vida do estado: Por especificar → Rascunho (*Draft*) → Em revisão (*In Review*) → Aprovada (*Approved*, com data). «Por especificar» quer dizer que o número e o tema estão reservados, mas o `/spec` ainda não correu.
- A implementação de uma fase só começa a partir da especificação aprovada dessa fase; cada critério de aceitação (`AC-n`) tem um teste com esse nome.
- Cada especificação indica os casos de uso (UC), os requisitos funcionais (RF) e os requisitos não funcionais (RNF) da tese a que responde; a ligação fica registada em [`docs/RASTREABILIDADE.md`](../RASTREABILIDADE.md).
- A especificação 000 contém a secção «Deferred Decisions», onde ficam as decisões D1 a D10.

## Princípios da divisão em fases

1. Cada fase entrega algo que se testa sozinho e responde a requisitos identificados da tese (Tabelas 13 e 14).
2. Uma fase cabe numa ou duas sessões do Claude Code. Se o plano (`/plan`) passar de cerca de 15 passos, a fase divide-se (por exemplo, F4a e F4b) e a especificação também.
3. As fases de base (F1 a F3) vêm antes da ingestão, porque a *outbox* e o modelo relacional são usados por quase tudo.
4. As medições (F12) só começam quando os dois modos de persistência (híbrido e monolítico) estão implementados, isto é, depois de F9.
5. A numeração das especificações segue a ordem das fases (F*n* ↔ especificação *n*−1) e não a ordem de criação, para que a fase e a especificação se encontrem uma pela outra.

## Ordem das fases e especificações

| Fase | Tema | Requisitos e objetivos | Especificação | Depende de | Secção da tese | Estado | Data de aprovação |
|---|---|---|---|---|---|---|---|
| F1 | Fundação da solução | transversal; OE6 | [000-visao-geral](000-visao-geral.md) | — | 5.9.1 | Aprovada | 2026-11-24 |
| F2 | Domínio, subcamada relacional e configuração (implementação em F2a e F2b) | RF-06; UC-02 e UC-05 (configuração); RNF-06 (auditoria) | [001-configuracao](001-configuracao.md) | F1 | 5.1, 5.4, 5.9.2 | Implementada e integrada na `main` (PR #2, 2026-11-25) | 2026-11-24 |
| F3 | Sessões, reservas e pagamentos (implementação em F3a e F3b) | UC-02; RF-02 | [002-sessoes-pagamentos](002-sessoes-pagamentos.md) | F2 | 5.4.1, 5.4.2 | F3a e F3b implementadas e integradas na `main` (PR #3, 2026-11-25) | 2026-11-25 |
| F4 | Ingestão de eventos | UC-01; RF-01 | [003-ingestao](003-ingestao.md) | F2 | 5.3, 5.5, 5.9.4 | Aprovada (F4a integrada na `main`, PR #4; F4b implementada no ramo `feat/003-simulador`, por integrar) | 2026-11-25 |
| F5 | Vista de disponibilidade e pesquisa geoespacial | UC-03; RF-03, RF-04 | [004-disponibilidade](004-disponibilidade.md) | F4 | 5.5.3, 5.7 | Por especificar | — |
| F6 | Sincronização e janela de consistência | OE4; RNF-01 (UC-01) | [005-sincronizacao](005-sincronizacao.md) | F4, F5 | 5.6.2, 5.8 | Por especificar | — |
| F7 | Camada analítica | UC-04; RF-05 | [006-analitica](006-analitica.md) | F3, F6 | 5.6.3 | Por especificar | — |
| F8 | Simulador de sensores e evolução do esquema | UC-05; RF-01 (`payload` extensível), RNF-04 | [007-simulador-esquema](007-simulador-esquema.md) | F4 | 5.5.1, 7.4 | Por especificar | — |
| F9 | Modo monolítico | OE7 | [008-modo-monolitico](008-modo-monolitico.md) | F3 a F8 | 7.5 | Por especificar | — |
| F10 | Ciclo de vida dos dados e sustentabilidade | RNF-05; OE5 | [009-ciclo-de-vida](009-ciclo-de-vida.md) | F4, F7 | 5.6.3, 7.3 | Por especificar | — |
| F11 | Segurança e proteção de dados | RNF-06 | [010-seguranca](010-seguranca.md) | F2, F3 | 5.4.1 | Por especificar | — |
| F12 | Resiliência e medições | RNF-01, RNF-02, RNF-03; OE7 | [011-medicoes](011-medicoes.md) | F9 (e F10, F11) | 7.1 a 7.5 | Por especificar | — |
| F13 | Demonstração | OE6 | [012-demonstracao](012-demonstracao.md) | todas | Cap. 6 | Por especificar | — |

A coluna «Depende de» é uma proposta derivada dos princípios 3 e 4 e do conteúdo de cada fase; confirma-se no `/spec` de cada fase. As ligações apontam para ficheiros que ainda não existem: passam a funcionar quando o `/spec` da fase os criar.
