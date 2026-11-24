---
paths:
  - "docs/**"
---
# Documentação que alimenta a tese

- Escrever em português europeu (AO90): utilizador, ficheiro, registo, desempenho, «a + infinitivo» em vez do gerúndio.
- Termos em inglês sem tradução consagrada em itálico (*sharding*, *outbox*); siglas por extenso na primeira ocorrência.
- Sem adjetivos de elogio não medidos («robusto», «escalável», «eficiente»): ou há número de `results/`, ou não se escreve.
- Nunca inventar citações, DOI ou páginas. Se faltar a fonte: `[VERIFICAR]`.
- ADR (`docs/adr/NNNN-titulo.md`): contexto, decisão, alternativas consideradas, consequências e o requisito (RF/RNF) e a secção da tese a que respondem.
- `docs/RASTREABILIDADE.md`: uma linha por requisito com UC, RF/RNF, `AC-n`, especificação, código e teste. Deve bater certo com a Tabela 17 da tese.
- `docs/DIARIO.md`: uma entrada por fase, com data, o que foi feito, problemas, decisões e ligações aos commits.
- Correspondência com a tese (v16): Cap. 4 requisitos; 5.9 implementação; Cap. 6 demonstração; 7.3 desempenho; 7.4 evolução do esquema; 7.5 comparação com o modo monolítico; 7.8 iterações ao artefacto.
