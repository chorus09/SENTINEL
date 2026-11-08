namespace Sentinel.Domain.Entities;

public sealed class MonitorCheckResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitorId { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public int DurationMilliseconds { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
}
