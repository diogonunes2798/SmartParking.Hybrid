using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Configurations.Users;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", table =>
            table.HasCheckConstraint("ck_users_email_lower_case", "email = lower(email)"));

        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();

        builder.Property(user => user.Name).HasMaxLength(User.NameMaxLength);
        builder.Property(user => user.Email).HasMaxLength(User.EmailMaxLength);
        builder.HasIndex(user => user.Email).IsUnique();
    }
}
