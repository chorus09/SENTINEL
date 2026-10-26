
using Microsoft.EntityFrameworkCore;
using Sentinel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Sentinel.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();

var connectionString =
    builder.Configuration.GetConnectionString("Sentinel")
    ?? throw new InvalidOperationException(
        "Connection string 'Sentinel' is missing.");

builder.Services.AddDbContext<SentinelDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services
    .AddIdentityCore<SentinelUser>(options =>
    {
        options.User.RequireUniqueEmail = true;

        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;

        options.Lockout.DefaultLockoutTimeSpan =
            TimeSpan.FromMinutes(15);

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<SentinelDbContext>()
    .AddDefaultTokenProviders();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "SENTINEL API",
    Timestamp = DateTimeOffset.UtcNow
}));

app.Run();
