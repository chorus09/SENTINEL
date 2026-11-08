using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Monitoring;
using Sentinel.Infrastructure.Persistence;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Infrastructure.Monitoring;

public sealed class MonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpHealthChecker _checker;
    private readonly ILogger<MonitorWorker> _logger;
    private readonly MonitorWorkerOptions _options;

    public MonitorWorker(IServiceScopeFactory scopeFactory, IHttpHealthChecker checker,
        IOptions<MonitorWorkerOptions> options, ILogger<MonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _checker = checker;
        _logger = logger;
        _options = options.Value;
        if (_options.Enabled) _options.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                _logger.LogError("Monitoring worker batch failed; eligible leases will be retried.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task<int> RunBatchAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ClaimedMonitor> claims;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            var store = new MonitorLeaseStore(db);
            claims = await store.ClaimAsync(Math.Min(_options.BatchSize, _options.MaxConcurrentChecks),
                TimeSpan.FromSeconds(_options.LeaseDurationSeconds), cancellationToken);
        }

        await Task.WhenAll(claims.Select(claim => ProcessAsync(claim, cancellationToken)));
        return claims.Count;
    }

    private async Task ProcessAsync(ClaimedMonitor claim, CancellationToken cancellationToken)
    {
        HttpHealthCheckResult result;
        try
        {
            var monitor = new MonitorEntity
            {
                Id = claim.Id, OwnerId = claim.OwnerId, Url = claim.Url,
                TimeoutSeconds = claim.TimeoutSeconds, IntervalSeconds = claim.IntervalSeconds,
                IsEnabled = true,
            };
            result = await _checker.CheckAsync(monitor, cancellationToken);
            _logger.LogInformation("Monitor {MonitorId} check {Outcome} status {StatusCode} duration {DurationMs}ms",
                claim.Id, result.Outcome, result.StatusCode, result.Duration.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryReleaseAsync(claim);
            return;
        }
        catch (Exception)
        {
            _logger.LogWarning("Monitor {MonitorId} check failed unexpectedly.", claim.Id);
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = new MonitorLeaseStore(scope.ServiceProvider.GetRequiredService<SentinelDbContext>());
            var completed = await store.CompleteAsync(claim, result, cancellationToken);
            if (completed == 0) await store.ReleaseAsync(claim, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryReleaseAsync(claim);
        }
        catch (Exception)
        {
            _logger.LogError("Monitor {MonitorId} lease completion failed; lease expiry permits recovery.", claim.Id);
        }
    }

    private async Task TryReleaseAsync(ClaimedMonitor claim)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = new MonitorLeaseStore(scope.ServiceProvider.GetRequiredService<SentinelDbContext>());
            await store.ReleaseAsync(claim, cleanup.Token);
        }
        catch (Exception)
        {
            _logger.LogWarning("Monitor {MonitorId} lease cleanup deferred to expiry.", claim.Id);
        }
    }
}
