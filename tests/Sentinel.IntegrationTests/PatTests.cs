using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Infrastructure.Identity;
using Sentinel.Infrastructure.Persistence;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class PatTests : IntegrationTestBase
{
    private const string TokensPath = "/api/tokens/";

    [Fact]
    public async Task CreatePatWithCsrfReturnsPrefixedSecretOnlyAtCreation()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);

        Assert.True(created.Token.StartsWith("sntl_", StringComparison.Ordinal),
            "The created PAT has an unexpected prefix.");
        Assert.Equal(69, created.Token.Length);

        using var list = await SendCheckedAsync(Client, new HttpRequestMessage(HttpMethod.Get, TokensPath));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(body.Contains(created.Token, StringComparison.Ordinal),
            "The token list exposed the PAT secret.");
    }

    [Fact]
    public async Task DatabaseStoresHashRatherThanPlaintextPat()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);

        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var stored = await db.PersonalAccessTokens.AsNoTracking()
            .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);

        Assert.True(string.Equals(stored.TokenHash,
            PersonalAccessTokenGenerator.ComputeHash(created.Token), StringComparison.Ordinal),
            "The stored PAT hash does not match the created token.");
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.False(string.Equals(stored.TokenHash, created.Token, StringComparison.Ordinal),
            "The database stored the plaintext PAT.");

        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT row_to_json(t)::text FROM personal_access_tokens AS t WHERE \"Id\" = @id";
            var id = command.CreateParameter();
            id.ParameterName = "id";
            id.Value = created.Id;
            command.Parameters.Add(id);

            var row = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(row);
            Assert.False(row.Contains(created.Token, StringComparison.Ordinal),
                "The stored PAT row contains the plaintext secret.");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task TokenListContainsMetadataWithoutSecretsOrHashes()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);

        using var response = await SendCheckedAsync(Client, new HttpRequestMessage(HttpMethod.Get, TokensPath));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var item = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(created.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("integration-pat", item.GetProperty("name").GetString());

        var names = item.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert.True(names.SetEquals(new[]
        {
            "id", "name", "createdAt", "expiresAt", "lastUsedAt", "revokedAt",
        }), "The token list contains unexpected fields.");
        Assert.False(document.RootElement.GetRawText().Contains(created.Token, StringComparison.Ordinal),
            "The token list exposed the PAT secret.");
    }

    [Fact]
    public async Task ValidBearerPatAuthenticatesWhoAmI()
    {
        var userId = await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        using var bearerClient = CreateBearerClient();

        using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", created.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(userId.ToString(), document.RootElement.GetProperty("userId").GetString());
        Assert.Equal("SentinelPat", document.RootElement.GetProperty("authenticationType").GetString());
    }

    [Fact]
    public async Task BearerPatCannotAccessCookieOnlyTokenList()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        using var bearerClient = CreateBearerClient();

        using var response = await SendBearerAsync(bearerClient, TokensPath, created.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Fact]
    public async Task UnknownBearerPatIsRejected()
    {
        using var bearerClient = CreateBearerClient();
        var unknown = $"sntl_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

        using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", unknown);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ModifiedBearerPatIsRejectedWithoutUpdatingLastUsedAt()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        var lastCharacter = created.Token[^1] == '0' ? '1' : '0';
        var modified = created.Token[..^1] + lastCharacter;
        using var bearerClient = CreateBearerClient();

        using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", modified);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Fact]
    public async Task IncorrectPatPrefixIsRejectedWithoutUpdatingLastUsedAt()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        var wrongPrefix = "other" + created.Token[5..];
        using var bearerClient = CreateBearerClient();

        using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", wrongPrefix);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Fact]
    public async Task IncorrectPatLengthsAreRejectedWithoutUpdatingLastUsedAt()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        using var bearerClient = CreateBearerClient();

        foreach (var invalid in new[] { created.Token[..^1], created.Token + "0" })
        {
            using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", invalid);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Fact]
    public async Task IncorrectAuthorizationSchemeIsRejectedWithoutUpdatingLastUsedAt()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        using var bearerClient = CreateBearerClient();

        using var response = await SendAuthorizationAsync(
            bearerClient, "/api/cli/whoami", "Basic", created.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Fact]
    public async Task MissingAuthorizationHeaderIsRejectedWithoutUpdatingLastUsedAt()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        using var bearerClient = CreateBearerClient();

        using var response = await SendCheckedAsync(bearerClient,
            new HttpRequestMessage(HttpMethod.Get, "/api/cli/whoami"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Fact]
    public async Task CookieWithoutPatCannotAccessPatOnlyWhoAmI()
    {
        await CreateAndLoginUserAsync(Client);

        using var response = await SendCheckedAsync(Client,
            new HttpRequestMessage(HttpMethod.Get, "/api/cli/whoami"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SuccessfulPatAuthenticationUpdatesLastUsedAt()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);

        await Database.AssertLockHeldAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            var before = await db.PersonalAccessTokens.AsNoTracking()
                .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);
            Assert.Null(before.LastUsedAt);
        }

        using var bearerClient = CreateBearerClient();
        using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", created.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            var after = await db.PersonalAccessTokens.AsNoTracking()
                .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(after.LastUsedAt);
            Assert.True(after.LastUsedAt >= after.CreatedAt);
        }
        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task RevokedPatIsRejected()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);

        using var revoke = await RevokeAsync(Client, created.Id);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var bearerClient = CreateBearerClient();
        using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", created.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Fact]
    public async Task ExpiredPatIsRejectedWithoutWaiting()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);

        await Database.AssertLockHeldAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            var stored = await db.PersonalAccessTokens
                .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);
            stored.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await Database.AssertLockHeldAsync();

        using var bearerClient = CreateBearerClient();
        using var response = await SendBearerAsync(bearerClient, "/api/cli/whoami", created.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLastUsedAtIsNullAsync(created.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyPatNameIsRejected(string name)
    {
        await CreateAndLoginUserAsync(Client);
        var csrf = await GetCsrfAsync(Client);

        using var response = await PostCreateAsync(Client, csrf, 30, name);
        await AssertBadRequestAsync(response, "Token name is required.");
        await AssertNoTokensPersistedAsync();
    }

    [Fact]
    public async Task PatNameOverOneHundredCharactersIsRejected()
    {
        await CreateAndLoginUserAsync(Client);
        var csrf = await GetCsrfAsync(Client);

        using var response = await PostCreateAsync(Client, csrf, 30, new string('x', 101));
        await AssertBadRequestAsync(response, "Token name cannot exceed 100 characters.");
        await AssertNoTokensPersistedAsync();
    }

    [Fact]
    public async Task PatNameIsTrimmedOnCreationAndInMetadata()
    {
        await CreateAndLoginUserAsync(Client);
        var csrf = await GetCsrfAsync(Client);

        using var create = await PostCreateAsync(Client, csrf, 30, "  integration-pat  ");
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var createdDocument = JsonDocument.Parse(
            await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("integration-pat", createdDocument.RootElement.GetProperty("name").GetString());

        using var list = await SendCheckedAsync(Client,
            new HttpRequestMessage(HttpMethod.Get, TokensPath));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDocument = JsonDocument.Parse(
            await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("integration-pat", Assert.Single(listDocument.RootElement.EnumerateArray())
            .GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidCsrfCannotCreatePat(bool invalidToken)
    {
        await CreateAndLoginUserAsync(Client);
        _ = await GetCsrfAsync(Client);

        using var response = await PostCreateAsync(Client,
            invalidToken ? "invalid-token" : null, 30);
        await AssertBadRequestAsync(response, "Invalid CSRF token.");
        await AssertNoTokensPersistedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidCsrfCannotRevokePat(bool invalidToken)
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        _ = await GetCsrfAsync(Client);

        using var response = await SendRevokeAsync(Client, created.Id,
            invalidToken ? "invalid-token" : null);
        await AssertBadRequestAsync(response, "Invalid CSRF token.");
        await AssertNotRevokedAsync(created.Id);
    }

    [Fact]
    public async Task AnotherUserCannotListOrRevokeOwnersPat()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);
        using var otherClient = CreateCookieClient();
        await CreateAndLoginUserAsync(otherClient);

        using var list = await SendCheckedAsync(otherClient,
            new HttpRequestMessage(HttpMethod.Get, TokensPath));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var document = JsonDocument.Parse(
            await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(document.RootElement.EnumerateArray());

        using var revoke = await RevokeAsync(otherClient, created.Id);
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        using var ownerList = await SendCheckedAsync(Client,
            new HttpRequestMessage(HttpMethod.Get, TokensPath));
        Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
        using var ownerDocument = JsonDocument.Parse(
            await ownerList.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(created.Id, Assert.Single(ownerDocument.RootElement.EnumerateArray())
            .GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task InvalidExpirationIsRejected(int expiresInDays)
    {
        await CreateAndLoginUserAsync(Client);
        var csrf = await GetCsrfAsync(Client);

        using var response = await PostCreateAsync(Client, csrf, expiresInDays);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Expiration must be between 1 and 90 days.",
            document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RevokingAlreadyRevokedPatReturnsNotFound()
    {
        await CreateAndLoginUserAsync(Client);
        var created = await CreatePatAsync(Client);

        using var first = await RevokeAsync(Client, created.Id);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        using var second = await RevokeAsync(Client, created.Id);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task RevokingNonexistentPatReturnsNotFound()
    {
        await CreateAndLoginUserAsync(Client);

        using var response = await RevokeAsync(Client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Guid> CreateAndLoginUserAsync(HttpClient client)
    {
        await Database.AssertLockHeldAsync();
        var userId = Guid.NewGuid();
        var email = $"integration-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
            var result = await users.CreateAsync(new SentinelUser
            {
                Id = userId,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
            }, password);
            Assert.True(result.Succeeded, "Could not create the integration test user.");
        }
        await Database.AssertLockHeldAsync();

        var csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        request.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendCheckedAsync(client, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith("Sentinel.Auth=", StringComparison.Ordinal)),
            "The login response did not set an authentication cookie.");
        return userId;
    }

    private async Task<string> GetCsrfAsync(HttpClient client)
    {
        using var response = await SendCheckedAsync(client,
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var token = document.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), "No CSRF token was returned.");
        return token!;
    }

    private async Task<CreatedPat> CreatePatAsync(HttpClient client)
    {
        var csrf = await GetCsrfAsync(client);
        using var response = await PostCreateAsync(client, csrf, 30);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var token = root.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), "No PAT secret was returned at creation.");
        return new CreatedPat(root.GetProperty("id").GetGuid(), token!);
    }

    private Task<HttpResponseMessage> PostCreateAsync(
        HttpClient client, string? csrf, int days, string name = "integration-pat")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TokensPath)
        {
            Content = JsonContent.Create(new { name, expiresInDays = days }),
        };
        if (csrf is not null)
        {
            request.Headers.Add("X-SENTINEL-CSRF", csrf);
        }
        return SendCheckedAsync(client, request);
    }

    private async Task<HttpResponseMessage> RevokeAsync(HttpClient client, Guid id)
    {
        var csrf = await GetCsrfAsync(client);
        return await SendRevokeAsync(client, id, csrf);
    }

    private Task<HttpResponseMessage> SendRevokeAsync(HttpClient client, Guid id, string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{TokensPath}{id}");
        if (csrf is not null)
        {
            request.Headers.Add("X-SENTINEL-CSRF", csrf);
        }
        return SendCheckedAsync(client, request);
    }

    private HttpClient CreateBearerClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
        AllowAutoRedirect = false,
    });

    private Task<HttpResponseMessage> SendBearerAsync(HttpClient client, string path, string token)
        => SendAuthorizationAsync(client, path, "Bearer", token);

    private Task<HttpResponseMessage> SendAuthorizationAsync(
        HttpClient client, string path, string scheme, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        return SendCheckedAsync(client, request);
    }

    private async Task AssertBadRequestAsync(HttpResponseMessage response, string expectedError)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expectedError, document.RootElement.GetProperty("error").GetString());
        Assert.False(document.RootElement.TryGetProperty("token", out _),
            "A rejected request returned a PAT secret.");
    }

    private async Task AssertNoTokensPersistedAsync()
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        Assert.Equal(0, await db.PersonalAccessTokens.CountAsync(TestContext.Current.CancellationToken));
        await Database.AssertLockHeldAsync();
    }

    private async Task AssertNotRevokedAsync(Guid id)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var stored = await db.PersonalAccessTokens.AsNoTracking()
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        Assert.Null(stored.RevokedAt);
        await Database.AssertLockHeldAsync();
    }

    private async Task AssertLastUsedAtIsNullAsync(Guid id)
    {
        await Database.AssertLockHeldAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var stored = await db.PersonalAccessTokens.AsNoTracking()
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        Assert.Null(stored.LastUsedAt);
        await Database.AssertLockHeldAsync();
    }

    private async Task<HttpResponseMessage> SendCheckedAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            await Database.AssertLockHeldAsync();
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            try
            {
                await Database.AssertLockHeldAsync();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private sealed class CreatedPat(Guid id, string token)
    {
        public Guid Id { get; } = id;
        public string Token { get; } = token;
    }
}
