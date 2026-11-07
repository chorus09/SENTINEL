using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Sentinel.Api;

public static class RateLimitPolicies
{
    public const string Login = "Login";
    public const string TokenCreation = "TokenCreation";
    public const string TokenRevocation = "TokenRevocation";
    public const string CliAuthentication = "CliAuthentication";

    public static IServiceCollection AddSentinelRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await response.WriteAsJsonAsync(
                    new { error = "Too many requests." }, cancellationToken);
            };

            options.AddPolicy(Login, context => RateLimitPartition.GetSlidingWindowLimiter(
                ClientIpKey(context),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(5),
                    SegmentsPerWindow = 5,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            options.AddPolicy(TokenCreation, context => RateLimitPartition.GetFixedWindowLimiter(
                AuthenticatedUserOrClientIpKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            options.AddPolicy(TokenRevocation, context => RateLimitPartition.GetFixedWindowLimiter(
                AuthenticatedUserOrClientIpKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            options.AddPolicy(CliAuthentication, context => RateLimitPartition.GetTokenBucketLimiter(
                ClientIpKey(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 120,
                    TokensPerPeriod = 2,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });

    private static string AuthenticatedUserOrClientIpKey(HttpContext context)
    {
        var claim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (context.User.Identity?.IsAuthenticated == true && Guid.TryParse(claim, out var userId))
        {
            return $"user:{userId:D}";
        }

        return ClientIpKey(context);
    }

    private static string ClientIpKey(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;

        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }

        return $"ip:{address?.ToString() ?? "unknown"}";
    }
}
