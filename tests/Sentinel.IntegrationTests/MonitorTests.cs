using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Infrastructure.Identity;
using Sentinel.Infrastructure.Persistence;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class MonitorTests : IntegrationTestBase
{
    private const string Path = "/api/monitors/";

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task AnonymousRequestsAreRejected(string method)
    {
        using var response = await SendAsync(Client, new HttpRequestMessage(new HttpMethod(method),
            method is "PUT" or "DELETE" ? Path + Guid.NewGuid() : Path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateReturnsServerFieldsAndNormalizesInput()
    {
        var ownerId = await LoginAsync(Client);
        var before = DateTimeOffset.UtcNow;
        using var response = await MutateAsync(Client, HttpMethod.Post, Path,
            Valid(name: "  Example  ", slug: "  EXAMPLE  "));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await ReadAsync(response);
        var item = body.RootElement;
        var id = item.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(ownerId, item.GetProperty("ownerId").GetGuid());
        Assert.Equal("Example", item.GetProperty("name").GetString());
        Assert.Equal("example", item.GetProperty("slug").GetString());
        Assert.Equal(Path + id, response.Headers.Location?.OriginalString);
        var created = item.GetProperty("createdAt").GetDateTimeOffset();
        var next = item.GetProperty("nextCheckAt").GetDateTimeOffset();
        Assert.True(created >= before && created <= DateTimeOffset.UtcNow);
        Assert.Equal(created.AddSeconds(60), next);
    }

    [Fact]
    public async Task ListAndReadReturnOnlyOwnedMonitors()
    {
        var alice = await LoginAsync(Client);
        var aliceId = await CreateAsync(Client, "alice");
        using var bobClient = CreateCookieClient();
        var bob = await LoginAsync(bobClient);
        var bobId = await CreateAsync(bobClient, "bob");

        using var list = await SendAsync(Client, new(HttpMethod.Get, Path));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listBody = await ReadAsync(list);
        var only = Assert.Single(listBody.RootElement.EnumerateArray());
        Assert.Equal(aliceId, only.GetProperty("id").GetGuid());
        Assert.Equal(alice, only.GetProperty("ownerId").GetGuid());

        using var own = await SendAsync(Client, new(HttpMethod.Get, Path + aliceId));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var other = await SendAsync(Client, new(HttpMethod.Get, Path + bobId));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.NotEqual(alice, bob);
    }

    [Fact]
    public async Task UpdateAndDeleteOwnedMonitor()
    {
        await LoginAsync(Client);
        var id = await CreateAsync(Client, "original");
        using var updated = await MutateAsync(Client, HttpMethod.Put, Path + id,
            Valid(name: "Changed", slug: "changed", interval: 120));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var body = await ReadAsync(updated);
        Assert.Equal(id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Changed", body.RootElement.GetProperty("name").GetString());
        using var removed = await MutateAsync(Client, HttpMethod.Delete, Path + id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var missing = await SendAsync(Client, new(HttpMethod.Get, Path + id));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task OtherUserCannotUpdateOrDeleteMonitor()
    {
        await LoginAsync(Client);
        var id = await CreateAsync(Client, "owned");
        using var other = CreateCookieClient();
        await LoginAsync(other);
        using var update = await MutateAsync(other, HttpMethod.Put, Path + id, Valid(name: "Intruder"));
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        using var delete = await MutateAsync(other, HttpMethod.Delete, Path + id);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        using var read = await SendAsync(Client, new(HttpMethod.Get, Path + id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var body = await ReadAsync(read);
        Assert.Equal("Monitor", body.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SlugsAreUniquePerOwner()
    {
        await LoginAsync(Client);
        _ = await CreateAsync(Client, "same");
        using var duplicate = await MutateAsync(Client, HttpMethod.Post, Path, Valid(slug: "same"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var other = CreateCookieClient();
        await LoginAsync(other);
        using var allowed = await MutateAsync(other, HttpMethod.Post, Path, Valid(slug: "same"));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task UpdatingToExistingOwnSlugReturnsConflict()
    {
        await LoginAsync(Client);
        _ = await CreateAsync(Client, "first");
        var second = await CreateAsync(Client, "second");
        using var response = await MutateAsync(Client, HttpMethod.Put, Path + second, Valid(slug: "first"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var read = await SendAsync(Client, new(HttpMethod.Get, Path + second));
        using var body = await ReadAsync(read);
        Assert.Equal("second", body.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task UnknownMonitorReturnsNotFoundForEachOperation()
    {
        await LoginAsync(Client);
        var path = Path + Guid.NewGuid();
        using var read = await SendAsync(Client, new(HttpMethod.Get, path));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var update = await MutateAsync(Client, HttpMethod.Put, path, Valid());
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        using var delete = await MutateAsync(Client, HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Theory]
    [InlineData("", "valid", "https://example.invalid", 60, 10)]
    [InlineData("   ", "valid", "https://example.invalid", 60, 10)]
    [InlineData("valid", "bad slug", "https://example.invalid", 60, 10)]
    [InlineData("valid", "valid", "ftp://example.invalid", 60, 10)]
    [InlineData("valid", "valid", "https://user:pass@example.invalid", 60, 10)]
    [InlineData("valid", "valid", "relative/path", 60, 10)]
    [InlineData("valid", "valid", "https://example.invalid", 59, 10)]
    [InlineData("valid", "valid", "https://example.invalid", 3601, 10)]
    [InlineData("valid", "valid", "https://example.invalid", 60, 4)]
    [InlineData("valid", "valid", "https://example.invalid", 60, 31)]
    public async Task InvalidInputIsRejected(string name, string slug, string url, int interval, int timeout)
    {
        await LoginAsync(Client);
        using var response = await MutateAsync(Client, HttpMethod.Post, Path,
            Valid(name, slug, url, interval, timeout));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await ReadAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task LengthLimitsAreRejected()
    {
        await LoginAsync(Client);
        foreach (var input in new[] { Valid(name: new string('n', 101)),
            Valid(slug: new string('s', 101)),
            Valid(url: "https://example.invalid/" + new string('a', 2048)) })
        {
            using var response = await MutateAsync(Client, HttpMethod.Post, Path, input);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task MissingRequiredFieldsAreRejected()
    {
        await LoginAsync(Client);
        using var response = await MutateAsync(Client, HttpMethod.Post, Path, new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", false)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", false)]
    [InlineData("DELETE", true)]
    public async Task MutationRequiresValidCsrf(string method, bool invalid)
    {
        await LoginAsync(Client);
        var id = method == "POST" ? Guid.Empty : await CreateAsync(Client, "original");
        using var request = new HttpRequestMessage(new HttpMethod(method),
            method == "POST" ? Path : Path + id);
        if (method != "DELETE") request.Content = JsonContent.Create(Valid());
        if (invalid) request.Headers.Add("X-SENTINEL-CSRF", "invalid-token");
        using var response = await SendAsync(Client, request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await ReadAsync(response);
        Assert.Equal("Invalid CSRF token.", body.RootElement.GetProperty("error").GetString());
        using var list = await SendAsync(Client, new(HttpMethod.Get, Path));
        using var listBody = await ReadAsync(list);
        var items = listBody.RootElement.EnumerateArray().ToArray();
        Assert.Equal(method == "POST" ? 0 : 1, items.Length);
        if (items.Length == 1) Assert.Equal("original", items[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task OverpostedFieldsDoNotChangeOwnershipOrServerTimestamps()
    {
        var alice = await LoginAsync(Client);
        using var bobClient = CreateCookieClient();
        var bob = await LoginAsync(bobClient);
        var input = new { name = "Monitor", slug = "safe", url = "https://example.invalid",
            intervalSeconds = 60, timeoutSeconds = 10, isEnabled = true,
            ownerId = bob, id = Guid.NewGuid(), createdAt = DateTimeOffset.UnixEpoch,
            nextCheckAt = DateTimeOffset.UnixEpoch };
        using var created = await MutateAsync(Client, HttpMethod.Post, Path, input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = await ReadAsync(created);
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(alice, body.RootElement.GetProperty("ownerId").GetGuid());
        Assert.NotEqual(input.id, id);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, body.RootElement.GetProperty("createdAt").GetDateTimeOffset());
        using var updated = await MutateAsync(Client, HttpMethod.Put, Path + id, input);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedBody = await ReadAsync(updated);
        Assert.Equal(alice, updatedBody.RootElement.GetProperty("ownerId").GetGuid());
    }

    [Fact]
    public async Task SchedulingClearsOnDisableAndRecalculatesOnReenableOrIntervalChange()
    {
        await LoginAsync(Client);
        var id = await CreateAsync(Client, "schedule");
        using var original = await SendAsync(Client, new(HttpMethod.Get, Path + id));
        using var originalBody = await ReadAsync(original);
        var originalNext = originalBody.RootElement.GetProperty("nextCheckAt").GetDateTimeOffset();
        using var disabled = await MutateAsync(Client, HttpMethod.Put, Path + id, Valid(enabled: false));
        using var disabledBody = await ReadAsync(disabled);
        Assert.Equal(JsonValueKind.Null, disabledBody.RootElement.GetProperty("nextCheckAt").ValueKind);
        using var enabled = await MutateAsync(Client, HttpMethod.Put, Path + id, Valid(enabled: true));
        using var enabledBody = await ReadAsync(enabled);
        var next = enabledBody.RootElement.GetProperty("nextCheckAt").GetDateTimeOffset();
        Assert.True(next >= originalNext);
        using var changed = await MutateAsync(Client, HttpMethod.Put, Path + id, Valid(interval: 120));
        using var changedBody = await ReadAsync(changed);
        var changedNext = changedBody.RootElement.GetProperty("nextCheckAt").GetDateTimeOffset();
        Assert.True(changedNext >= DateTimeOffset.UtcNow.AddSeconds(119));
        Assert.Equal(originalBody.RootElement.GetProperty("createdAt").GetDateTimeOffset(),
            changedBody.RootElement.GetProperty("createdAt").GetDateTimeOffset());
    }

    private async Task<Guid> LoginAsync(HttpClient client)
    {
        await Database.AssertLockHeldAsync();
        var email = $"monitor-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
        var user = new SentinelUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        var csrf = await CsrfAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        { Content = JsonContent.Create(new { email, password }) };
        login.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendAsync(client, login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return user.Id;
    }

    private async Task<Guid> CreateAsync(HttpClient client, string slug)
    {
        using var response = await MutateAsync(client, HttpMethod.Post, Path, Valid(slug: slug));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await ReadAsync(response);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<string> CsrfAsync(HttpClient client)
    {
        using var response = await SendAsync(client, new(HttpMethod.Get, "/api/auth/csrf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadAsync(response);
        return body.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private async Task<HttpResponseMessage> MutateAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var csrf = await CsrfAsync(client);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        return await SendAsync(client, request);
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

    private static object Valid(string name = "Monitor", string slug = "monitor",
        string url = "https://example.invalid", int interval = 60, int timeout = 10, bool enabled = true) =>
        new { name, slug, url, intervalSeconds = interval, timeoutSeconds = timeout, isEnabled = enabled };

}
