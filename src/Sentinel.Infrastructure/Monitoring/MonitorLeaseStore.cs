using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
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

    public Task<int> CompleteAsync(ClaimedMonitor claim, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE monitors
            SET "LeaseId" = NULL, "LeaseExpiresAt" = NULL,
                "NextCheckAt" = clock_timestamp() + "IntervalSeconds" * INTERVAL '1 second'
            WHERE "Id" = {claim.Id} AND "LeaseId" = {claim.LeaseId}
              AND "LeaseExpiresAt" > clock_timestamp()
              AND "ConfigVersion" = {claim.ConfigVersion}
              AND "IsEnabled" = TRUE;
            """, cancellationToken);

    public Task<int> ReleaseAsync(ClaimedMonitor claim, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE monitors SET "LeaseId" = NULL, "LeaseExpiresAt" = NULL
            WHERE "Id" = {claim.Id} AND "LeaseId" = {claim.LeaseId};
            """, cancellationToken);
}
