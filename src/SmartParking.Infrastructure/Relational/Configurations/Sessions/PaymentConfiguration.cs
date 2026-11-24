using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Configurations.Sessions;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", table =>
            table.HasCheckConstraint("ck_payments_amount", "amount >= 0"));

        builder.HasKey(payment => payment.Id);
        builder.Property(payment => payment.Id).ValueGeneratedNever();
        builder.Property(payment => payment.Amount).HasPrecision(10, 2);

        builder.HasOne<ParkingSession>().WithMany().HasForeignKey(payment => payment.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(payment => payment.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);

        // At most one successful payment per session (spec 001, AC-19).
        builder.HasIndex(payment => payment.SessionId)
            .HasDatabaseName("ux_payments_session_succeeded")
            .IsUnique()
            .HasFilter("status = 'Succeeded'");
    }
}
