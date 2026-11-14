using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sentinel.Domain.Entities;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Api.Endpoints;

public static class MonitorIncidentEndpoints
{
    public static IEndpointConventionBuilder MapMonitorIncidentEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/monitors/{id:guid}/incidents", async (
            Guid id, ClaimsPrincipal user, SentinelDbContext db,
            [FromQuery(Name = "pageSize")] string? pageSizeText,
            [FromQuery] string? cursor, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var ownerId)
                || ownerId == Guid.Empty)
                return Results.Unauthorized();

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
                return Results.BadRequest(new { error = "Invalid incident cursor." });

            List<MonitorIncident> rows;
            if (hasCursor)
            {
                rows = await db.MonitorIncidents.FromSqlInterpolated($"""
                    SELECT * FROM monitor_incidents
                    WHERE "MonitorId" = {id}
                      AND ("OpenedAt", "Id") < ({position.OpenedAt}, {position.Id})
                    ORDER BY "OpenedAt" DESC, "Id" DESC
                    LIMIT {pageSize + 1}
                    """).AsNoTracking().ToListAsync(cancellationToken);
            }
            else
            {
                rows = await db.MonitorIncidents.AsNoTracking()
                    .Where(x => x.MonitorId == id)
                    .OrderByDescending(x => x.OpenedAt).ThenByDescending(x => x.Id)
                    .Take(pageSize + 1).ToListAsync(cancellationToken);
            }

            var items = rows.Take(pageSize).Select(x => new MonitorIncidentResponse(
                x.Id, x.MonitorId, x.ResolvedAt is null ? "OPEN" : "RESOLVED",
                x.OpenedAt, x.ResolvedAt)).ToArray();
            var nextCursor = rows.Count > pageSize ? EncodeCursor(id, rows[pageSize - 1]) : null;
            return Results.Ok(new MonitorIncidentPage(items, nextCursor));
        }).RequireAuthorization("CookieOnly");

    private static string EncodeCursor(Guid monitorId, MonitorIncident incident)
    {
        Span<byte> bytes = stackalloc byte[40];
        monitorId.TryWriteBytes(bytes[..16]);
        BinaryPrimitives.WriteInt64BigEndian(bytes.Slice(16, 8), incident.OpenedAt.UtcTicks);
        incident.Id.TryWriteBytes(bytes.Slice(24, 16));
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
        var incidentId = new Guid(bytes.AsSpan(24, 16));
        if (incidentId == Guid.Empty || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        position = new(new DateTimeOffset(ticks, TimeSpan.Zero), incidentId);
        return true;
    }

    private readonly record struct CursorPosition(DateTimeOffset OpenedAt, Guid Id);
}

public sealed record MonitorIncidentResponse(Guid Id, Guid MonitorId, string State,
    DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt);

public sealed record MonitorIncidentPage(IReadOnlyList<MonitorIncidentResponse> Items, string? NextCursor);
