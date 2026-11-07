using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Infrastructure.Identity;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class AuthTests : IntegrationTestBase
{
    [Fact]
    public async Task AnonymousMeReturnsUnauthorized()
    {
        using var response = await SendCheckedAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithValidCsrfReturnsOkAndSetsAuthenticationCookie()
    {
        var (email, password) = await CreateUserAsync();
        var csrfToken = await GetCsrfTokenAsync();

        using var login = await PostLoginAsync(email, password, csrfToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        AssertCookieAttributes(login, "Sentinel.Auth", "Lax");

        using var me = await SendCheckedAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using var document = JsonDocument.Parse(
            await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(email, document.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task LoginWithoutCsrfReturnsBadRequest()
    {
        var (email, password) = await CreateUserAsync();
        _ = await GetCsrfTokenAsync();

        using var response = await PostLoginAsync(email, password, null);
        await AssertCsrfRejectedAsync(response);
    }

    [Fact]
    public async Task LoginWithInvalidCsrfReturnsBadRequest()
    {
        var (email, password) = await CreateUserAsync();
        _ = await GetCsrfTokenAsync();

        using var response = await PostLoginAsync(email, password, "invalid-token");
        await AssertCsrfRejectedAsync(response);
    }

    [Fact]
    public async Task LogoutWithValidAuthenticationAndCsrfReturnsNoContent()
    {
        await LoginForLogoutAsync();
        var csrfToken = await GetCsrfTokenAsync(expectNewCookie: false);

        using var response = await PostLogoutAsync(csrfToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertSetsCookie(response, "Sentinel.Auth");
    }

    [Fact]
    public async Task MeAfterLogoutReturnsUnauthorized()
    {
        await LoginForLogoutAsync();
        var csrfToken = await GetCsrfTokenAsync(expectNewCookie: false);

        using var logout = await PostLogoutAsync(csrfToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var me = await SendCheckedAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private async Task<(string Email, string Password)> CreateUserAsync()
    {
        await Database.AssertLockHeldAsync();

        var email = $"integration-{Guid.NewGuid():N}@example.invalid";
        var password = $"aA1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";

        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SentinelUser>>();
        var result = await users.CreateAsync(new SentinelUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
        }, password);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Could not create the integration test user.");
        }

        await Database.AssertLockHeldAsync();
        return (email, password);
    }

    private async Task<string> GetCsrfTokenAsync(bool expectNewCookie = true)
    {
        using var response = await SendCheckedAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (expectNewCookie)
        {
            AssertCookieAttributes(response, "Sentinel.Antiforgery", "Strict");
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var token = document.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), "The CSRF endpoint returned no token.");
        return token!;
    }

    private Task<HttpResponseMessage> PostLoginAsync(string email, string password, string? csrfToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };

        if (csrfToken is not null)
        {
            request.Headers.Add("X-SENTINEL-CSRF", csrfToken);
        }

        return SendCheckedAsync(request);
    }

    private Task<HttpResponseMessage> PostLogoutAsync(string csrfToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("X-SENTINEL-CSRF", csrfToken);
        return SendCheckedAsync(request);
    }

    private async Task LoginForLogoutAsync()
    {
        var (email, password) = await CreateUserAsync();
        var csrfToken = await GetCsrfTokenAsync();
        using var response = await PostLoginAsync(email, password, csrfToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCookieAttributes(response, "Sentinel.Auth", "Lax");
    }

    private async Task AssertCsrfRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Invalid CSRF token.", document.RootElement.GetProperty("error").GetString());
        AssertDoesNotSetCookie(response, "Sentinel.Auth");

        using var me = await SendCheckedAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private async Task<HttpResponseMessage> SendCheckedAsync(HttpRequestMessage request)
    {
        using (request)
        {
            await Database.AssertLockHeldAsync();
            var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

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

    private static void AssertSetsCookie(HttpResponseMessage response, string cookieName)
    {
        var found = response.Headers.TryGetValues("Set-Cookie", out var values)
            && values.Any(value => value.StartsWith($"{cookieName}=", StringComparison.Ordinal));
        Assert.True(found, $"Expected cookie {cookieName} was not set.");
    }

    private static void AssertDoesNotSetCookie(HttpResponseMessage response, string cookieName)
    {
        var found = response.Headers.TryGetValues("Set-Cookie", out var values)
            && values.Any(value => value.StartsWith($"{cookieName}=", StringComparison.Ordinal));
        Assert.False(found, $"Unexpected cookie {cookieName} was set.");
    }

    private static void AssertCookieAttributes(
        HttpResponseMessage response,
        string cookieName,
        string sameSite)
    {
        var found = response.Headers.TryGetValues("Set-Cookie", out var values);
        Assert.True(found, $"Expected cookie {cookieName} was not set.");

        var cookie = values!.FirstOrDefault(
            value => value.StartsWith($"{cookieName}=", StringComparison.Ordinal));
        Assert.True(cookie is not null, $"Expected cookie {cookieName} was not set.");

        var attributes = cookie!.Split(';', StringSplitOptions.TrimEntries);
        Assert.True(attributes.Contains("Secure", StringComparer.OrdinalIgnoreCase),
            $"Cookie {cookieName} is missing Secure.");
        Assert.True(attributes.Contains("HttpOnly", StringComparer.OrdinalIgnoreCase),
            $"Cookie {cookieName} is missing HttpOnly.");
        Assert.True(attributes.Contains($"SameSite={sameSite}", StringComparer.OrdinalIgnoreCase),
            $"Cookie {cookieName} has an unexpected SameSite policy.");
    }
}
