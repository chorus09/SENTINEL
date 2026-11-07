using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Infrastructure.Identity;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class RateLimitingTests : IntegrationTestBase
{
    [Fact]
    public async Task LoginReturns429AfterTenRequestsWithRetryAfter()
    {
        await Database.AssertLockHeldAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostLoginWithoutCsrfAsync(Client);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var rejected = await PostLoginWithoutCsrfAsync(Client);
        await AssertRateLimitedAsync(rejected, expectRetryAfter: false);
        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task TokenCreationReturns429AfterFiveRequests()
    {
        await CreateAndLoginUserAsync(Client);
        var csrf = await GetCsrfAsync(Client);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await PostTokenAsync(Client, csrf);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var rejected = await PostTokenAsync(Client, csrf);
        await AssertRateLimitedAsync(rejected);
    }

    [Fact]
    public async Task TokenRevocationReturns429AfterThirtyRequests()
    {
        await CreateAndLoginUserAsync(Client);
        var csrf = await GetCsrfAsync(Client);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var response = await RevokeUnknownTokenAsync(Client, csrf);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var rejected = await RevokeUnknownTokenAsync(Client, csrf);
        await AssertRateLimitedAsync(rejected);
    }

    [Fact]
    public async Task CliBurstExhaustsTokenBucket()
    {
        await Database.AssertLockHeldAsync();
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        var requests = Enumerable.Range(0, 130)
            .Select(_ => client.GetAsync("/api/cli/whoami", TestContext.Current.CancellationToken))
            .ToArray();
        var responses = await Task.WhenAll(requests);

        try
        {
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.All(responses, response => Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests));
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task AuthenticatedUsersHaveSeparateTokenCreationLimits()
    {
        await CreateAndLoginUserAsync(Client);
        var firstCsrf = await GetCsrfAsync(Client);
        using var otherClient = CreateCookieClient();
        await CreateAndLoginUserAsync(otherClient);
        var secondCsrf = await GetCsrfAsync(otherClient);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await PostTokenAsync(Client, firstCsrf);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var firstRejected = await PostTokenAsync(Client, firstCsrf);
        await AssertRateLimitedAsync(firstRejected);

        using var secondAccepted = await PostTokenAsync(otherClient, secondCsrf);
        Assert.Equal(HttpStatusCode.OK, secondAccepted.StatusCode);
    }

    [Fact]
    public async Task ClientIpsHaveSeparateLoginLimits()
    {
        await Database.AssertLockHeldAsync();
        using var firstClient = CreateClientFrom(IPAddress.Parse("198.51.100.10"));
        using var secondClient = CreateClientFrom(IPAddress.Parse("198.51.100.11"));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostLoginWithoutCsrfAsync(firstClient);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var firstRejected = await PostLoginWithoutCsrfAsync(firstClient);
        await AssertRateLimitedAsync(firstRejected, expectRetryAfter: false);

        using var secondAccepted = await PostLoginWithoutCsrfAsync(secondClient);
        Assert.Equal(HttpStatusCode.BadRequest, secondAccepted.StatusCode);
        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task SpoofedForwardedForCannotEvadeLoginLimit()
    {
        await Database.AssertLockHeldAsync();
        using var proxyFactory = StartProxyFactory();
        using var client = CreateClientFrom(proxyFactory, IPAddress.Parse("198.51.100.20"));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostLoginWithoutCsrfAsync(
                client, $"203.0.113.{attempt + 1}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var rejected = await PostLoginWithoutCsrfAsync(client, "203.0.113.200");
        await AssertRateLimitedAsync(rejected, expectRetryAfter: false);
        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task TrustedForwardedClientIpsHaveSeparateLoginLimits()
    {
        await Database.AssertLockHeldAsync();
        using var proxyFactory = StartProxyFactory();
        using var client = CreateClientFrom(proxyFactory, IPAddress.Parse("172.30.77.10"));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostLoginWithoutCsrfAsync(client, "198.51.100.30");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var otherClient = await PostLoginWithoutCsrfAsync(client, "198.51.100.31");
        Assert.Equal(HttpStatusCode.BadRequest, otherClient.StatusCode);

        using var rejected = await PostLoginWithoutCsrfAsync(client, "198.51.100.30");
        await AssertRateLimitedAsync(rejected, expectRetryAfter: false);
        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task HealthRemainsAvailableAfterLoginLimit()
    {
        await Database.AssertLockHeldAsync();

        for (var attempt = 0; attempt < 11; attempt++)
        {
            using var response = await PostLoginWithoutCsrfAsync(Client);
            Assert.Equal(attempt == 10 ? HttpStatusCode.TooManyRequests : HttpStatusCode.BadRequest,
                response.StatusCode);
        }

        using var health = await Client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await Database.AssertLockHeldAsync();
    }

    [Fact]
    public async Task AuthenticationAndCsrfStillApplyBeforeLimitsAreReached()
    {
        await Database.AssertLockHeldAsync();
        using var anonymousToken = await PostTokenAsync(Client, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousToken.StatusCode);

        using var missingLoginCsrf = await PostLoginWithoutCsrfAsync(Client);
        Assert.Equal(HttpStatusCode.BadRequest, missingLoginCsrf.StatusCode);
        await AssertCsrfErrorAsync(missingLoginCsrf);

        await CreateAndLoginUserAsync(Client);
        using var missingTokenCsrf = await PostTokenAsync(Client, null);
        Assert.Equal(HttpStatusCode.BadRequest, missingTokenCsrf.StatusCode);
        await AssertCsrfErrorAsync(missingTokenCsrf);

        using var missingRevokeCsrf = await RevokeUnknownTokenAsync(Client, null);
        Assert.Equal(HttpStatusCode.BadRequest, missingRevokeCsrf.StatusCode);
        await AssertCsrfErrorAsync(missingRevokeCsrf);
        await Database.AssertLockHeldAsync();
    }

    private async Task CreateAndLoginUserAsync(HttpClient client)
    {
        await Database.AssertLockHeldAsync();
        var email = $"integration-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
            var result = await users.CreateAsync(new SentinelUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
            }, password);
            Assert.True(result.Succeeded, "Could not create the integration test user.");
        }

        var csrf = await GetCsrfAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        login.Headers.Add("X-SENTINEL-CSRF", csrf);
        using var response = await SendCheckedAsync(client, login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith("Sentinel.Auth=", StringComparison.Ordinal)),
            "Login did not issue the authentication cookie.");
        await Database.AssertLockHeldAsync();
    }

    private async Task<string> GetCsrfAsync(HttpClient client)
    {
        using var response = await SendCheckedAsync(client,
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("No CSRF token was returned.");
    }

    private Task<HttpResponseMessage> PostLoginWithoutCsrfAsync(
        HttpClient client, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "", password = "" }),
        };

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return SendCheckedAsync(client, request);
    }

    private Task<HttpResponseMessage> PostTokenAsync(HttpClient client, string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tokens/")
        {
            Content = JsonContent.Create(new { name = "rate-limit-test", expiresInDays = 30 }),
        };

        if (csrf is not null)
        {
            request.Headers.Add("X-SENTINEL-CSRF", csrf);
        }

        return SendCheckedAsync(client, request);
    }

    private Task<HttpResponseMessage> RevokeUnknownTokenAsync(HttpClient client, string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/tokens/{Guid.NewGuid()}");

        if (csrf is not null)
        {
            request.Headers.Add("X-SENTINEL-CSRF", csrf);
        }

        return SendCheckedAsync(client, request);
    }

    private HttpClient CreateClientFrom(IPAddress remoteIp) => CreateClientFrom(Factory, remoteIp);

    private static HttpClient CreateClientFrom(WebApplicationFactory<Program> factory, IPAddress remoteIp) => new(
        factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = remoteIp))
    {
        BaseAddress = new Uri("https://localhost"),
    };

    private WebApplicationFactory<Program> StartProxyFactory()
    {
        const string enabledKey = "ReverseProxy__Enabled";
        const string trustedIpKey = "ReverseProxy__TrustedIp";
        var previousEnabled = Environment.GetEnvironmentVariable(enabledKey);
        var previousTrustedIp = Environment.GetEnvironmentVariable(trustedIpKey);

        try
        {
            Environment.SetEnvironmentVariable(enabledKey, "true");
            Environment.SetEnvironmentVariable(trustedIpKey, "172.30.77.10");
            var factory = Factory.WithWebHostBuilder(_ => { });

            try
            {
                _ = factory.Server;
                return factory;
            }
            catch
            {
                factory.Dispose();
                throw;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(enabledKey, previousEnabled);
            Environment.SetEnvironmentVariable(trustedIpKey, previousTrustedIp);
        }
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

    private static async Task AssertRateLimitedAsync(
        HttpResponseMessage response, bool expectRetryAfter = true)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        if (expectRetryAfter)
        {
            Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
            Assert.True(int.TryParse(Assert.Single(values), out var seconds) && seconds > 0,
                "Retry-After must be a positive number of seconds.");
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Too many requests.", document.RootElement.GetProperty("error").GetString());
    }

    private static async Task AssertCsrfErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Invalid CSRF token.", document.RootElement.GetProperty("error").GetString());
    }
}
