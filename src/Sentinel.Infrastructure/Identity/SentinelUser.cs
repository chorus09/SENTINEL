using Microsoft.AspNetCore.Identity;

namespace Sentinel.Infrastructure.Identity;

public sealed class SentinelUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; } =
        DateTimeOffset.UtcNow;
}