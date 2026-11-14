using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public sealed class IncidentLifecycleTests : IntegrationTestBase
{
    private static readonly HttpHealthCheckResult Success =
        new(HttpHealthCheckOutcome.Healthy, 200, TimeSpan.FromMilliseconds(4));
    private static readonly HttpHealthCheckResult Failure =
        new(HttpHealthCheckOutcome.HttpError, 503, TimeSpan.FromMilliseconds(7));

    [Fact]
    public async Task FailureAndRecoveryStreaksOpenResolveAndReopenExactlyOnce()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        Assert.Equal((1, 0, 1), StreakAndChecks(await StateAsync(id)));
        await RunAsync(id, Failure);
        Assert.Equal((2, 0, 2), StreakAndChecks(await StateAsync(id)));
        await RunAsync(id, Success); // Pre-incident success resets failures.
        Assert.Equal((0, 1, 3), StreakAndChecks(await StateAsync(id)));
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        Assert.Empty(await IncidentsAsync(id));
        await RunAsync(id, Failure);
        var first = Assert.Single(await IncidentsAsync(id));
        Assert.Null(first.ResolvedAt);
        Assert.Equal(3, (await StateAsync(id)).Failures);
        await RunAsync(id, Failure);
        Assert.Single(await IncidentsAsync(id));
        await RunAsync(id, Success);
        Assert.Null(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
        await RunAsync(id, Failure); // Interrupts recovery.
        Assert.Equal(0, (await StateAsync(id)).Successes);
        await RunAsync(id, Success);
        Assert.Null(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
        await RunAsync(id, Success);
        Assert.NotNull(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        Assert.Single(await IncidentsAsync(id));
        await RunAsync(id, Failure);
        var incidents = await IncidentsAsync(id);
        Assert.Equal(2, incidents.Count);
        Assert.Single(incidents, x => x.ResolvedAt is null);
    }

    [Fact]
    public async Task RestartedWorkersContinuePersistedFailureStreak()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        Assert.Equal(2, (await StateAsync(id)).Failures);
        await RunAsync(id, Failure); // RunAsync constructs a fresh worker and scope each time.
        Assert.Single(await IncidentsAsync(id));
    }

    [Fact]
    public async Task MixedNonHealthyOutcomesShareOneFailureStreak()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        await RunAsync(id, new(HttpHealthCheckOutcome.Timeout, null, TimeSpan.FromMilliseconds(8)));
        Assert.Equal((2, 0, 2), StreakAndChecks(await StateAsync(id)));
        Assert.Empty(await IncidentsAsync(id));
        await RunAsync(id, new(HttpHealthCheckOutcome.DnsFailure, null, TimeSpan.FromMilliseconds(9)));
        Assert.Equal(3, (await StateAsync(id)).Failures);
        Assert.Null(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
    }

    [Fact]
    public async Task RestartedWorkersContinuePersistedRecoveryStreak()
    {
        var id = await OpenAsync();
        await RunAsync(id, Success);
        Assert.Equal(1, (await StateAsync(id)).Successes);
        Assert.Null(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
        await RunAsync(id, Success); // Each RunAsync creates a new worker and scope.
        Assert.Equal(2, (await StateAsync(id)).Successes);
        Assert.NotNull(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
    }

    [Fact]
    public async Task ConcurrentWorkersCannotOpenDuplicateIncident()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        await MakeDueAsync(id);
        var entered = Signal();
        var release = Signal();
        using var first = Worker(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Failure;
        });
        using var second = Worker(_ => Task.FromResult(Failure));
        var firstRun = first.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, await second.RunBatchAsync(TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await firstRun);
        Assert.Single(await IncidentsAsync(id));
        Assert.Equal(3, (await StateAsync(id)).Failures);
    }

    [Fact]
    public async Task ExpiredLeaseCannotAdvanceCountersOrOpenIncident()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        await MakeDueAsync(id);
        var before = await StateAsync(id);
        var entered = Signal();
        var release = Signal();
        using var worker = Worker(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Failure;
        });
        var run = worker.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await MutateAsync(id, (db, monitor) => db.Entry(monitor)
                .Property<DateTimeOffset?>("LeaseExpiresAt").CurrentValue = DateTimeOffset.UtcNow.AddSeconds(-1));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await run);
        var after = await StateAsync(id);
        Assert.Equal(before.Failures, after.Failures);
        Assert.Equal(before.Checks, after.Checks);
        Assert.Empty(await IncidentsAsync(id));
    }

    [Fact]
    public async Task StaleConfigurationCannotAdvanceCountersOrOpenIncident()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        await MakeDueAsync(id);
        var entered = Signal();
        var release = Signal();
        using var worker = Worker(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return Failure;
        });
        var run = worker.RunBatchAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await MutateAsync(id, (db, monitor) =>
            {
                monitor.ConsecutiveUnhealthyCount = 0;
                monitor.ConsecutiveHealthyCount = 0;
                db.Entry(monitor).Property<long>("ConfigVersion").CurrentValue++;
            });
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await run);
        var after = await StateAsync(id);
        Assert.Equal(0, after.Failures);
        Assert.Equal(2, after.Checks);
        Assert.Empty(await IncidentsAsync(id));
    }

    [Fact]
    public async Task FailedResultInsertRollsBackCountersScheduleAndIncident()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        await MakeDueAsync(id);
        var before = await StateAsync(id);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE monitor_check_results ADD CONSTRAINT CK_incident_test_reject CHECK (FALSE) NOT VALID",
                TestContext.Current.CancellationToken);
        }
        using var worker = Worker(_ => Task.FromResult(Failure));
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        var after = await StateAsync(id);
        Assert.Equal(before.Failures, after.Failures);
        Assert.Equal(before.Checks, after.Checks);
        Assert.Equal(before.Next, after.Next);
        Assert.NotNull(after.Lease);
        Assert.Empty(await IncidentsAsync(id));
    }

    [Fact]
    public async Task FailedSecondHealthyInsertRollsBackResolutionCountersAndSchedule()
    {
        var id = await OpenAsync();
        await RunAsync(id, Success);
        await MakeDueAsync(id);
        var before = await StateAsync(id);
        var open = Assert.Single(await IncidentsAsync(id));
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE monitor_check_results ADD CONSTRAINT CK_incident_resolution_reject CHECK (FALSE) NOT VALID",
                TestContext.Current.CancellationToken);
        }
        using var worker = Worker(_ => Task.FromResult(Success));
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        var after = await StateAsync(id);
        Assert.Equal(StreakAndChecks(before), StreakAndChecks(after));
        Assert.Equal(before.Next, after.Next);
        Assert.NotNull(after.Lease);
        var incident = Assert.Single(await IncidentsAsync(id));
        Assert.Equal(open.Id, incident.Id);
        Assert.Null(incident.ResolvedAt);
    }

    [Fact]
    public async Task ReenabledMonitorNeedsTwoNewHealthyChecksToResolveOpenIncident()
    {
        var id = await CreateMonitorAsync(login: true);
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        await RunAsync(id, Success);
        Assert.Equal(1, (await StateAsync(id)).Successes);

        await UpdateEnabledAsync(id, false);
        await UpdateEnabledAsync(id, true);
        var reset = await StateAsync(id);
        Assert.Equal((0, 0), (reset.Failures, reset.Successes));
        Assert.Null(Assert.Single(await IncidentsAsync(id)).ResolvedAt);

        await RunAsync(id, Success);
        Assert.Equal(1, (await StateAsync(id)).Successes);
        Assert.Null(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
        await RunAsync(id, Success);
        Assert.NotNull(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
    }

    [Fact]
    public async Task FutureOpenedAtClampsResolutionAfterTwoHealthyChecks()
    {
        var id = await OpenAsync();
        var futureOpenedAt = DateTimeOffset.UtcNow.AddDays(1);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            var incident = await db.MonitorIncidents.SingleAsync(x => x.MonitorId == id,
                TestContext.Current.CancellationToken);
            incident.OpenedAt = futureOpenedAt;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await RunAsync(id, Success);
        await RunAsync(id, Success);
        var resolved = Assert.Single(await IncidentsAsync(id));
        Assert.Equal(resolved.OpenedAt, resolved.ResolvedAt);
        using var verificationScope = Factory.Services.CreateScope();
        var dbAfter = verificationScope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var latest = await dbAfter.MonitorCheckResults.Where(x => x.MonitorId == id)
            .OrderByDescending(x => x.CheckedAt).FirstAsync(TestContext.Current.CancellationToken);
        Assert.True(latest.CheckedAt < resolved.OpenedAt);
    }

    [Fact]
    public async Task DisabledMonitorKeepsOpenIncidentWithoutTransition()
    {
        var id = await OpenAsync();
        await MutateAsync(id, (db, monitor) =>
        {
            monitor.IsEnabled = false;
            monitor.NextCheckAt = null;
            monitor.ConsecutiveUnhealthyCount = 0;
            monitor.ConsecutiveHealthyCount = 0;
            db.Entry(monitor).Property<long>("ConfigVersion").CurrentValue++;
        });
        using var worker = Worker(_ => Task.FromResult(Success));
        Assert.Equal(0, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Null(Assert.Single(await IncidentsAsync(id)).ResolvedAt);
    }

    [Fact]
    public async Task DeletingMonitorCascadesToItsIncidents()
    {
        var id = await OpenAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        Assert.Equal(1, await db.Monitors.Where(x => x.Id == id)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await IncidentsAsync(id));
    }

    [Fact]
    public async Task DatabaseRejectsSecondOpenIncidentForSameMonitor()
    {
        var id = await OpenAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        db.MonitorIncidents.Add(new MonitorIncident
        {
            MonitorId = id, OpenedAt = DateTimeOffset.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Single(await IncidentsAsync(id));
    }

    private async Task<Guid> OpenAsync()
    {
        var id = await CreateMonitorAsync();
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        await RunAsync(id, Failure);
        return id;
    }

    private async Task RunAsync(Guid id, HttpHealthCheckResult result)
    {
        await MakeDueAsync(id);
        using var worker = Worker(_ => Task.FromResult(result));
        Assert.Equal(1, await worker.RunBatchAsync(TestContext.Current.CancellationToken));
    }

    private MonitorWorker Worker(Func<MonitorEntity, Task<HttpHealthCheckResult>> check) =>
        new(Factory.Services.GetRequiredService<IServiceScopeFactory>(), new FakeChecker(check),
            Options.Create(new MonitorWorkerOptions { Enabled = true }), NullLogger<MonitorWorker>.Instance);

    private async Task<Guid> CreateMonitorAsync(bool login = false)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
        var email = $"incident-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        var user = new SentinelUser { Id = Guid.NewGuid(), UserName = email,
            Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = new MonitorEntity
        {
            Id = Guid.NewGuid(), OwnerId = user.Id, Name = "Incident test",
            Slug = $"incident-{Guid.NewGuid():N}", Url = "https://example.invalid/check",
            IntervalSeconds = 60, TimeoutSeconds = 10, IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (login)
        {
            using var csrfResponse = await Client.GetAsync("/api/auth/csrf", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
            using var csrfBody = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
            var csrf = csrfBody.RootElement.GetProperty("csrfToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            { Content = JsonContent.Create(new { email, password }) };
            request.Headers.Add("X-SENTINEL-CSRF", csrf);
            using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        return monitor.Id;
    }

    private async Task UpdateEnabledAsync(Guid id, bool enabled)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = await db.Monitors.AsNoTracking().SingleAsync(x => x.Id == id,
            TestContext.Current.CancellationToken);
        using var csrfResponse = await Client.GetAsync("/api/auth/csrf", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        using var csrfBody = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        var csrf = csrfBody.RootElement.GetProperty("csrfToken").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/monitors/{id}")
        {
            Content = JsonContent.Create(new
            {
                monitor.Name, monitor.Slug, monitor.Url, monitor.IntervalSeconds,
                monitor.TimeoutSeconds, IsEnabled = enabled,
            }),
        };
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task MakeDueAsync(Guid id) => MutateAsync(id, (_, monitor) =>
        monitor.NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(-1));

    private async Task MutateAsync(Guid id, Action<SentinelDbContext, MonitorEntity> mutate)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = await db.Monitors.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        mutate(db, monitor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(int Failures, int Successes, int Checks, DateTimeOffset? Next, Guid? Lease)> StateAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = await db.Monitors.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        return (monitor.ConsecutiveUnhealthyCount, monitor.ConsecutiveHealthyCount,
            await db.MonitorCheckResults.CountAsync(x => x.MonitorId == id, TestContext.Current.CancellationToken),
            monitor.NextCheckAt, db.Entry(monitor).Property<Guid?>("LeaseId").CurrentValue);
    }

    private async Task<List<MonitorIncident>> IncidentsAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        return await db.MonitorIncidents.AsNoTracking().Where(x => x.MonitorId == id)
            .OrderBy(x => x.OpenedAt).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static (int Failures, int Successes, int Checks) StreakAndChecks(
        (int Failures, int Successes, int Checks, DateTimeOffset? Next, Guid? Lease) state) =>
        (state.Failures, state.Successes, state.Checks);

    private sealed class FakeChecker(Func<MonitorEntity, Task<HttpHealthCheckResult>> check) : IHttpHealthChecker
    {
        public Task<HttpHealthCheckResult> CheckAsync(MonitorEntity monitor, CancellationToken _) => check(monitor);
    }
}
