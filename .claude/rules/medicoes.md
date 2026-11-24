---
paths:
  - "bench/**"
  - "results/**"
  - "tools/SensorSimulator/**"
---
# Medições para o Capítulo 7

- Cada experiência é reprodutível: um comando, uma configuração versionada em `bench/<cenario>/` e um CSV em `results/<cenario>/<AAAA-MM-DD_HHMM>.csv`.
- Colunas do CSV: `scenario, mode, metric, value, unit, run, config_hash, git_sha, machine`.
- Pelo menos 5 execuções por cenário e por modo (`hybrid` e `monolithic`), com aquecimento descartado e a mesma máquina para os dois modos.
- O simulador recebe sempre a semente (`--seed`) e regista-a no CSV, para que a carga seja igual entre execuções.
- Percentis calculados a partir das amostras brutas (p50, p95, p99), nunca a partir de médias.
- Consumo de recursos por indicadores indiretos (1.4 da tese): armazenamento ocupado e tempo de CPU por milhão de eventos. Não estimar energia.
- Nunca editar à mão nem apagar ficheiros em `results/`. Uma execução falhada fica registada e explica-se no `docs/DIARIO.md`.
- Nunca escrever na tese ou nos documentos um número que não venha de um CSV em `results/`.
- Registar no início de cada CSV (ou num `.meta.json` ao lado) o hardware, o sistema operativo, as versões das imagens e do SDK.
