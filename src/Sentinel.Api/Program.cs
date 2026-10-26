
using Microsoft.EntityFrameworkCore;
using Sentinel.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Sentinel")
    ?? throw new InvalidOperationException(
        "Connection string 'Sentinel' is missing.");

builder.Services.AddDbContext<SentinelDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "SENTINEL API",
    Timestamp = DateTimeOffset.UtcNow
}));

app.Run();
