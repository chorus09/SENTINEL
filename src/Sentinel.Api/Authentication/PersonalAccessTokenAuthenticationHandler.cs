using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sentinel.Infrastructure.Identity;

namespace Sentinel.Api.Authentication;

public sealed class PersonalAccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PersonalAccessTokenService tokenService,
    UserManager<SentinelUser> userManager
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SentinelPat";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authorization))
            return AuthenticateResult.NoResult();

        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorization["Bearer ".Length..].Trim();

        if (token.Length != 69)
            return AuthenticateResult.Fail("Invalid token.");

        var storedToken = await tokenService.ValidateAsync(token, Context.RequestAborted);

        if (storedToken is null)
            return AuthenticateResult.Fail("Invalid token.");

        var user = await userManager.FindByIdAsync(storedToken.UserId.ToString());

        if (user is null || !user.EmailConfirmed || await userManager.IsLockedOutAsync(user))
        {
            return AuthenticateResult.Fail("Invalid user.");
        }
        var markedAsUsed = await tokenService.MarkUsedAsync(storedToken.Id, Context.RequestAborted);

        if (!markedAsUsed)
        {
            return AuthenticateResult.Fail("Token is no longer active.");
        }
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new("sentinel_token_id", storedToken.Id.ToString()),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
