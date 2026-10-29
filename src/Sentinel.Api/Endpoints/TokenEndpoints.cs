using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Sentinel.Infrastructure.Identity;

namespace Sentinel.Api.Endpoints;

public static class TokenEndpoints
{
    public static RouteGroupBuilder MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tokens").RequireAuthorization("CookieOnly");

        group.MapGet(
            "/",
            async (
                HttpContext context,
                UserManager<SentinelUser> userManager,
                PersonalAccessTokenService tokenService,
                CancellationToken cancellationToken
            ) =>
            {
                var user = await userManager.GetUserAsync(context.User);

                if (user is null)
                    return Results.Unauthorized();

                var tokens = await tokenService.ListAsync(user.Id, cancellationToken);

                return Results.Ok(
                    tokens.Select(x => new
                    {
                        x.Id,
                        x.Name,
                        x.CreatedAt,
                        x.ExpiresAt,
                        x.LastUsedAt,
                        x.RevokedAt,
                    })
                );
            }
        );

        group.MapPost(
            "/",
            async (
                CreateTokenRequest request,
                HttpContext context,
                IAntiforgery antiforgery,
                UserManager<SentinelUser> userManager,
                PersonalAccessTokenService tokenService,
                CancellationToken cancellationToken
            ) =>
            {
                if (!await IsValidCsrfAsync(context, antiforgery))
                    return Results.BadRequest(new { error = "Invalid CSRF token." });

                if (request is null || request.ExpiresInDays < 1 || request.ExpiresInDays > 90)
                {
                    return Results.BadRequest(
                        new { error = "Expiration must be between 1 and 90 days." }
                    );
                }

                var user = await userManager.GetUserAsync(context.User);

                if (user is null)
                    return Results.Unauthorized();

                try
                {
                    var created = await tokenService.CreateAsync(
                        user.Id,
                        request.Name,
                        request.ExpiresInDays,
                        cancellationToken
                    );

                    context.Response.Headers.CacheControl = "no-store";

                    return Results.Ok(created);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
        );

        group.MapDelete(
            "/{id:guid}",
            async (
                Guid id,
                HttpContext context,
                IAntiforgery antiforgery,
                UserManager<SentinelUser> userManager,
                PersonalAccessTokenService tokenService,
                CancellationToken cancellationToken
            ) =>
            {
                if (!await IsValidCsrfAsync(context, antiforgery))
                    return Results.BadRequest(new { error = "Invalid CSRF token." });

                var user = await userManager.GetUserAsync(context.User);

                if (user is null)
                    return Results.Unauthorized();

                var revoked = await tokenService.RevokeAsync(user.Id, id, cancellationToken);

                return revoked ? Results.NoContent() : Results.NotFound();
            }
        );

        return group;
    }

    private static async Task<bool> IsValidCsrfAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}

public sealed record CreateTokenRequest(string Name, int ExpiresInDays);
