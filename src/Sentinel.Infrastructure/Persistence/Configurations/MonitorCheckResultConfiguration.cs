using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Entities;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class MonitorCheckResultConfiguration : IEntityTypeConfiguration<MonitorCheckResult>
{
    public void Configure(EntityTypeBuilder<MonitorCheckResult> builder)
    {
        builder.ToTable("monitor_check_results", table =>
        {
            table.HasCheckConstraint("CK_monitor_check_results_Duration",
                "\"DurationMilliseconds\" >= 0");
            table.HasCheckConstraint("CK_monitor_check_results_HttpStatus",
                "\"HttpStatusCode\" IS NULL OR \"HttpStatusCode\" BETWEEN 100 AND 599");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.CheckedAt).IsRequired();
        builder.Property(x => x.DurationMilliseconds).IsRequired();
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(32);

        builder.HasOne<MonitorEntity>()
            .WithMany()
            .HasForeignKey(x => x.MonitorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.MonitorId, x.CheckedAt, x.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_monitor_check_results_Monitor_Recent");
    }
}
