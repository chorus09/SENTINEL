namespace Sentinel.Infrastructure.Monitoring;

public sealed class MonitorWorkerOptions
{
    public const string SectionName = "MonitoringWorker";

    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxConcurrentChecks { get; set; } = 5;
    public int BatchSize { get; set; } = 10;
    public int LeaseDurationSeconds { get; set; } = 60;

    internal void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 3600
            || MaxConcurrentChecks is < 1 or > 32
            || BatchSize is < 1 or > 100
            || LeaseDurationSeconds is < 45 or > 86400)
            throw new InvalidOperationException("MonitoringWorker settings are invalid.");
    }
}
