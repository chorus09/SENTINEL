using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Sentinel.Infrastructure.Identity;

namespace Sentinel.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/csrf", (
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);

            context.Response.Headers.CacheControl = "no-store";

            return Results.Ok(new
            {
                csrfToken = tokens.RequestToken
            });
        });

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            UserManager<SentinelUser> userManager,
            SignInManager<SentinelUser> signInManager) =>
        {
            if (!await IsValidCsrfAsync(context, antiforgery))
                return Results.BadRequest(new
                {
                    error = "Invalid CSRF token."
                });

            if (string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new
                {
                    error = "Email and password are required."
                });
            }

            var user = await userManager.FindByEmailAsync(
                request.Email);

            if (user is null)
                return Results.Unauthorized();

            var result = await signInManager.PasswordSignInAsync(
                user,
                request.Password,
                isPersistent: false,
                lockoutOnFailure: true);

            if (!result.Succeeded)
                return Results.Unauthorized();

            context.Response.Headers.CacheControl = "no-store";

            return Results.Ok(new
            {
                authenticated = true,
                user = new
                {
                    id = user.Id,
                    email = user.Email
                }
            });
        });

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<SentinelUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);

            if (user is null)
                return Results.Unauthorized();

            return Results.Ok(new
            {
                id = user.Id,
                email = user.Email,
                createdAt = user.CreatedAt
            });
        })
        .RequireAuthorization();

        group.MapPost("/logout", async (
            HttpContext context,
            IAntiforgery antiforgery,
            SignInManager<SentinelUser> signInManager) =>
        {
            if (!await IsValidCsrfAsync(context, antiforgery))
                return Results.BadRequest(new
                {
                    error = "Invalid CSRF token."
                });

            await signInManager.SignOutAsync();

            return Results.NoContent();
        })
        .RequireAuthorization();

        return group;
    }

    private static async Task<bool> IsValidCsrfAsync(
        HttpContext context,
        IAntiforgery antiforgery)
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

public sealed record LoginRequest(
    string Email,
    string Password);
