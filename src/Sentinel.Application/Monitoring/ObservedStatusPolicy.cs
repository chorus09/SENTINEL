namespace Sentinel.Application.Monitoring;

public static class ObservedStatusPolicy
{
    public static string GetStatus(bool isEnabled, int intervalSeconds, DateTimeOffset? lastCheckedAt,
        string? lastOutcome, DateTimeOffset utcNow)
    {
        if (!isEnabled || lastCheckedAt is null || lastOutcome is null)
            return "UNKNOWN";

        if (lastCheckedAt.Value > utcNow)
            return "UNKNOWN";

        var staleAfterSeconds = Math.Max(3L * intervalSeconds, 300L);
        if (utcNow - lastCheckedAt.Value > TimeSpan.FromSeconds(staleAfterSeconds))
            return "UNKNOWN";

        return string.Equals(lastOutcome, nameof(HttpHealthCheckOutcome.Healthy), StringComparison.Ordinal)
            ? "UP" : "DOWN";
    }
}
