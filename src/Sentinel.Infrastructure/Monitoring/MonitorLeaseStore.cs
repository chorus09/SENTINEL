using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Sentinel.Application.Monitoring;
using Sentinel.Domain.Entities;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Monitoring;

internal sealed record ClaimedMonitor(Guid Id, Guid OwnerId, string Url, int TimeoutSeconds,
    int IntervalSeconds, long ConfigVersion, Guid LeaseId);

internal sealed class MonitorLeaseStore(SentinelDbContext db)
{
    public async Task<IReadOnlyList<ClaimedMonitor>> ClaimAsync(int batchSize, TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var leaseId = Guid.NewGuid();
        var claimed = new List<ClaimedMonitor>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = (NpgsqlCommand)db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                WITH due AS (
                    SELECT "Id" FROM monitors
                    WHERE "IsEnabled" = TRUE
                      AND "NextCheckAt" <= clock_timestamp()
                      AND ("LeaseId" IS NULL OR "LeaseExpiresAt" <= clock_timestamp())
                    ORDER BY "NextCheckAt", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT @batchSize
                )
                UPDATE monitors AS m
                SET "LeaseId" = @leaseId,
                    "LeaseExpiresAt" = clock_timestamp() + @leaseDuration
                FROM due
                WHERE m."Id" = due."Id"
                RETURNING m."Id", m."OwnerId", m."Url", m."TimeoutSeconds",
                          m."IntervalSeconds", m."ConfigVersion";
                """;
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });
            command.Parameters.Add(new NpgsqlParameter("leaseId", NpgsqlDbType.Uuid) { Value = leaseId });
            command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval) { Value = leaseDuration });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ClaimedMonitor(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5), leaseId));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return claimed;
    }

    public async Task<int> CompleteAsync(ClaimedMonitor claim, HttpHealthCheckResult result,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(result.Outcome)
            || result.StatusCode is < 100 or > 599)
            throw new InvalidOperationException("The check result classification is invalid.");

        var duration = (int)Math.Clamp(Math.Ceiling(result.Duration.TotalMilliseconds), 0, int.MaxValue);
        var healthy = result.Outcome == HttpHealthCheckOutcome.Healthy;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE monitors
            SET "LeaseId" = NULL, "LeaseExpiresAt" = NULL,
                "NextCheckAt" = clock_timestamp() + "IntervalSeconds" * INTERVAL '1 second',
                "ConsecutiveUnhealthyCount" = CASE WHEN {healthy} THEN 0
                    WHEN "ConsecutiveUnhealthyCount" < 2147483647
                    THEN "ConsecutiveUnhealthyCount" + 1 ELSE 2147483647 END,
                "ConsecutiveHealthyCount" = CASE WHEN {healthy}
                    THEN CASE WHEN "ConsecutiveHealthyCount" < 2147483647
                        THEN "ConsecutiveHealthyCount" + 1 ELSE 2147483647 END
                    ELSE 0 END
            WHERE "Id" = {claim.Id} AND "LeaseId" = {claim.LeaseId}
              AND "LeaseExpiresAt" > clock_timestamp()
              AND "ConfigVersion" = {claim.ConfigVersion}
              AND "IsEnabled" = TRUE;
            """, cancellationToken);
        if (updated != 1) return 0;

        var counts = await db.Monitors.AsNoTracking().Where(m => m.Id == claim.Id)
            .Select(m => new { m.ConsecutiveUnhealthyCount, m.ConsecutiveHealthyCount })
            .SingleAsync(cancellationToken);
        // Use the database clock so different worker hosts agree on transition time.
        var checkedAt = await db.Database.SqlQuery<DateTimeOffset>(
            $"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        var check = new MonitorCheckResult
        {
            Id = Guid.NewGuid(),
            MonitorId = claim.Id,
            CheckedAt = checkedAt,
            DurationMilliseconds = duration,
            Outcome = result.Outcome.ToString(),
            HttpStatusCode = result.StatusCode,
        };
        db.MonitorCheckResults.Add(check);

        if (!healthy && counts.ConsecutiveUnhealthyCount == 3
            && !await db.MonitorIncidents.AnyAsync(
                x => x.MonitorId == claim.Id && x.ResolvedAt == null, cancellationToken))
        {
            db.MonitorIncidents.Add(new MonitorIncident
            {
                MonitorId = claim.Id,
                OpenedAt = checkedAt,
            });
        }
        else if (healthy && counts.ConsecutiveHealthyCount == 2)
        {
            var open = await db.MonitorIncidents.SingleOrDefaultAsync(
                x => x.MonitorId == claim.Id && x.ResolvedAt == null, cancellationToken);
            if (open is not null)
            {
                // A backward clock adjustment must not reject a valid recovery.
                open.ResolvedAt = checkedAt < open.OpenedAt ? open.OpenedAt : checkedAt;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return 1;
    }

    public Task<int> ReleaseAsync(ClaimedMonitor claim, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE monitors SET "LeaseId" = NULL, "LeaseExpiresAt" = NULL
            WHERE "Id" = {claim.Id} AND "LeaseId" = {claim.LeaseId};
            """, cancellationToken);
}
