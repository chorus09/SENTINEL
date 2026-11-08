using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sentinel.Domain.Entities;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Api.Endpoints;

public static class MonitorCheckEndpoints
{
    public static IEndpointConventionBuilder MapMonitorCheckEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/monitors/{id:guid}/checks", async (
            Guid id, ClaimsPrincipal user, SentinelDbContext db,
            [FromQuery(Name = "pageSize")] string? pageSizeText,
            [FromQuery] string? cursor, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var ownerId)
                || ownerId == Guid.Empty)
                return Results.Unauthorized();

            // Ownership is checked before the history query or cursor validation.
            if (!await db.Monitors.AsNoTracking().AnyAsync(
                m => m.Id == id && m.OwnerId == ownerId, cancellationToken))
                return Results.NotFound();

            var pageSize = 20;
            if (pageSizeText is not null
                && (!int.TryParse(pageSizeText, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                    || pageSize is < 1 or > 100))
                return Results.BadRequest(new { error = "Page size must be between 1 and 100." });

            var hasCursor = cursor is not null;
            CursorPosition position = default;
            if (hasCursor && !TryDecodeCursor(cursor!, id, out position))
                return Results.BadRequest(new { error = "Invalid history cursor." });

            List<MonitorCheckResult> rows;
            if (hasCursor)
            {
                rows = await db.MonitorCheckResults.FromSqlInterpolated($"""
                    SELECT * FROM monitor_check_results
                    WHERE "MonitorId" = {id}
                      AND ("CheckedAt", "Id") < ({position.CheckedAt}, {position.Id})
                    ORDER BY "CheckedAt" DESC, "Id" DESC
                    LIMIT {pageSize + 1}
                    """).AsNoTracking().ToListAsync(cancellationToken);
            }
            else
            {
                rows = await db.MonitorCheckResults.AsNoTracking()
                    .Where(x => x.MonitorId == id)
                    .OrderByDescending(x => x.CheckedAt).ThenByDescending(x => x.Id)
                    .Take(pageSize + 1).ToListAsync(cancellationToken);
            }

            var hasMore = rows.Count > pageSize;
            var items = rows.Take(pageSize).Select(x => new MonitorCheckResponse(
                x.Id, x.CheckedAt, x.Outcome, x.HttpStatusCode, x.DurationMilliseconds)).ToArray();
            var nextCursor = hasMore ? EncodeCursor(id, rows[pageSize - 1]) : null;
            return Results.Ok(new MonitorCheckPage(items, nextCursor));
        }).RequireAuthorization("CookieOnly");

    private static string EncodeCursor(Guid monitorId, MonitorCheckResult result)
    {
        Span<byte> bytes = stackalloc byte[40];
        monitorId.TryWriteBytes(bytes[..16]);
        BinaryPrimitives.WriteInt64BigEndian(bytes.Slice(16, 8), result.CheckedAt.UtcTicks);
        result.Id.TryWriteBytes(bytes.Slice(24, 16));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCursor(string value, Guid monitorId, out CursorPosition position)
    {
        position = default;
        if (value.Length != 54 || value.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            return false;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=="); }
        catch (FormatException) { return false; }
        if (bytes.Length != 40 || new Guid(bytes.AsSpan(0, 16)) != monitorId) return false;

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(16, 8));
        var resultId = new Guid(bytes.AsSpan(24, 16));
        if (resultId == Guid.Empty || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        position = new(new DateTimeOffset(ticks, TimeSpan.Zero), resultId);
        return true;
    }

    private readonly record struct CursorPosition(DateTimeOffset CheckedAt, Guid Id);
}

public sealed record MonitorCheckResponse(Guid Id, DateTimeOffset CheckedAt, string Outcome,
    int? HttpStatusCode, int DurationMilliseconds);

public sealed record MonitorCheckPage(IReadOnlyList<MonitorCheckResponse> Items, string? NextCursor);
