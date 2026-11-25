using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Configurations.Facilities;

internal sealed class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> builder)
    {
        builder.ToTable("operators");
        builder.HasKey(@operator => @operator.Id);
        builder.Property(@operator => @operator.Id).ValueGeneratedNever();
        builder.Property(@operator => @operator.Name).HasMaxLength(Operator.NameMaxLength);
        builder.HasIndex(@operator => @operator.Name).IsUnique();

        // Reference operator: the only one until operator endpoints arrive (F11).
        builder.HasData(Operator.Create(Operator.ReferenceOperatorId, Operator.ReferenceOperatorName));
    }
}
