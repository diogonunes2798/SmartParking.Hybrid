using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Configurations.Users;

/// <summary>Provider token and masked number only; never the PAN or the CVV.</summary>
internal sealed class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
    public void Configure(EntityTypeBuilder<PaymentMethod> builder)
    {
        builder.ToTable("payment_methods");
        builder.HasKey(method => method.Id);
        builder.Property(method => method.Id).ValueGeneratedNever();

        builder.Property(method => method.ProviderToken).HasMaxLength(PaymentMethod.ProviderTokenMaxLength);
        builder.Property(method => method.MaskedNumber).HasMaxLength(PaymentMethod.MaskedNumberMaxLength);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(method => method.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
