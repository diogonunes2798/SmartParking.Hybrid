# ADR 0003 — Repository por raiz de agregado sobre o EF Core

- **Estado:** Aceite (decisão D10, 2026-11-24)
- **Data:** 2026-11-24
- **Requisitos:** RF-02 (integridade transacional das sessões, reservas e pagamentos); prepara a comparação do Cap. 7.5 (modo monolítico).
- **Decisão (D1–D10):** D10.
- **Secções da tese:** 5.4.2 e 5.7.
- **Especificação:** [000-visao-geral](../specs/000-visao-geral.md) (F1, AC-15).

## Contexto

O *kit* (dotnet-claude-kit) recomenda não envolver o EF Core em repositórios: o `DbContext` já é uma unidade de trabalho e o `DbSet` já é um repositório. No protótipo, porém, a mesma camada Application tem de funcionar sobre duas persistências diferentes (`Persistence:Mode=Hybrid|Monolithic`). Se Application dependesse do `DbContext`, o modo híbrido e o monolítico não poderiam ter implementações próprias das escritas, e Application ficaria acoplada ao EF Core. A tese (5.4.2 e 5.7) descreve o padrão Repository na escrita e consultas dedicadas na leitura (CQRS).

## Decisão

- **Escrita:** um repositório por raiz de agregado. A interface fica em `Application/Abstractions` e há uma implementação por subcamada e por `Persistence:Mode`. Os métodos têm a forma do domínio (`GetByIdAsync`, `Add`…) e nunca devolvem `IQueryable`.
- **Unidade de trabalho:** o repositório nunca chama `SaveChanges`. O `DbContext` é a unidade de trabalho (`IUnitOfWork`, interface criada na F1 e implementada na F2) e grava o agregado e a *outbox* na mesma transação.
- **Por dentro:** o repositório usa o `DbContext` diretamente, com consultas compiladas, sem esconder o *change tracking*.
- **Leitura:** sem repositórios. Cada consulta tem uma porta de leitura própria (por exemplo, `IAvailabilityReader`, `IReportReader`), implementada com projeções e `AsNoTracking`, ou sobre o Redis e as tabelas de resumo.
- **Proibido:** repositório genérico (`IRepository<T>`) e métodos CRUD genéricos.
- Aplicada no `CLAUDE.md` e na secção «Data Access» de `.claude/rules/architecture.md`. Os testes de arquitetura (AC-3 da especificação 000) garantem que Application não usa EF Core, MongoDB nem Redis.

## Alternativas consideradas

- **`DbContext` injetado em Application (regra original do *kit*):** menos código, mas Application passa a depender do EF Core e o modo monolítico não pode substituir as escritas sem alterar os *handlers*.
- **Repositório genérico `IRepository<T>`:** um só tipo para tudo, mas esconde o domínio atrás de operações CRUD, convida a devolver `IQueryable` e não exprime as regras de cada agregado.
- **Repositórios também na leitura:** uniformiza o acesso, mas obriga as consultas de elevada frequência (UC-03) a passar por objetos de domínio, contra o CQRS da tese.
- **Especificações (*Specification pattern*) sobre um repositório genérico:** mais flexível, mas mais abstração sem ganho para o número de agregados do protótipo (três).

## Consequências

- Cada agregado novo traz uma interface e duas implementações (híbrida e monolítica) quando a persistência difere entre modos.
- Os *handlers* de comando recebem o repositório e o `IUnitOfWork` e não conhecem o EF Core; os testes de integração usam as implementações reais com Testcontainers.
- É uma exceção deliberada à regra do *kit*; quem ler o código tem de encontrar aqui a justificação.

## Referências

- Texto das secções 5.4.2 e 5.7 da tese (v16).
- Referência pedida pelo orientador para o padrão Repository: por escolher entre Fowler (2002), Evans (2003) e o guia da Microsoft *.NET Microservices: Architecture for Containerized .NET Applications* [VERIFICAR]. Nenhuma foi confirmada nesta sessão (edição, páginas e URL por confirmar).
