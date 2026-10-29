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
    public DbSet<PersonalAccessToken> PersonalAccessTokens
    => Set<PersonalAccessToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PersonalAccessToken>(entity =>
        {
            entity.ToTable("personal_access_tokens");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(x => x.TokenHash)
                .IsRequired()
                .HasMaxLength(64);

            entity.Property(x => x.CreatedAt)
                .IsRequired();

            entity.Property(x => x.ExpiresAt)
                .IsRequired();

            entity.HasIndex(x => x.TokenHash)
                .IsUnique();

            entity.HasIndex(x => x.UserId);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(SentinelDbContext).Assembly);
    }
}