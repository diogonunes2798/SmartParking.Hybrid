using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Common;

namespace SmartParking.Infrastructure.Relational.Configurations;

internal static class EntityTypeBuilderExtensions
{
    /// <summary>Pending domain events live only in memory; the unit of work turns them into outbox rows.</summary>
    public static EntityTypeBuilder<TAggregate> IgnoreDomainEvents<TAggregate>(this EntityTypeBuilder<TAggregate> builder)
        where TAggregate : AggregateRoot
    {
        builder.Ignore(aggregate => aggregate.DomainEvents);
        return builder;
    }
}
