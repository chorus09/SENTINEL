
namespace Sentinel.Domain.Entities;

public sealed class Monitor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public int IntervalSeconds { get; set; } = 60;

    public int TimeoutSeconds { get; set; } = 10;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
        = DateTimeOffset.UtcNow;

    public DateTimeOffset? NextCheckAt { get; set; }
}
