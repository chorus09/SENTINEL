using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Entities;
using Sentinel.Infrastructure.Identity;
using Sentinel.Infrastructure.Persistence;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class MonitorIncidentsApiTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Opened = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnonymousAndForeignMonitorsAreNotDisclosed()
    {
        using var anonymous = await GetAsync(Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner);
        await AddIncidentAsync(id, Opened, null);
        using var otherClient = CreateCookieClient();
        await LoginAsync(otherClient);
        using var foreign = await GetAsync(otherClient, id, "?pageSize=bad");
        using var missing = await GetAsync(Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task IncidentsReturnSafeStateAndDeterministicDescendingPages()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner);
        var older = await AddIncidentAsync(id, Opened.AddHours(-1), Opened.AddMinutes(-50));
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await AddIncidentAsync(id, Opened, Opened.AddMinutes(1), lowerId);
        await AddIncidentAsync(id, Opened, null, higherId);

        using var first = await GetAsync(Client, id, "?pageSize=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstBody = await ReadAsync(first);
        var firstItems = firstBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, firstItems.Length);
        AssertIncident(firstItems[0], higherId, id, "OPEN", Opened, null);
        AssertIncident(firstItems[1], lowerId, id, "RESOLVED", Opened, Opened.AddMinutes(1));
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        using var second = await GetAsync(Client, id, $"?pageSize=2&cursor={cursor}");
        using var secondBody = await ReadAsync(second);
        var item = Assert.Single(secondBody.RootElement.GetProperty("items").EnumerateArray());
        AssertIncident(item, older, id, "RESOLVED", Opened.AddHours(-1), Opened.AddMinutes(-50));
        Assert.Equal(JsonValueKind.Null, secondBody.RootElement.GetProperty("nextCursor").ValueKind);

        var another = await AddMonitorAsync(owner);
        using var wrongMonitor = await GetAsync(Client, another, $"?cursor={cursor}");
        Assert.Equal(HttpStatusCode.BadRequest, wrongMonitor.StatusCode);
    }

    [Fact]
    public async Task PageSizeDefaultsToTwentyAndIsBoundedToOneHundred()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            for (var i = 0; i < 101; i++)
                db.MonitorIncidents.Add(new MonitorIncident
                {
                    Id = Guid.NewGuid(), MonitorId = id,
                    OpenedAt = Opened.AddMinutes(-i), ResolvedAt = Opened.AddMinutes(-i).AddSeconds(1),
                });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var standard = await GetAsync(Client, id);
        using var standardBody = await ReadAsync(standard);
        Assert.Equal(20, standardBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.NotNull(standardBody.RootElement.GetProperty("nextCursor").GetString());
        using var maximum = await GetAsync(Client, id, "?pageSize=100");
        using var maximumBody = await ReadAsync(maximum);
        Assert.Equal(100, maximumBody.RootElement.GetProperty("items").GetArrayLength());
        using var zero = await GetAsync(Client, id, "?pageSize=0");
        using var tooLarge = await GetAsync(Client, id, "?pageSize=101");
        using var badCursor = await GetAsync(Client, id, "?cursor=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
    }

    [Fact]
    public async Task ConfigurationUpdatesResetStreaksWithoutResolvingOpenIncident()
    {
        var owner = await LoginAsync(Client);
        var id = await AddMonitorAsync(owner);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            var monitor = await db.Monitors.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
            monitor.ConsecutiveUnhealthyCount = 2;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var changed = await UpdateAsync(id, enabled: true, name: "Renamed");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal((0, 0), await CountersAsync(id));

        await AddIncidentAsync(id, Opened, null);
        using var disabled = await UpdateAsync(id, enabled: false);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal((0, 0), await CountersAsync(id));
        using var enabled = await UpdateAsync(id, enabled: true);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal((0, 0), await CountersAsync(id));
        using var incidents = await GetAsync(Client, id);
        using var body = await ReadAsync(incidents);
        var open = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("OPEN", open.GetProperty("state").GetString());
    }

    private async Task<Guid> LoginAsync(HttpClient client)
    {
        var email = $"incidents-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        var user = new SentinelUser { Id = Guid.NewGuid(), UserName = email,
            Email = email, EmailConfirmed = true };
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
        }
        var csrf = await CsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        { Content = JsonContent.Create(new { email, password }) };
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendAsync(client, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return user.Id;
    }

    private async Task<Guid> AddMonitorAsync(Guid owner)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = new MonitorEntity
        {
            Id = Guid.NewGuid(), OwnerId = owner, Name = "Incident API test",
            Slug = $"incident-api-{Guid.NewGuid():N}", Url = "https://example.invalid/check",
            IntervalSeconds = 60, TimeoutSeconds = 10, IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return monitor.Id;
    }

    private async Task<Guid> AddIncidentAsync(Guid monitorId, DateTimeOffset opened,
        DateTimeOffset? resolved, Guid? incidentId = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var incident = new MonitorIncident
        {
            Id = incidentId ?? Guid.NewGuid(), MonitorId = monitorId, OpenedAt = opened,
            ResolvedAt = resolved,
        };
        db.MonitorIncidents.Add(incident);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return incident.Id;
    }

    private async Task<(int Failures, int Successes)> CountersAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var m = await db.Monitors.AsNoTracking().SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        return (m.ConsecutiveUnhealthyCount, m.ConsecutiveHealthyCount);
    }

    private async Task<HttpResponseMessage> UpdateAsync(Guid id, bool enabled, string name = "Incident API test")
    {
        var csrf = await CsrfAsync(Client);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/monitors/{id}")
        {
            Content = JsonContent.Create(new
            {
                name, slug = $"incident-api-{id:N}", url = "https://example.invalid/check",
                intervalSeconds = 60, timeoutSeconds = 10, isEnabled = enabled,
            }),
        };
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        return await SendAsync(Client, request);
    }

    private async Task<string> CsrfAsync(HttpClient client)
    {
        using var response = await SendAsync(client, new(HttpMethod.Get, "/api/auth/csrf"));
        using var body = await ReadAsync(response);
        return body.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private Task<HttpResponseMessage> GetAsync(HttpClient client, Guid id, string suffix = "") =>
        SendAsync(client, new(HttpMethod.Get, $"/api/monitors/{id}/incidents{suffix}"));

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

    private static void AssertIncident(JsonElement item, Guid id, Guid monitorId, string state,
        DateTimeOffset openedAt, DateTimeOffset? resolvedAt)
    {
        Assert.True(item.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(
            new[] { "id", "monitorId", "state", "openedAt", "resolvedAt" }));
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(monitorId, item.GetProperty("monitorId").GetGuid());
        Assert.Equal(state, item.GetProperty("state").GetString());
        Assert.Equal(openedAt, item.GetProperty("openedAt").GetDateTimeOffset());
        if (resolvedAt is null)
            Assert.Equal(JsonValueKind.Null, item.GetProperty("resolvedAt").ValueKind);
        else
            Assert.Equal(resolvedAt, item.GetProperty("resolvedAt").GetDateTimeOffset());
    }
}
