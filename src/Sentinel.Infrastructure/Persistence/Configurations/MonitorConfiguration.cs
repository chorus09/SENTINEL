
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Infrastructure.Identity;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class MonitorConfiguration
    : IEntityTypeConfiguration<MonitorEntity>
{
    public void Configure(EntityTypeBuilder<MonitorEntity> builder)
    {
        builder.ToTable("monitors");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(m => m.Slug)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(m => m.Url)
            .IsRequired()
            .HasMaxLength(2048);

        builder.HasOne<SentinelUser>()
            .WithMany()
            .HasForeignKey(m => m.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.OwnerId, m.Slug })
            .IsUnique();

        builder.Property<Guid?>("LeaseId");
        builder.Property<DateTimeOffset?>("LeaseExpiresAt");
        builder.Property<long>("ConfigVersion")
            .HasDefaultValue(0L)
            .IsConcurrencyToken();

        builder.HasIndex(m => new { m.NextCheckAt })
            .HasDatabaseName("IX_monitors_Due")
            .HasFilter("\"IsEnabled\" = TRUE");

        builder.ToTable(table => table.HasCheckConstraint(
            "CK_monitors_LeasePair",
            "(\"LeaseId\" IS NULL) = (\"LeaseExpiresAt\" IS NULL)"));

        builder.Property(m => m.IntervalSeconds)
            .IsRequired();

        builder.Property(m => m.TimeoutSeconds)
            .IsRequired();

        builder.Property(m => m.IsEnabled)
            .IsRequired();

        builder.Property(m => m.ConsecutiveUnhealthyCount).HasDefaultValue(0).IsRequired();
        builder.Property(m => m.ConsecutiveHealthyCount).HasDefaultValue(0).IsRequired();

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_monitors_ConsecutiveUnhealthyCount", "\"ConsecutiveUnhealthyCount\" >= 0");
            table.HasCheckConstraint("CK_monitors_ConsecutiveHealthyCount", "\"ConsecutiveHealthyCount\" >= 0");
        });

        builder.Property(m => m.CreatedAt)
            .IsRequired();
    }
}
