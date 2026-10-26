
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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

        builder.HasIndex(m => m.Slug)
            .IsUnique();

        builder.Property(m => m.IntervalSeconds)
            .IsRequired();

        builder.Property(m => m.TimeoutSeconds)
            .IsRequired();

        builder.Property(m => m.IsEnabled)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .IsRequired();
    }
}
