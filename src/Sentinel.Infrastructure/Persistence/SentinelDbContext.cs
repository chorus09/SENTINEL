using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sentinel.Infrastructure.Identity;

using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Infrastructure.Persistence;

public sealed class SentinelDbContext
    : IdentityDbContext<SentinelUser, IdentityRole<Guid>, Guid>
{
    public SentinelDbContext(
        DbContextOptions<SentinelDbContext> options)
        : base(options)
    {
    }

    public DbSet<MonitorEntity> Monitors => Set<MonitorEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(SentinelDbContext).Assembly);
    }
}