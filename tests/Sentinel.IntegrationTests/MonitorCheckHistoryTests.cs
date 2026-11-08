using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Entities;
using Sentinel.Infrastructure.Identity;
using Sentinel.Infrastructure.Persistence;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class MonitorCheckHistoryTests : IntegrationTestBase
{
    [Fact]
    public async Task AnonymousHistoryRequestReturnsUnauthorized()
    {
        using var response = await GetAsync(Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OwnerSeesEmptyHistoryAndUnknownMonitorIsNotFound()
    {
        var owner = await LoginAsync(Client);
        var monitor = await CreateMonitorAsync(owner);
        using var empty = await GetAsync(Client, monitor);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using var document = await ReadAsync(empty);
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);

        using var missing = await GetAsync(Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task OwnerSeesSafeResultFieldsButOtherUserGetsNotFound()
    {
        var owner = await LoginAsync(Client);
        var monitor = await CreateMonitorAsync(owner);
        var result = await AddResultAsync(monitor, DateTimeOffset.UtcNow, "DnsFailure", null, 18);
        using var own = await GetAsync(Client, monitor);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var document = await ReadAsync(own);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(result, item.GetProperty("id").GetGuid());
        Assert.Equal("DnsFailure", item.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("httpStatusCode").ValueKind);
        Assert.Equal(18, item.GetProperty("durationMilliseconds").GetInt32());
        Assert.True(item.GetProperty("checkedAt").GetDateTimeOffset() > DateTimeOffset.UnixEpoch);
        var fields = item.EnumerateObject().Select(x => x.Name).ToHashSet();
        Assert.True(fields.SetEquals(new[] { "id", "checkedAt", "outcome", "httpStatusCode", "durationMilliseconds" }));

        using var otherClient = CreateCookieClient();
        _ = await LoginAsync(otherClient);
        using var other = await GetAsync(otherClient, monitor);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("abc")]
    [InlineData("-1")]
    public async Task InvalidPageSizeReturnsBadRequest(string size)
    {
        var owner = await LoginAsync(Client);
        var monitor = await CreateMonitorAsync(owner);
        using var response = await GetAsync(Client, monitor, $"?pageSize={size}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidOrOtherMonitorCursorReturnsBadRequest()
    {
        var owner = await LoginAsync(Client);
        var first = await CreateMonitorAsync(owner);
        var second = await CreateMonitorAsync(owner);
        await AddResultAsync(first, DateTimeOffset.UtcNow.AddMinutes(-1), "Healthy", 200, 4);
        await AddResultAsync(first, DateTimeOffset.UtcNow, "Healthy", 200, 5);

        using var malformed = await GetAsync(Client, first, "?cursor=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        using var page = await GetAsync(Client, first, "?pageSize=1");
        using var body = await ReadAsync(page);
        var cursor = body.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        using var crossMonitor = await GetAsync(Client, second, "?cursor=" + cursor);
        Assert.Equal(HttpStatusCode.BadRequest, crossMonitor.StatusCode);
    }

    [Fact]
    public async Task ResultsAreNewestFirstWithDeterministicTieBreaker()
    {
        var owner = await LoginAsync(Client);
        var monitor = await CreateMonitorAsync(owner);
        var same = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var older = await AddResultAsync(monitor, same.AddSeconds(-1), "HttpError", 500, 12);
        var lower = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higher = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await AddResultAsync(monitor, same, "Healthy", 200, 10, lower);
        await AddResultAsync(monitor, same, "Healthy", 200, 11, higher);
        using var response = await GetAsync(Client, monitor);
        using var body = await ReadAsync(response);
        var ids = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(new[] { higher, lower, older }, ids);
    }

    [Fact]
    public async Task DefaultAndMaximumPageSizesAreBounded()
    {
        var owner = await LoginAsync(Client);
        var monitor = await CreateMonitorAsync(owner);
        await AddManyAsync(monitor, 105);
        using var defaultPage = await GetAsync(Client, monitor);
        using var defaultBody = await ReadAsync(defaultPage);
        Assert.Equal(20, defaultBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.String, defaultBody.RootElement.GetProperty("nextCursor").ValueKind);

        using var maxPage = await GetAsync(Client, monitor, "?pageSize=100");
        using var maxBody = await ReadAsync(maxPage);
        Assert.Equal(100, maxBody.RootElement.GetProperty("items").GetArrayLength());
        var cursor = maxBody.RootElement.GetProperty("nextCursor").GetString();
        using var lastPage = await GetAsync(Client, monitor, "?pageSize=100&cursor=" + cursor);
        using var lastBody = await ReadAsync(lastPage);
        Assert.Equal(5, lastBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, lastBody.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task CursorPagesDoNotRepeatResults()
    {
        var owner = await LoginAsync(Client);
        var monitor = await CreateMonitorAsync(owner);
        var same = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= 7; i++)
            await AddResultAsync(monitor, same, "Healthy", 200, i,
                Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}"));

        var seen = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var query = "?pageSize=3" + (cursor is null ? "" : "&cursor=" + cursor);
            using var response = await GetAsync(Client, monitor, query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadAsync(response);
            foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
                Assert.True(seen.Add(item.GetProperty("id").GetGuid()));
            cursor = body.RootElement.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(7, seen.Count);
    }

    [Fact]
    public async Task DeletingMonitorCascadesItsHistory()
    {
        var owner = await LoginAsync(Client);
        var monitor = await CreateMonitorAsync(owner);
        await AddResultAsync(monitor, DateTimeOffset.UtcNow, "Healthy", 200, 8);
        var csrf = await CsrfAsync(Client);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/monitors/{monitor}");
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var deleted = await SendAsync(Client, request);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        Assert.False(await db.MonitorCheckResults.AnyAsync(x => x.MonitorId == monitor,
            TestContext.Current.CancellationToken));
    }

    private async Task<Guid> LoginAsync(HttpClient client)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
        var email = $"history-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        var user = new SentinelUser { Id = Guid.NewGuid(), UserName = email,
            Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        var csrf = await CsrfAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        { Content = JsonContent.Create(new { email, password }) };
        login.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendAsync(client, login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return user.Id;
    }

    private async Task<Guid> CreateMonitorAsync(Guid ownerId)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var monitor = new MonitorEntity
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Name = "History test",
            Slug = $"history-{Guid.NewGuid():N}", Url = "https://example.invalid/check",
            IntervalSeconds = 60, TimeoutSeconds = 10, IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return monitor.Id;
    }

    private async Task<Guid> AddResultAsync(Guid monitorId, DateTimeOffset checkedAt,
        string outcome, int? status, int duration, Guid? resultId = null)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var item = new MonitorCheckResult
        {
            Id = resultId ?? Guid.NewGuid(), MonitorId = monitorId, CheckedAt = checkedAt,
            Outcome = outcome, HttpStatusCode = status, DurationMilliseconds = duration,
        };
        db.MonitorCheckResults.Add(item);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return item.Id;
    }

    private async Task AddManyAsync(Guid monitorId, int count)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var start = DateTimeOffset.UtcNow.AddMinutes(-count);
        for (var i = 0; i < count; i++)
            db.MonitorCheckResults.Add(new MonitorCheckResult
            {
                Id = Guid.NewGuid(), MonitorId = monitorId, CheckedAt = start.AddSeconds(i),
                Outcome = "Healthy", HttpStatusCode = 200, DurationMilliseconds = i,
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> CsrfAsync(HttpClient client)
    {
        using var response = await SendAsync(client, new(HttpMethod.Get, "/api/auth/csrf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadAsync(response);
        return document.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private Task<HttpResponseMessage> GetAsync(HttpClient client, Guid id, string suffix = "") =>
        SendAsync(client, new(HttpMethod.Get, $"/api/monitors/{id}/checks{suffix}"));

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
}
