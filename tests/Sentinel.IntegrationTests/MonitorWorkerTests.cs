using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sentinel.Application.Monitoring;
using Sentinel.Domain.Entities;
using Sentinel.Infrastructure.Identity;
using Sentinel.Infrastructure.Monitoring;
using Sentinel.Infrastructure.Persistence;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class MonitorWorkerTests : IntegrationTestBase
{
    private static readonly HttpHealthCheckResult Healthy = new(HttpHealthCheckOutcome.Healthy, 200, TimeSpan.Zero);

    [Fact]
    public async Task DisabledWorkerDoesNotCheckOrClaim()
    {
        var id = await CreateMonitorAsync();
        var calls = 0;
        using var worker = Worker(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Healthy); },
            new MonitorWorkerOptions { Enabled = false });
        Assert.Equal(0, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        Assert.Null((await ReadAsync(id)).LeaseId);
    }

    [Fact]
    public async Task DueMonitorIsCheckedAndRescheduledAfterCompletion()
    {
        var id = await CreateMonitorAsync();
        var calls = 0;
        using var worker = Worker(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Healthy); });
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        var state = await ReadAsync(id);
        Assert.Equal(1, calls);
        Assert.Null(state.LeaseId);
        Assert.Null(state.LeaseExpiresAt);
        Assert.True(state.NextCheckAt >= before.AddSeconds(60));
        Assert.True(state.NextCheckAt <= DateTimeOffset.UtcNow.AddSeconds(60));
        var saved = Assert.Single(await HistoryAsync(id));
        Assert.Equal(nameof(HttpHealthCheckOutcome.Healthy), saved.Outcome);
        Assert.Equal(200, saved.HttpStatusCode);
        Assert.Equal(0, saved.DurationMilliseconds);
    }

    [Theory]
    [InlineData(HttpHealthCheckOutcome.HttpError, 500)]
    [InlineData(HttpHealthCheckOutcome.Timeout, null)]
    [InlineData(HttpHealthCheckOutcome.DnsFailure, null)]
    public async Task UnsuccessfulStructuredChecksArePersisted(HttpHealthCheckOutcome outcome, int? status)
    {
        var id = await CreateMonitorAsync();
        using var worker = Worker(_ => Task.FromResult(
            new HttpHealthCheckResult(outcome, status, TimeSpan.FromMilliseconds(12.2))));
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        var saved = Assert.Single(await HistoryAsync(id));
        Assert.Equal(outcome.ToString(), saved.Outcome);
        Assert.Equal(status, saved.HttpStatusCode);
        Assert.Equal(13, saved.DurationMilliseconds);
    }

    [Fact]
    public async Task ResultInsertFailureRollsBackSchedulingAndLeaseRelease()
    {
        var id = await CreateMonitorAsync();
        await Database.AssertLockHeldAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE monitor_check_results ADD CONSTRAINT CK_test_reject_insert CHECK (FALSE)",
                TestContext.Current.CancellationToken);
        }
        var before = await ReadAsync(id);
        using var worker = Worker(_ => Task.FromResult(Healthy));
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        var after = await ReadAsync(id);
        Assert.Equal(before.NextCheckAt, after.NextCheckAt);
        Assert.NotNull(after.LeaseId);
        Assert.Empty(await HistoryAsync(id));
    }

    [Fact]
    public async Task FutureAndDisabledMonitorsAreSkipped()
    {
        var future = await CreateMonitorAsync(next: DateTimeOffset.UtcNow.AddHours(1));
        var disabled = await CreateMonitorAsync(enabled: false);
        var calls = 0;
        using var worker = Worker(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Healthy); });
        Assert.Equal(0, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        Assert.Null((await ReadAsync(future)).LeaseId);
        Assert.Null((await ReadAsync(disabled)).LeaseId);
    }

    [Fact]
    public async Task FailedCheckDoesNotStopOtherMonitors()
    {
        var failed = await CreateMonitorAsync();
        var other = await CreateMonitorAsync();
        var checkedIds = new List<Guid>();
        var sync = new object();
        using var worker = Worker(m =>
        {
            lock (sync) checkedIds.Add(m.Id);
            if (m.Id == failed) throw new InvalidOperationException("private error detail");
            return Task.FromResult(Healthy);
        });
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(2, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Contains(failed, checkedIds);
        Assert.Contains(other, checkedIds);
        Assert.True((await ReadAsync(failed)).NextCheckAt <= before);
        Assert.NotNull((await ReadAsync(failed)).LeaseId);
        Assert.Empty(await HistoryAsync(failed));
        Assert.True((await ReadAsync(other)).NextCheckAt >= before.AddSeconds(60));
        Assert.Single(await HistoryAsync(other));
    }

    [Fact]
    public async Task TwoWorkersCannotClaimSameMonitor()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        using var first = Worker(async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return Healthy;
        });
        using var second = Worker(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Healthy);
        });
        var firstRun = first.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull((await ReadAsync(id)).LeaseId);
            Assert.Equal(0, await second.RunBatchAsync(TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await firstRun);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredLeaseIsRecovered()
    {
        var id = await CreateMonitorAsync();
        await ChangeAsync(id, db =>
        {
            db.Entry(db.Monitors.Local.Single()).Property<Guid?>("LeaseId").CurrentValue = Guid.NewGuid();
            db.Entry(db.Monitors.Local.Single()).Property<DateTimeOffset?>("LeaseExpiresAt").CurrentValue =
                DateTimeOffset.UtcNow.AddMinutes(-1);
        });
        var calls = 0;
        using var worker = Worker(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Healthy); });
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        Assert.Null((await ReadAsync(id)).LeaseId);
        Assert.Single(await HistoryAsync(id));
    }

    [Fact]
    public async Task StaleLeaseCannotOverwriteReplacementSchedule()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        var release = Signal();
        using var old = Worker(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Healthy;
        });
        var oldRun = old.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var replacement = Guid.NewGuid();
            var future = DateTimeOffset.UtcNow.AddHours(1);
            await ChangeAsync(id, db =>
            {
                var m = db.Monitors.Local.Single();
                m.NextCheckAt = future;
                db.Entry(m).Property<Guid?>("LeaseId").CurrentValue = replacement;
                db.Entry(m).Property<DateTimeOffset?>("LeaseExpiresAt").CurrentValue =
                    DateTimeOffset.UtcNow.AddMinutes(1);
            });
            var expected = await ReadAsync(id);
            release.TrySetResult();
            Assert.Equal(1, await oldRun);
            var state = await ReadAsync(id);
            Assert.Equal(replacement, state.LeaseId);
            Assert.Equal(expected.NextCheckAt, state.NextCheckAt);
            Assert.Empty(await HistoryAsync(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ExpiredLeaseCannotPersistResultOrAdvanceSchedule()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        var release = Signal();
        using var worker = Worker(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Healthy;
        });
        var run = worker.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var before = await ReadAsync(id);
            await ChangeAsync(id, db => db.Entry(db.Monitors.Local.Single())
                .Property<DateTimeOffset?>("LeaseExpiresAt").CurrentValue = DateTimeOffset.UtcNow.AddSeconds(-1));
            release.TrySetResult();
            Assert.Equal(1, await run);
            var after = await ReadAsync(id);
            Assert.Equal(before.NextCheckAt, after.NextCheckAt);
            Assert.Null(after.LeaseId);
            Assert.Empty(await HistoryAsync(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ExpiredOldWorkerCannotOverwriteNewWorkersCompletion()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        var release = Signal();
        using var old = Worker(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Healthy;
        });
        var oldRun = old.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await ChangeAsync(id, db => db.Entry(db.Monitors.Local.Single())
                .Property<DateTimeOffset?>("LeaseExpiresAt").CurrentValue = DateTimeOffset.UtcNow.AddSeconds(-1));
            using var current = Worker(_ => Task.FromResult(Healthy));
            Assert.Equal(1, await current.RunBatchAsync(TestContext.Current.CancellationToken));
            var afterCurrent = await ReadAsync(id);
            release.TrySetResult();
            Assert.Equal(1, await oldRun);
            Assert.Equal(afterCurrent.NextCheckAt, (await ReadAsync(id)).NextCheckAt);
            Assert.Single(await HistoryAsync(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationReleasesLease()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        using var cancellation = new CancellationTokenSource();
        using var worker = Worker(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Healthy;
        });
        var run = worker.RunBatchAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var state = await ReadAsync(id);
        Assert.Null(state.LeaseId);
        Assert.True(state.NextCheckAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task HostedServiceStopsAndCleansUpActiveLease()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        using var worker = Worker(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Healthy;
        });
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);
        Assert.Null((await ReadAsync(id)).LeaseId);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(44)]
    public void TooShortLeaseConfigurationIsRejected(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() => Worker(_ => Task.FromResult(Healthy),
            new MonitorWorkerOptions { Enabled = true, LeaseDurationSeconds = seconds }));
    }

    [Fact]
    public async Task MaxConcurrencyAndBatchSizeAreBounded()
    {
        for (var i = 0; i < 4; i++) await CreateMonitorAsync();
        var twoEntered = Signal();
        var release = Signal();
        var active = 0;
        var peak = 0;
        using var worker = Worker(async _ =>
        {
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            if (current == 2) twoEntered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
            return Healthy;
        }, new MonitorWorkerOptions { Enabled = true, BatchSize = 4, MaxConcurrentChecks = 2 });
        var run = worker.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, await LeasedCountAsync());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(2, await run);
        Assert.Equal(2, peak);
        Assert.Equal(2, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BatchSizeLimitsClaimCount()
    {
        for (var i = 0; i < 3; i++) await CreateMonitorAsync();
        var calls = 0;
        using var worker = Worker(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Healthy); },
            new MonitorWorkerOptions { Enabled = true, BatchSize = 2, MaxConcurrentChecks = 5 });
        Assert.Equal(2, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DeletionDuringCheckDoesNotResurrectMonitor()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        var release = Signal();
        using var worker = Worker(async _ => { entered.TrySetResult(); await release.Task; return Healthy; });
        var run = worker.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Database.AssertLockHeldAsync();
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            Assert.Equal(1, await db.Monitors.Where(m => m.Id == id)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken));
            release.TrySetResult();
            Assert.Equal(1, await run);
            Assert.False(await db.Monitors.AnyAsync(m => m.Id == id, TestContext.Current.CancellationToken));
            Assert.Empty(await HistoryAsync(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task DisablingDuringCheckPreservesDisabledState()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        var release = Signal();
        using var worker = Worker(async _ => { entered.TrySetResult(); await release.Task; return Healthy; });
        var run = worker.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await ChangeAsync(id, db =>
            {
                var m = db.Monitors.Local.Single();
                m.IsEnabled = false;
                m.NextCheckAt = null;
                db.Entry(m).Property<long>("ConfigVersion").CurrentValue++;
            });
            release.TrySetResult();
            Assert.Equal(1, await run);
            var state = await ReadAsync(id);
            Assert.False(state.IsEnabled);
            Assert.Null(state.NextCheckAt);
            Assert.Null(state.LeaseId);
            Assert.Empty(await HistoryAsync(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ConfigurationUpdateDuringCheckIsNotOverwritten()
    {
        var id = await CreateMonitorAsync();
        var entered = Signal();
        var release = Signal();
        using var worker = Worker(async _ => { entered.TrySetResult(); await release.Task; return Healthy; });
        var run = worker.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var future = DateTimeOffset.UtcNow.AddMinutes(2);
            await ChangeAsync(id, db =>
            {
                var m = db.Monitors.Local.Single();
                m.Url = "https://changed.example.invalid";
                m.TimeoutSeconds = 20;
                m.IntervalSeconds = 120;
                m.NextCheckAt = future;
                db.Entry(m).Property<long>("ConfigVersion").CurrentValue++;
            });
            var expected = await ReadAsync(id);
            release.TrySetResult();
            Assert.Equal(1, await run);
            var state = await ReadAsync(id);
            Assert.Equal("https://changed.example.invalid", state.Url);
            Assert.Equal(20, state.TimeoutSeconds);
            Assert.Equal(120, state.IntervalSeconds);
            Assert.Equal(expected.NextCheckAt, state.NextCheckAt);
            Assert.Null(state.LeaseId);
            Assert.Empty(await HistoryAsync(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ChecksMultipleOwnersWithoutChangingOwnership()
    {
        var firstOwner = await CreateUserAsync();
        var secondOwner = await CreateUserAsync();
        var first = await CreateMonitorAsync(ownerId: firstOwner);
        var second = await CreateMonitorAsync(ownerId: secondOwner);
        var seen = new List<Guid>();
        var sync = new object();
        using var worker = Worker(m =>
        {
            lock (sync) seen.Add(m.OwnerId);
            return Task.FromResult(Healthy);
        });
        Assert.Equal(2, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Contains(firstOwner, seen);
        Assert.Contains(secondOwner, seen);
        Assert.Equal(firstOwner, (await ReadAsync(first)).OwnerId);
        Assert.Equal(secondOwner, (await ReadAsync(second)).OwnerId);
    }

    private MonitorWorker Worker(Func<MonitorEntity, Task<HttpHealthCheckResult>> check,
        MonitorWorkerOptions? options = null) =>
        Worker((monitor, _) => check(monitor), options);

    private MonitorWorker Worker(Func<MonitorEntity, CancellationToken, Task<HttpHealthCheckResult>> check,
        MonitorWorkerOptions? options = null) =>
        new(Factory.Services.GetRequiredService<IServiceScopeFactory>(), new FakeChecker(check),
            Options.Create(options ?? new MonitorWorkerOptions { Enabled = true }),
            NullLogger<MonitorWorker>.Instance);

    private async Task<Guid> CreateUserAsync()
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
        var email = $"worker-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        var user = new SentinelUser { Id = Guid.NewGuid(), UserName = email,
            Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        return user.Id;
    }

    private async Task<Guid> CreateMonitorAsync(Guid? ownerId = null, bool enabled = true,
        DateTimeOffset? next = null)
    {
        var owner = ownerId ?? await CreateUserAsync();
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = new MonitorEntity
        {
            Id = Guid.NewGuid(), OwnerId = owner, Name = "Worker test",
            Slug = $"worker-{Guid.NewGuid():N}", Url = "https://example.invalid/check",
            IntervalSeconds = 60, TimeoutSeconds = 10, IsEnabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
            NextCheckAt = enabled ? next ?? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return monitor.Id;
    }

    private async Task ChangeAsync(Guid id, Action<SentinelDbContext> change)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        _ = await db.Monitors.SingleAsync(m => m.Id == id, TestContext.Current.CancellationToken);
        change(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<MonitorState> ReadAsync(Guid id)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var m = await db.Monitors.SingleAsync(m => m.Id == id, TestContext.Current.CancellationToken);
        return new(m.OwnerId, m.Url, m.IntervalSeconds, m.TimeoutSeconds, m.IsEnabled,
            m.NextCheckAt, db.Entry(m).Property<Guid?>("LeaseId").CurrentValue,
            db.Entry(m).Property<DateTimeOffset?>("LeaseExpiresAt").CurrentValue);
    }

    private async Task<int> LeasedCountAsync()
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        return await db.Monitors.CountAsync(m => EF.Property<Guid?>(m, "LeaseId") != null,
            TestContext.Current.CancellationToken);
    }

    private async Task<List<MonitorCheckResult>> HistoryAsync(Guid id)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        return await db.MonitorCheckResults.AsNoTracking().Where(x => x.MonitorId == id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record MonitorState(Guid OwnerId, string Url, int IntervalSeconds, int TimeoutSeconds,
        bool IsEnabled, DateTimeOffset? NextCheckAt, Guid? LeaseId, DateTimeOffset? LeaseExpiresAt);

    private sealed class FakeChecker(Func<MonitorEntity, CancellationToken, Task<HttpHealthCheckResult>> check)
        : IHttpHealthChecker
    {
        public Task<HttpHealthCheckResult> CheckAsync(MonitorEntity monitor, CancellationToken ct) => check(monitor, ct);
    }
}
