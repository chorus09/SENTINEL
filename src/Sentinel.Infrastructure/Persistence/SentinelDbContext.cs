
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Infrastructure.Persistence;

public sealed class SentinelDbContext : DbContext
{
    public SentinelDbContext(
        DbContextOptions<SentinelDbContext> options)
        : base(options)
    {
    }

    public DbSet<MonitorEntity> Monitors => Set<MonitorEntity>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(SentinelDbContext).Assembly);
    }
}
