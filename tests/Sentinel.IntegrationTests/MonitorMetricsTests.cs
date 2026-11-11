using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Entities;
using Sentinel.Infrastructure.Identity;
using Sentinel.Infrastructure.Persistence;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class MonitorMetricsTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoChecksReturnsZeroCountsAndNullDerivedValues()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        using var response = await GetAsync(host.Client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 0, 0, 0,
            null, null, null, null);
    }

    [Fact]
    public async Task AllHealthyChecksReturnOneHundredPercentAndTheirLatency()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddMinutes(-2), "Healthy", 200, 10),
            (Now.AddMinutes(-1), "Healthy", 204, 20));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 2, 2, 0,
            100m, 15m, 10, 20);
    }

    [Fact]
    public async Task AllFailedChecksReturnZeroPercentAndNullLatency()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddMinutes(-2), "Timeout", null, 900),
            (Now.AddMinutes(-1), "HttpError", 503, 4));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 2, 0, 2,
            0m, null, null, null);
    }

    [Fact]
    public async Task MixedOutcomesCountAndRoundSuccessRate()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddMinutes(-3), "Healthy", 200, 12),
            (Now.AddMinutes(-2), "Timeout", null, 99),
            (Now.AddMinutes(-1), "DnsFailure", null, 88));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 3, 1, 2,
            33.33m, 12m, 12, 12);
    }

    [Fact]
    public async Task LatencyUsesOnlyHealthyChecksAndRoundsAverage()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddMinutes(-4), "Healthy", 200, 1),
            (Now.AddMinutes(-3), "HttpError", 500, 900),
            (Now.AddMinutes(-2), "Healthy", 204, 2),
            (Now.AddMinutes(-1), "Healthy", 200, 2));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 4, 3, 1,
            75m, 1.67m, 1, 2);
    }

    [Fact]
    public async Task WindowStartIsInclusive()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddHours(-24), "Healthy", 200, 7));
        using var response = await GetAsync(host.Client, id, "?period=24h");
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 1, 1, 0,
            100m, 7m, 7, 7);
    }

    [Fact]
    public async Task WindowEndIsExclusive()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddMilliseconds(-1), "Healthy", 200, 5),
            (Now, "Timeout", null, 90));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 1, 1, 0,
            100m, 5m, 5, 5);
    }

    [Fact]
    public async Task FutureChecksAreExcluded()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddMinutes(-1), "Timeout", null, 6),
            (Now.AddMilliseconds(1), "Healthy", 200, 1));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 1, 0, 1,
            0m, null, null, null);
    }

    [Fact]
    public async Task ExplicitTwentyFourHourPeriodHasExactUtcWindow()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        using var response = await GetAsync(host.Client, id, "?period=24h");
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 0, 0, 0,
            null, null, null, null);
    }

    [Fact]
    public async Task SevenDayPeriodHasExactUtcWindow()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddDays(-7), "Healthy", 200, 8),
            (Now.AddDays(-7).AddMilliseconds(-1), "Timeout", null, 9));
        using var response = await GetAsync(host.Client, id, "?period=7d");
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "7d", Now.AddDays(-7), 1, 1, 0,
            100m, 8m, 8, 8);
    }

    [Fact]
    public async Task ThirtyDayPeriodHasExactUtcWindow()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddDays(-30), "Healthy", 200, 3),
            (Now.AddDays(-30).AddMilliseconds(-1), "Timeout", null, 9));
        using var response = await GetAsync(host.Client, id, "?period=30d");
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "30d", Now.AddDays(-30), 1, 1, 0,
            100m, 3m, 3, 3);
    }

    [Theory]
    [InlineData("1h")]
    [InlineData("24H")]
    [InlineData("")]
    public async Task UnsupportedPeriodReturnsBadRequest(string period)
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        using var response = await GetAsync(host.Client, id, $"?period={period}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OmittedPeriodDefaultsToTwentyFourHours()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddHours(-23), "Healthy", 200, 13),
            (Now.AddHours(-25), "Timeout", null, 99));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 1, 1, 0,
            100m, 13m, 13, 13);
    }

    [Fact]
    public async Task AnonymousRequestReturnsUnauthorized()
    {
        using var host = await StartAsync(login: false);
        using var response = await GetAsync(host.Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BearerPatCannotAccessCookieOnlyMetrics()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        using var csrfResponse = await SendAsync(host.Client, new(HttpMethod.Get, "/api/auth/csrf"));
        using var csrfBody = await OkBodyAsync(csrfResponse);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/tokens/")
        {
            Content = JsonContent.Create(new { name = "metrics-test", expiresInDays = 1 }),
        };
        create.Headers.Add("X-SENTINEL-CSRF", csrfBody.RootElement.GetProperty("csrfToken").GetString());
        using var tokenResponse = await SendAsync(host.Client, create);
        using var tokenBody = await OkBodyAsync(tokenResponse);
        var token = tokenBody.RootElement.GetProperty("token").GetString()!;
        using var bearerClient = host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), HandleCookies = false,
            AllowAutoRedirect = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/monitors/{id}/metrics");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(bearerClient, request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OtherOwnerReceivesNotFoundEvenWhenChecksExist()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddMinutes(-1), "Healthy", 200, 11));
        using var otherClient = host.Factory.CreateClient(ClientOptions());
        _ = await LoginAsync(otherClient, host.Factory);
        using var response = await GetAsync(otherClient, id, "?period=unsupported");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownMonitorReturnsNotFound()
    {
        using var host = await StartAsync();
        using var response = await GetAsync(host.Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ChecksOutsideWindowAndFromAnotherMonitorAreExcluded()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        var otherId = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddHours(-24).AddMilliseconds(-1), "Healthy", 200, 1),
            (Now.AddHours(-1), "Timeout", null, 50));
        await AddResultsAsync(otherId, (Now.AddHours(-1), "Healthy", 200, 2));
        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 1, 0, 1,
            0m, null, null, null);
    }

    [Fact]
    public async Task MetricsDeriveFromPersistedResultsAndDoNotChangeStatusOrHistory()
    {
        using var host = await StartAsync();
        var id = await AddMonitorAsync(host.OwnerId);
        await AddResultsAsync(id, (Now.AddSeconds(-10), "Healthy", 200, 14),
            (Now.AddSeconds(-20), "HttpError", 503, 300));

        await Database.AssertLockHeldAsync();
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            Assert.Equal(2, await db.MonitorCheckResults.AsNoTracking()
                .CountAsync(x => x.MonitorId == id, TestContext.Current.CancellationToken));
        }

        using var statusBefore = await GetAsync(host.Client, id, endpoint: "status");
        using var historyBefore = await GetAsync(host.Client, id, endpoint: "checks");
        Assert.Equal(HttpStatusCode.OK, statusBefore.StatusCode);
        Assert.Equal(HttpStatusCode.OK, historyBefore.StatusCode);
        var statusJson = await statusBefore.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var historyJson = await historyBefore.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var response = await GetAsync(host.Client, id);
        using var body = await OkBodyAsync(response);
        AssertMetrics(body.RootElement, id, "24h", Now.AddHours(-24), 2, 1, 1,
            50m, 14m, 14, 14);

        using var statusAfter = await GetAsync(host.Client, id, endpoint: "status");
        using var historyAfter = await GetAsync(host.Client, id, endpoint: "checks");
        Assert.Equal(statusJson, await statusAfter.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(historyJson, await historyAfter.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<MetricsHost> StartAsync(bool login = true)
    {
        var fixedFactory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now))));
        try
        {
            var client = fixedFactory.CreateClient(ClientOptions());
            var ownerId = login ? await LoginAsync(client, fixedFactory) : Guid.Empty;
            return new MetricsHost(fixedFactory, client, ownerId);
        }
        catch
        {
            fixedFactory.Dispose();
            throw;
        }
    }

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false,
    };

    private async Task<Guid> LoginAsync(HttpClient client, WebApplicationFactory<Program> factory)
    {
        await Database.AssertLockHeldAsync();
        var email = $"metrics-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        var user = new SentinelUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
        }
        using var csrfResponse = await SendAsync(client, new(HttpMethod.Get, "/api/auth/csrf"));
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        using var csrfBody = await ReadAsync(csrfResponse);
        var csrf = csrfBody.RootElement.GetProperty("csrfToken").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendAsync(client, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return user.Id;
    }

    private async Task<Guid> AddMonitorAsync(Guid ownerId)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = new MonitorEntity
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Name = "Metrics test",
            Slug = $"metrics-{Guid.NewGuid():N}", Url = "https://example.invalid/check",
            IntervalSeconds = 60, TimeoutSeconds = 10, IsEnabled = true,
            CreatedAt = Now.AddDays(-40), NextCheckAt = Now.AddDays(1),
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return monitor.Id;
    }

    private async Task AddResultsAsync(Guid id, params (DateTimeOffset At, string Outcome,
        int? Status, int Duration)[] results)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        foreach (var result in results)
            db.MonitorCheckResults.Add(new MonitorCheckResult
            {
                Id = Guid.NewGuid(), MonitorId = id, CheckedAt = result.At,
                Outcome = result.Outcome, HttpStatusCode = result.Status,
                DurationMilliseconds = result.Duration,
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> GetAsync(HttpClient client, Guid id, string suffix = "",
        string endpoint = "metrics") => SendAsync(client,
            new(HttpMethod.Get, $"/api/monitors/{id}/{endpoint}{suffix}"));

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            await Database.AssertLockHeldAsync();
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            try { await Database.AssertLockHeldAsync(); return response; }
            catch { response.Dispose(); throw; }
        }
    }

    private static async Task<JsonDocument> OkBodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static void AssertMetrics(JsonElement metrics, Guid id, string period,
        DateTimeOffset start, long total, long successful, long failed,
        decimal? rate, decimal? average, int? min, int? max)
    {
        var fields = metrics.EnumerateObject().Select(x => x.Name).ToHashSet();
        Assert.True(fields.SetEquals(new[]
        {
            "monitorId", "period", "windowStartUtc", "windowEndUtc", "totalChecks",
            "successfulChecks", "failedChecks", "successRatePercent",
            "averageResponseTimeMs", "minResponseTimeMs", "maxResponseTimeMs",
        }));
        Assert.Equal(id, metrics.GetProperty("monitorId").GetGuid());
        Assert.Equal(period, metrics.GetProperty("period").GetString());
        Assert.Equal(start, metrics.GetProperty("windowStartUtc").GetDateTimeOffset());
        Assert.Equal(Now, metrics.GetProperty("windowEndUtc").GetDateTimeOffset());
        Assert.Equal(total, Number(metrics, "totalChecks").GetInt64());
        Assert.Equal(successful, Number(metrics, "successfulChecks").GetInt64());
        Assert.Equal(failed, Number(metrics, "failedChecks").GetInt64());
        AssertDecimal(metrics, "successRatePercent", rate);
        AssertDecimal(metrics, "averageResponseTimeMs", average);
        AssertInt(metrics, "minResponseTimeMs", min);
        AssertInt(metrics, "maxResponseTimeMs", max);
    }

    private static JsonElement Number(JsonElement obj, string name)
    {
        var value = obj.GetProperty(name);
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        return value;
    }

    private static void AssertDecimal(JsonElement obj, string name, decimal? expected)
    {
        var value = obj.GetProperty(name);
        if (expected is null) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected.Value, Number(obj, name).GetDecimal());
    }

    private static void AssertInt(JsonElement obj, string name, int? expected)
    {
        var value = obj.GetProperty(name);
        if (expected is null) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected.Value, Number(obj, name).GetInt32());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MetricsHost(WebApplicationFactory<Program> factory, HttpClient client,
        Guid ownerId) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public HttpClient Client { get; } = client;
        public Guid OwnerId { get; } = ownerId;

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}
