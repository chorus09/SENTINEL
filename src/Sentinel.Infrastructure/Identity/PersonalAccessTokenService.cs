using Microsoft.EntityFrameworkCore;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Identity;

public sealed class PersonalAccessTokenService(SentinelDbContext dbContext)
{
    public async Task<CreatedPersonalAccessToken> CreateAsync(
        Guid userId,
        string name,
        int expiresInDays,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Token name is required.");

        name = name.Trim();

        if (name.Length > 100)
            throw new ArgumentException("Token name cannot exceed 100 characters.");

        if (expiresInDays < 1 || expiresInDays > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresInDays),
                "Token expiration must be between 1 and 90 days."
            );
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(expiresInDays);

        var (token, hash) = PersonalAccessTokenGenerator.Generate();

        var entity = new PersonalAccessToken
        {
            UserId = userId,
            Name = name,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        dbContext.PersonalAccessTokens.Add(entity);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreatedPersonalAccessToken(entity.Id, entity.Name, token, entity.ExpiresAt);
    }

    public async Task<List<PersonalAccessToken>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    )
    {
        return await dbContext
            .PersonalAccessTokens.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<PersonalAccessToken?> ValidateAsync(
        string token,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(token)
            || token.Length != 69
            || !token.StartsWith("sntl_", StringComparison.Ordinal)
        )
        {
            return null;
        }

        var hash = PersonalAccessTokenGenerator.ComputeHash(token);

        var now = DateTimeOffset.UtcNow;

        return await dbContext
            .PersonalAccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.TokenHash == hash && x.RevokedAt == null && x.ExpiresAt > now,
                cancellationToken
            );
    }

    public async Task<bool> MarkUsedAsync(
        Guid tokenId,
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTimeOffset.UtcNow;

        var updatedRows = await dbContext
            .PersonalAccessTokens.Where(x =>
                x.Id == tokenId && x.RevokedAt == null && x.ExpiresAt > now
            )
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.LastUsedAt, now),
                cancellationToken
            );

        return updatedRows == 1;
    }

    public async Task<bool> RevokeAsync(
        Guid userId,
        Guid tokenId,
        CancellationToken cancellationToken = default
    )
    {
        var token = await dbContext.PersonalAccessTokens.FirstOrDefaultAsync(
            x => x.Id == tokenId && x.UserId == userId && x.RevokedAt == null,
            cancellationToken
        );

        if (token is null)
            return false;

        token.RevokedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }
}

public sealed record CreatedPersonalAccessToken(
    Guid Id,
    string Name,
    string Token,
    DateTimeOffset ExpiresAt
);
