# Diário do projeto

Registo, por fase, do trabalho no protótipo SmartParking.Hybrid: o que foi feito, problemas (incluindo tentativas falhadas), decisões e ligações aos *commits*. É a matéria-prima das secções 5.9 (implementação) e 7.8 (iterações ao artefacto) da tese.

Formato de cada entrada:

- **Data**
- **Feito**
- **Problemas**
- **Decisões**
- ***Commits***

---

## A1–A2: preparação

- **Data:** 2026-11-24
- **Feito:**
  - Repositório criado, com licença, `.gitignore` para .NET e terminações de linha LF.
  - Instruções do projeto (`CLAUDE.md`) e regras do Claude Code (`.claude/rules/`).
  - *Hooks* de *pre-commit* do dotnet-claude-kit.
  - Esqueleto da documentação: índices de `docs/specs/`, `docs/plans/` e `docs/adr/`, `docs/RASTREABILIDADE.md`, este diário, `results/` e `bench/`.
- **Versões das ferramentas** (recolhidas a 2026-11-24 com `dotnet --version`, `claude --version`, `dotnet tool list -g` e o `plugin.json` do *plugin*):
  - .NET SDK: 10.0.404 (é o SDK por omissão da máquina; o `global.json` ainda não existe);
  - Claude Code: 2.1.325;
  - dotnet-claude-kit: 0.14.0;
  - CWM.RoslynNavigator (`cwm.roslynnavigator`, ferramenta global .NET): 0.8.0.
- **Problemas:** por preencher.
- **Decisões:** por preencher.
- ***Commits:*** `5fca546`, `2720bab`, `9fb74d3`, `6f8d1b7`, `743000e` (esqueleto da documentação).
