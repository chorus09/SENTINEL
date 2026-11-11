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

public sealed class MonitorStatusTests : IntegrationTestBase
{
    [Fact]
    public async Task NoObservationReturnsUnknownWithNullResultFields()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner, "Never checked");

        using var response = await GetStatusAsync(Client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        var status = body.RootElement;
        AssertFields(status);
        Assert.Equal(id, status.GetProperty("monitorId").GetGuid());
        Assert.Equal("Never checked", status.GetProperty("name").GetString());
        Assert.True(status.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("UNKNOWN", status.GetProperty("status").GetString());
        Assert.Null(status.GetProperty("lastCheckedAt").GetString());
        Assert.Null(status.GetProperty("lastOutcome").GetString());
        Assert.Null(status.GetProperty("lastHttpStatusCode").GetString());
        Assert.Null(status.GetProperty("lastResponseTimeMs").GetString());
    }

    [Fact]
    public async Task FreshHealthyResultReturnsUp()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner, "Healthy check");
        var checkedAt = UtcNowMilliseconds().AddSeconds(-10);
        await AddResultAsync(id, checkedAt, "Healthy", 204, 6);

        using var response = await GetStatusAsync(Client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        var status = body.RootElement;
        Assert.Equal("UP", status.GetProperty("status").GetString());
        Assert.Equal(checkedAt, status.GetProperty("lastCheckedAt").GetDateTimeOffset());
        Assert.Equal("Healthy", status.GetProperty("lastOutcome").GetString());
        Assert.Equal(204, status.GetProperty("lastHttpStatusCode").GetInt32());
        Assert.Equal(6, status.GetProperty("lastResponseTimeMs").GetInt32());
    }

    [Fact]
    public async Task LatestResultWinsByCheckedAtThenIdAndReturnsItsFields()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner, "Tie order");
        var now = UtcNowMilliseconds();
        var tieAt = now.AddSeconds(-10);
        var lower = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higher = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await AddResultAsync(id, now.AddSeconds(-20), "Timeout", null, 4);
        await AddResultAsync(id, tieAt, "Healthy", 200, 15, lower);
        await AddResultAsync(id, tieAt, "HttpError", 503, 27, higher);

        using var response = await GetStatusAsync(Client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        var status = body.RootElement;
        AssertFields(status);
        Assert.Equal(id, status.GetProperty("monitorId").GetGuid());
        Assert.Equal("Tie order", status.GetProperty("name").GetString());
        Assert.True(status.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("DOWN", status.GetProperty("status").GetString());
        Assert.Equal(tieAt, status.GetProperty("lastCheckedAt").GetDateTimeOffset());
        Assert.Equal("HttpError", status.GetProperty("lastOutcome").GetString());
        Assert.Equal(503, status.GetProperty("lastHttpStatusCode").GetInt32());
        Assert.Equal(27, status.GetProperty("lastResponseTimeMs").GetInt32());
    }

    [Fact]
    public async Task DisabledMonitorIsUnknownButRetainsHistoricalObservation()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner, "Paused", enabled: false);
        var checkedAt = UtcNowMilliseconds().AddSeconds(-20);
        await AddResultAsync(id, checkedAt, "Healthy", 200, 11);

        using var response = await GetStatusAsync(Client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        var status = body.RootElement;
        Assert.False(status.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("UNKNOWN", status.GetProperty("status").GetString());
        Assert.Equal(checkedAt, status.GetProperty("lastCheckedAt").GetDateTimeOffset());
        Assert.Equal("Healthy", status.GetProperty("lastOutcome").GetString());
        Assert.Equal(200, status.GetProperty("lastHttpStatusCode").GetInt32());
        Assert.Equal(11, status.GetProperty("lastResponseTimeMs").GetInt32());
    }

    [Fact]
    public async Task StaleResultIsUnknownButRemainsVisibleAndHistoryIsUnchanged()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner, "Old check");
        var checkedAt = UtcNowMilliseconds().AddMinutes(-6);
        var resultId = await AddResultAsync(id, checkedAt, "Healthy", 200, 9);

        using var statusResponse = await GetStatusAsync(Client, id);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        using var statusBody = await ReadAsync(statusResponse);
        Assert.Equal("UNKNOWN", statusBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(checkedAt, statusBody.RootElement.GetProperty("lastCheckedAt").GetDateTimeOffset());
        Assert.Equal("Healthy", statusBody.RootElement.GetProperty("lastOutcome").GetString());

        using var historyResponse = await SendAsync(Client,
            new(HttpMethod.Get, $"/api/monitors/{id}/checks"));
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var historyBody = await ReadAsync(historyResponse);
        var item = Assert.Single(historyBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(resultId, item.GetProperty("id").GetGuid());
        Assert.Equal(checkedAt, item.GetProperty("checkedAt").GetDateTimeOffset());
        Assert.Equal("Healthy", item.GetProperty("outcome").GetString());
        Assert.Equal(200, item.GetProperty("httpStatusCode").GetInt32());
        Assert.Equal(9, item.GetProperty("durationMilliseconds").GetInt32());
    }

    [Fact]
    public async Task InjectedUtcClockDeterminesExactStalenessBoundaries()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var fixedFactory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(now))));
        using var fixedClient = fixedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
        var owner = await LoginAsync(fixedClient);
        var floorBoundary = await AddMonitorAsync(owner, "Five minute boundary");
        var floorStale = await AddMonitorAsync(owner, "Past five minutes");
        var intervalBoundary = await AddMonitorAsync(owner, "Three interval boundary", intervalSeconds: 120);
        var intervalStale = await AddMonitorAsync(owner, "Past three intervals", intervalSeconds: 120);
        await AddResultAsync(floorBoundary, now.AddSeconds(-300), "Healthy", 200, 1);
        await AddResultAsync(floorStale, now.AddMilliseconds(-300_001), "Healthy", 200, 1);
        await AddResultAsync(intervalBoundary, now.AddSeconds(-360), "HttpError", 500, 2);
        await AddResultAsync(intervalStale, now.AddMilliseconds(-360_001), "HttpError", 500, 2);

        await AssertStatusAsync(fixedClient, floorBoundary, "UP");
        await AssertStatusAsync(fixedClient, floorStale, "UNKNOWN");
        await AssertStatusAsync(fixedClient, intervalBoundary, "DOWN");
        await AssertStatusAsync(fixedClient, intervalStale, "UNKNOWN");
    }

    [Fact]
    public async Task OwnershipIsCheckedBeforeReadingStatus()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner, "Private");
        await AddResultAsync(id, DateTimeOffset.UtcNow, "Healthy", 200, 3);
        using var otherClient = CreateCookieClient();
        await LoginAsync(otherClient);

        using var other = await GetStatusAsync(otherClient, id);
        using var missing = await GetStatusAsync(Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task StatusRequiresCookieAuthentication()
    {
        using var anonymous = await GetStatusAsync(Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await LoginAsync(Client);
        var token = await CreatePatAsync(Client);
        using var bearerClient = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/monitors/{Guid.NewGuid()}/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var bearer = await SendAsync(bearerClient, request);
        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
    }

    private async Task<Guid> LoginAsync(HttpClient client)
    {
        await Database.AssertLockHeldAsync();
        var email = $"status-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        var user = new SentinelUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true,
        };
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
        }
        var csrf = await CsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendAsync(client, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return user.Id;
    }

    private async Task<string> CreatePatAsync(HttpClient client)
    {
        var csrf = await CsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tokens/")
        {
            Content = JsonContent.Create(new { name = "status-test", expiresInDays = 1 }),
        };
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendAsync(client, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<Guid> AddMonitorAsync(Guid ownerId, string name, bool enabled = true,
        int intervalSeconds = 60)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = new MonitorEntity
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Name = name,
            Slug = $"status-{Guid.NewGuid():N}", Url = "https://example.invalid/check",
            IntervalSeconds = intervalSeconds, TimeoutSeconds = 10, IsEnabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
            NextCheckAt = enabled ? DateTimeOffset.UtcNow.AddHours(1) : null,
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return monitor.Id;
    }

    private async Task<Guid> AddResultAsync(Guid monitorId, DateTimeOffset checkedAt,
        string outcome, int? statusCode, int durationMs, Guid? resultId = null)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var result = new MonitorCheckResult
        {
            Id = resultId ?? Guid.NewGuid(), MonitorId = monitorId, CheckedAt = checkedAt,
            Outcome = outcome, HttpStatusCode = statusCode, DurationMilliseconds = durationMs,
        };
        db.MonitorCheckResults.Add(result);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return result.Id;
    }

    private async Task<string> CsrfAsync(HttpClient client)
    {
        using var response = await SendAsync(client, new(HttpMethod.Get, "/api/auth/csrf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        return body.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private Task<HttpResponseMessage> GetStatusAsync(HttpClient client, Guid id) =>
        SendAsync(client, new(HttpMethod.Get, $"/api/monitors/{id}/status"));

    private static DateTimeOffset UtcNowMilliseconds() =>
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private async Task AssertStatusAsync(HttpClient client, Guid id, string expected)
    {
        using var response = await GetStatusAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        Assert.Equal(expected, body.RootElement.GetProperty("status").GetString());
    }

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

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static void AssertFields(JsonElement status)
    {
        var fields = status.EnumerateObject().Select(x => x.Name).ToHashSet();
        Assert.True(fields.SetEquals(new[]
        {
            "monitorId", "name", "isEnabled", "status", "lastCheckedAt", "lastOutcome",
            "lastHttpStatusCode", "lastResponseTimeMs",
        }));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
