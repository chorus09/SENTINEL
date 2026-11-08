using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Application.Monitoring;

public interface IHttpHealthChecker
{
    Task<HttpHealthCheckResult> CheckAsync(MonitorEntity monitor, CancellationToken cancellationToken);
}

public enum HttpHealthCheckOutcome
{
    Healthy,
    HttpError,
    Timeout,
    DnsFailure,
    ConnectionFailure,
    UnsafeDestination,
    NetworkFailure,
}

public sealed record HttpHealthCheckResult(
    HttpHealthCheckOutcome Outcome,
    int? StatusCode,
    TimeSpan Duration);
