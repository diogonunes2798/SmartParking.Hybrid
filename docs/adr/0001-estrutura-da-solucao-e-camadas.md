# ADR-0001 — Estrutura da solução, camadas e regra de dependências

- **Estado:** aceite
- **Data:** 14-11-2026
- **Fase:** 1 (Fundação)
- **Relaciona-se com:** Cap. 4 (4.1, 4.6), ESPECIFICACAO.md §5 (CQRS, Repository), CLAUDE.md («Estrutura da solução»)

## Contexto

O protótipo tem de demonstrar uma arquitetura híbrida de persistência em que cada motor de base de dados tem um papel distinto:
- PostgreSQL para os dados transacionais;
- MongoDB para os eventos de ocupação;
- Redis para o estado de disponibilidade.

Para que as medições dos Caps. 5 e 6 sejam interpretáveis, é preciso garantir três coisas:

1. A lógica dos casos de uso não depende de nenhum motor concreto. Isto permite a variante monolítica (§7, `Persistence:Mode=Monolithic`) e a troca das decisões em aberto (D1–D4) sem reescrever o código.
2. As subcamadas de persistência são independentes umas das outras, como o Cap. 4 pressupõe.
3. As regras arquiteturais são verificáveis de forma objetiva, e não apenas declaradas.

## Decisão

1. **Camadas concêntricas (Clean Architecture)**, com as dependências a apontar para dentro: `Domain` ← `Application` ← `Infrastructure.*` ← anfitriões (`Api`, `Workers`).
   - O `Domain` não tem dependências.
   - O `Application` define as interfaces (repositórios, `IEventBus`), e cada `Infrastructure.*` implementa-as para um único motor.
2. **Uma subcamada de infraestrutura por motor**, conforme o CLAUDE.md: `Relational`, `Documents`, `Cache` e `Messaging`.
   - Cada uma expõe um método de registo próprio (`AddRelationalPersistence`, `AddDocumentPersistence`, `AddCacheStore`).
   - Cada uma regista o respetivo *health check* de prontidão.
3. **CQRS sem biblioteca de mediação.** A §5 exclui bibliotecas de mediação com licença comercial (por exemplo, o MediatR a partir da versão 13). Os *handlers* de comandos e consultas serão classes simples, invocadas diretamente pelos *endpoints*. Isto também torna explícito o caminho de cada pedido nos *traces*.
4. **Projeto adicional `SmartParking.ServiceDefaults`** (desvio à estrutura inicial do CLAUDE.md). Contém a configuração transversal partilhada pela API e pelos Workers: OpenTelemetry e *endpoints* de *health*. É o mesmo padrão dos *service defaults* do .NET Aspire. Pode depender do `Application`, por causa dos nomes de telemetria, mas não da infraestrutura nem dos anfitriões.
5. **Os Workers são um anfitrião web mínimo.** Não expõem *endpoints* de negócio, mas expõem `/health/*` e `/metrics`, porque as métricas e a prontidão dos consumidores são necessárias para RNF-02 e RNF-03.
6. **Testes de arquitetura em dois níveis** (`tests/SmartParking.ArchitectureTests`). Funcionam como *fitness functions* que falham quando a regra de dependências é violada.
   - **Grafo de projetos** (`ProjectGraphTests`): lê os `.csproj` de `src/` e compara `ProjectReference`, `PackageReference` e `FrameworkReference` com uma lista de permissões por camada. Por exemplo:
     - o `Domain` não tem referências;
     - o `Application` só referencia o `Domain` e não usa pacotes;
     - cada subcamada de infraestrutura só referencia o `Application` e os pacotes do seu motor.

     Este teste apanha referências proibidas mesmo antes de serem usadas no código.
   - **Tipos (NetArchTest)** (`LayerDependencyTests`). Verifica:
     - que o `Domain` e o `Application` não usam os *drivers* dos motores (Npgsql, EF Core, MongoDB, StackExchange.Redis), ou seja, que a persistência fica isolada (*persistence ignorance*);
     - que as subcamadas não usam tipos umas das outras;
     - que a API não usa os *drivers* diretamente (CQRS: os *endpoints* passam pelos *handlers*).
7. **Reprodutibilidade do ambiente de compilação:**
   - `global.json` fixa o SDK (10.0.404, `rollForward: latestPatch`, ou seja, só correções 10.0.4xx);
   - `Directory.Packages.props` centraliza as versões NuGet;
   - `Directory.Build.props` impõe `net10.0`, `Nullable` e `TreatWarningsAsErrors`.

## Alternativas consideradas

- **Vertical Slice Architecture.** Organiza o código por funcionalidade e tem menos cerimónia. Foi rejeitada porque o objeto de estudo é precisamente a separação por motor de persistência: a fronteira «uma subcamada por motor» tem de ser visível na estrutura e testável.
- **Um único projeto `Infrastructure`.** É mais simples, mas deixaria de ser possível provar que as subcamadas são independentes, e a variante monolítica ficaria mais difícil de isolar.
- **MediatR ou outro mediador.** Foi excluído pela §5 (licença). Também acrescentaria indireção ao caminho medido.
- **Duplicar a configuração de OpenTelemetry e *health* nos dois `Program.cs`, sem ServiceDefaults.** Evitava o projeto extra, mas criava o risco de a API e os Workers medirem de forma diferente, o que prejudicaria a comparação.

## Consequências

- **Positivas:**
  - A troca de motor ou de decisão (D1–D4) e a variante monolítica ficam localizadas nas subcamadas de infraestrutura e no registo DI.
  - A regra de dependências é verificada automaticamente em cada `dotnet test`.
- **Negativas:**
  - Há mais projetos (nove em `src/`) e alguma cerimónia para um protótipo.
  - O NetArchTest analisa o IL, o que tem dois limites (verificados na Fase 1, ver o `DIARIO.md`):
    - as constantes (`const`) não contam como dependência, porque o compilador as copia para o código que as usa;
    - uma referência de projeto que ainda não é usada também não aparece.

    Por isso existe o teste do grafo de projetos. A lista de permissões tem de ser mantida quando se acrescentam pacotes (por exemplo, o EF Core na Fase 2, que já está previsto).
- **A rever:** a estrutura do CLAUDE.md foi atualizada para incluir `ServiceDefaults`, `tests/` e `infra/`.
