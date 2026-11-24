---
alwaysApply: true
description: >
  Enforces dependency direction, feature organization, and module boundary
  rules for .NET solution architecture.
---

# Architecture Rules

## Ask First, Recommend Second

- **Never assume an architecture — use the architecture-advisor skill.** Every project has different constraints. Ask about team size, domain complexity, and deployment model before recommending Clean Architecture, VSA, DDD, or Modular Monolith.

## Data Access

<!-- Project exception D10 (decided 24-11-2026): replaces the kit rule "No repository pattern over EF Core".
     Reason: hybrid persistence (PostgreSQL, MongoDB, Redis) and Persistence:Mode=Hybrid|Monolithic need the same
     ports in Application for every implementation. See CLAUDE.md and docs/adr/0003. -->
- **Repository per aggregate root on the write side; no repositories on the read side (project exception D10).** `DbContext` stays the Unit of Work. Repositories exist only so that Application does not depend on EF Core and so that each persistence mode can provide its own implementation.
- **Never create a generic repository** (`IRepository<T>`) or generic CRUD methods.
- **Write side:** one repository per aggregate root. Interface in `Application/Abstractions`, implementation in Infrastructure (one per persistence sublayer and per `Persistence:Mode`). Domain-shaped methods only; never return `IQueryable`.
- **Repositories never call `SaveChanges`.** They use `DbContext` internally (change tracking, batching and compiled queries stay available); the unit of work saves the aggregate and the outbox in the same transaction.
- **Read side:** no repositories. Each query uses its own read port in `Application/Abstractions`, implemented in Infrastructure with projections and `AsNoTracking`, or over Redis and the summary tables.
- **Application never references `DbContext` or EF Core directly.**

```csharp
// DO — write port per aggregate root (Application/Abstractions)
public interface IParkingSessionRepository
{
    Task<ParkingSession?> GetByIdAsync(Guid id, CancellationToken ct);
    void Add(ParkingSession session);
}

// DO — EF Core implementation (Infrastructure); no SaveChanges here
internal sealed class ParkingSessionRepository(AppDbContext db) : IParkingSessionRepository
{
    public Task<ParkingSession?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.ParkingSessions.FindAsync([id], ct).AsTask();

    public void Add(ParkingSession session) => db.ParkingSessions.Add(session);
}

// DO — read port, no repository (Application/Abstractions)
public interface IAvailabilityReader
{
    Task<FacilityAvailability?> GetAsync(Guid facilityId, CancellationToken ct);
}

// DON'T — generic repository wrapping EF
public interface IRepository<T> { Task<T?> GetByIdAsync(Guid id); }

// DON'T — DbContext injected into an Application handler
public sealed class CloseSessionHandler(AppDbContext db) { /* ... */ }
```

## Endpoint Organization

- **Every endpoint group gets its own file implementing `IEndpointGroup`.** Never define endpoints inline in `Program.cs`. Each feature/resource gets a dedicated `*Endpoints.cs` file.
- **Use `app.MapEndpoints()` for auto-discovery.** Program.cs calls `app.MapEndpoints()` once. This scans the assembly for all `IEndpointGroup` implementations and registers them. Program.cs never changes when adding new endpoints.
- **Never add `MapGroup` or `Map*Endpoints()` calls to Program.cs.** Both inline endpoints and manual extension-method wiring in Program.cs are anti-patterns. The `IEndpointGroup` interface + auto-discovery is the only accepted pattern.

```csharp
// DO — auto-discovered endpoint group in its own file
public sealed class OrderEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders");
        group.MapGet("/", ListOrders);
        group.MapPost("/", CreateOrder);
    }
}

// DON'T — manual wiring in Program.cs
app.MapGroup("/api/orders").MapOrderEndpoints();
app.MapGroup("/api/products").MapProductEndpoints();
```

## Project Organization

- **Feature folders over layer folders.** Vertical slices keep related code together, reducing the number of files you touch per feature.

```
# DO                          # DON'T
Features/                     Controllers/
  Orders/                       OrdersController.cs
    CreateOrder.cs              ProductsController.cs
    OrderEndpoints.cs           GetOrder.cs
  Products/                   Services/
    CreateProduct.cs            OrderService.cs
    ProductEndpoints.cs         ProductService.cs
```

- **Dependency direction is inward.** Domain depends on nothing. Application depends on Domain. Infrastructure depends on Application. Presentation depends on Application. Never reverse these arrows.
- **Module boundaries enforced through project references.** If two modules should not couple, they must not have a project reference. Use integration events or a shared contracts project for cross-module communication.

## Shared Kernel

- **Shared kernel contains only contracts, never business logic.** Shared projects hold interfaces, DTOs, and integration event definitions. Domain logic belongs in the owning module. Leaking logic into shared projects creates hidden coupling.

```csharp
// DO — shared kernel has contracts
public interface IOrderPlaced
{
    Guid OrderId { get; }
    DateTimeOffset OccurredAt { get; }
}

// DON'T — shared kernel has domain logic
public static class PricingCalculator { /* business rules */ }
```
