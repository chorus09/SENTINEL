namespace Sentinel.Domain.Entities;

public sealed class MonitorIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitorId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
