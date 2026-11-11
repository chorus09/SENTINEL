using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Monitoring;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Api.Endpoints;

public static class MonitorMetricsEndpoints
{
    public static IEndpointConventionBuilder MapMonitorMetricsEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/monitors/{id:guid}/metrics", async (
            Guid id, ClaimsPrincipal user, SentinelDbContext db, TimeProvider clock,
            [FromQuery] string? period, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var ownerId)
                || ownerId == Guid.Empty)
                return Results.Unauthorized();

            // Do not reveal period validity or check data for another owner's monitor.
            if (!await db.Monitors.AsNoTracking().AnyAsync(
                monitor => monitor.Id == id && monitor.OwnerId == ownerId, cancellationToken))
                return Results.NotFound();

            var selectedPeriod = period ?? "24h";
            var windowLength = selectedPeriod switch
            {
                "24h" => TimeSpan.FromHours(24),
                "7d" => TimeSpan.FromDays(7),
                "30d" => TimeSpan.FromDays(30),
                _ => TimeSpan.Zero,
            };
            if (windowLength == TimeSpan.Zero)
                return Results.BadRequest(new { error = "Period must be 24h, 7d, or 30d." });

            var windowEndUtc = clock.GetUtcNow().ToUniversalTime();
            var windowStartUtc = windowEndUtc - windowLength;
            var healthy = nameof(HttpHealthCheckOutcome.Healthy);

            // PostgreSQL aggregates only the bounded, indexed check range; no check rows reach the API.
            var aggregate = await db.Database.SqlQuery<MonitorMetricsAggregate>($"""
                SELECT COUNT(*)::bigint AS "TotalChecks",
                       COUNT(*) FILTER (WHERE "Outcome" = {healthy})::bigint AS "SuccessfulChecks",
                       AVG("DurationMilliseconds"::numeric)
                           FILTER (WHERE "Outcome" = {healthy}) AS "AverageResponseTimeMs",
                       MIN("DurationMilliseconds")
                           FILTER (WHERE "Outcome" = {healthy}) AS "MinResponseTimeMs",
                       MAX("DurationMilliseconds")
                           FILTER (WHERE "Outcome" = {healthy}) AS "MaxResponseTimeMs"
                FROM monitor_check_results
                WHERE "MonitorId" = {id}
                  AND "CheckedAt" >= {windowStartUtc}
                  AND "CheckedAt" < {windowEndUtc}
                """).SingleAsync(cancellationToken);

            var total = aggregate.TotalChecks;
            var successful = aggregate.SuccessfulChecks;
            var failed = total - successful;
            var successRate = total == 0 ? (decimal?)null
                : Math.Round(100m * successful / total, 2, MidpointRounding.AwayFromZero);
            var average = aggregate.AverageResponseTimeMs is { } value
                ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : (decimal?)null;

            return Results.Ok(new MonitorMetricsResponse(id, selectedPeriod, windowStartUtc, windowEndUtc,
                total, successful, failed, successRate, average,
                aggregate.MinResponseTimeMs, aggregate.MaxResponseTimeMs));
        }).RequireAuthorization("CookieOnly");

    private sealed class MonitorMetricsAggregate
    {
        public long TotalChecks { get; set; }
        public long SuccessfulChecks { get; set; }
        public decimal? AverageResponseTimeMs { get; set; }
        public int? MinResponseTimeMs { get; set; }
        public int? MaxResponseTimeMs { get; set; }
    }
}

public sealed record MonitorMetricsResponse(Guid MonitorId, string Period,
    DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc,
    long TotalChecks, long SuccessfulChecks, long FailedChecks,
    decimal? SuccessRatePercent, decimal? AverageResponseTimeMs,
    int? MinResponseTimeMs, int? MaxResponseTimeMs);
