using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Entities;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class MonitorIncidentConfiguration : IEntityTypeConfiguration<MonitorIncident>
{
    public void Configure(EntityTypeBuilder<MonitorIncident> builder)
    {
        builder.ToTable("monitor_incidents", table => table.HasCheckConstraint(
            "CK_monitor_incidents_ResolvedAt", "\"ResolvedAt\" IS NULL OR \"ResolvedAt\" >= \"OpenedAt\""));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OpenedAt).IsRequired();
        builder.HasOne<MonitorEntity>().WithMany().HasForeignKey(x => x.MonitorId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.MonitorId)
            .IsUnique().HasFilter("\"ResolvedAt\" IS NULL")
            .HasDatabaseName("IX_monitor_incidents_Monitor_Open");
        builder.HasIndex(x => new { x.MonitorId, x.OpenedAt, x.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_monitor_incidents_Monitor_Recent");
    }
}
