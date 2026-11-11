using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sentinel.Application.Monitoring;
using Sentinel.Infrastructure.Persistence;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Api.Endpoints;

public static class MonitorEndpoints
{
    private const string SlugIndex = "IX_monitors_OwnerId_Slug";
    private static readonly Regex SlugPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RouteGroupBuilder MapMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/monitors").RequireAuthorization("CookieOnly");

        group.MapGet("/", async (ClaimsPrincipal user, SentinelDbContext db, CancellationToken ct) =>
        {
            if (!TryGetOwnerId(user, out var ownerId)) return Results.Unauthorized();
            var monitors = await db.Monitors.AsNoTracking()
                .Where(m => m.OwnerId == ownerId)
                .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
                .Select(m => ToResponse(m)).ToListAsync(ct);
            return Results.Ok(monitors);
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, SentinelDbContext db, CancellationToken ct) =>
        {
            if (!TryGetOwnerId(user, out var ownerId)) return Results.Unauthorized();
            var monitor = await db.Monitors.AsNoTracking()
                .SingleOrDefaultAsync(m => m.Id == id && m.OwnerId == ownerId, ct);
            return monitor is null ? Results.NotFound() : Results.Ok(ToResponse(monitor));
        });

        group.MapGet("/{id:guid}/status", async (Guid id, ClaimsPrincipal user, SentinelDbContext db, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!TryGetOwnerId(user, out var ownerId)) return Results.Unauthorized();
            var monitor = await db.Monitors.AsNoTracking()
                .SingleOrDefaultAsync(m => m.Id == id && m.OwnerId == ownerId, ct);
            if (monitor is null) return Results.NotFound();

            var latest = await db.MonitorCheckResults.AsNoTracking()
                .Where(x => x.MonitorId == id)
                .OrderByDescending(x => x.CheckedAt).ThenByDescending(x => x.Id)
                .Select(x => new { x.CheckedAt, x.Outcome, x.HttpStatusCode, x.DurationMilliseconds })
                .FirstOrDefaultAsync(ct);

            var status = ObservedStatusPolicy.GetStatus(monitor.IsEnabled, monitor.IntervalSeconds,
                latest?.CheckedAt, latest?.Outcome, clock.GetUtcNow());
            return Results.Ok(new MonitorObservedStatusResponse(monitor.Id, monitor.Name, monitor.IsEnabled,
                status, latest?.CheckedAt, latest?.Outcome, latest?.HttpStatusCode,
                latest?.DurationMilliseconds));
        });

        group.MapPost("/", async (MonitorRequest? request, HttpContext context, IAntiforgery antiforgery,
            SentinelDbContext db, CancellationToken ct) =>
        {
            if (!await IsValidCsrfAsync(context, antiforgery)) return CsrfError();
            if (!TryGetOwnerId(context.User, out var ownerId)) return Results.Unauthorized();
            var error = Validate(request, out var values);
            if (error is not null) return Results.BadRequest(new { error });

            var now = DateTimeOffset.UtcNow;
            var monitor = new MonitorEntity
            {
                Id = Guid.NewGuid(), OwnerId = ownerId, Name = values.Name, Slug = values.Slug,
                Url = values.Url, IntervalSeconds = values.IntervalSeconds,
                TimeoutSeconds = values.TimeoutSeconds, IsEnabled = values.IsEnabled,
                CreatedAt = now,
                NextCheckAt = values.IsEnabled ? now.AddSeconds(values.IntervalSeconds) : null,
            };
            db.Monitors.Add(monitor);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsSlugConflict(ex)) { return SlugConflict(); }
            return Results.Created($"/api/monitors/{monitor.Id}", ToResponse(monitor));
        });

        group.MapPut("/{id:guid}", async (Guid id, MonitorRequest? request, HttpContext context,
            IAntiforgery antiforgery, SentinelDbContext db, CancellationToken ct) =>
        {
            if (!await IsValidCsrfAsync(context, antiforgery)) return CsrfError();
            if (!TryGetOwnerId(context.User, out var ownerId)) return Results.Unauthorized();
            var error = Validate(request, out var values);
            if (error is not null) return Results.BadRequest(new { error });

            var monitor = await db.Monitors.SingleOrDefaultAsync(m => m.Id == id && m.OwnerId == ownerId, ct);
            if (monitor is null) return Results.NotFound();
            var reschedule = values.IsEnabled && (!monitor.IsEnabled || monitor.IntervalSeconds != values.IntervalSeconds);
            monitor.Name = values.Name;
            monitor.Slug = values.Slug;
            monitor.Url = values.Url;
            monitor.IntervalSeconds = values.IntervalSeconds;
            monitor.TimeoutSeconds = values.TimeoutSeconds;
            monitor.IsEnabled = values.IsEnabled;
            monitor.NextCheckAt = !values.IsEnabled ? null
                : reschedule ? DateTimeOffset.UtcNow.AddSeconds(values.IntervalSeconds) : monitor.NextCheckAt;
            db.Entry(monitor).Property<long>("ConfigVersion").CurrentValue++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Monitor changed; retry the update." }); }
            catch (DbUpdateException ex) when (IsSlugConflict(ex)) { return SlugConflict(); }
            return Results.Ok(ToResponse(monitor));
        });

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context, IAntiforgery antiforgery,
            SentinelDbContext db, CancellationToken ct) =>
        {
            if (!await IsValidCsrfAsync(context, antiforgery)) return CsrfError();
            if (!TryGetOwnerId(context.User, out var ownerId)) return Results.Unauthorized();
            var deleted = await db.Monitors.Where(m => m.Id == id && m.OwnerId == ownerId).ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        return group;
    }

    private static bool TryGetOwnerId(ClaimsPrincipal user, out Guid ownerId) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out ownerId) && ownerId != Guid.Empty;

    private static bool IsSlugConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: SlugIndex };

    private static IResult SlugConflict() => Results.Conflict(new { error = "A monitor with this slug already exists." });
    private static IResult CsrfError() => Results.BadRequest(new { error = "Invalid CSRF token." });

    private static async Task<bool> IsValidCsrfAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static string? Validate(MonitorRequest? request, out MonitorValues values)
    {
        values = default;
        if (request is null) return "A monitor request is required.";
        var name = request.Name?.Trim() ?? "";
        var slug = request.Slug?.Trim().ToLowerInvariant() ?? "";
        var url = request.Url?.Trim() ?? "";
        if (name.Length is < 1 or > 100) return "Name must contain 1 to 100 characters.";
        if (slug.Length is < 1 or > 100 || !SlugPattern.IsMatch(slug))
            return "Slug must contain 1 to 100 lowercase letters, digits or hyphens.";
        if (url.Length is < 1 or > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.UserInfo) || string.IsNullOrWhiteSpace(parsed.Host))
            return "Url must be an absolute HTTP or HTTPS URL without credentials, at most 2048 characters.";
        if (request.IntervalSeconds is null or < 60 or > 3600)
            return "IntervalSeconds must be between 60 and 3600.";
        if (request.TimeoutSeconds is null or < 5 or > 30 || request.TimeoutSeconds >= request.IntervalSeconds)
            return "TimeoutSeconds must be between 5 and 30 and less than IntervalSeconds.";
        if (request.IsEnabled is null) return "IsEnabled is required.";
        values = new(name, slug, url, request.IntervalSeconds.Value, request.TimeoutSeconds.Value, request.IsEnabled.Value);
        return null;
    }

    private static MonitorResponse ToResponse(MonitorEntity m) => new(m.Id, m.OwnerId, m.Name,
        m.Slug, m.Url, m.IntervalSeconds, m.TimeoutSeconds, m.IsEnabled, m.CreatedAt, m.NextCheckAt);

    private readonly record struct MonitorValues(string Name, string Slug, string Url,
        int IntervalSeconds, int TimeoutSeconds, bool IsEnabled);
}

public sealed record MonitorRequest(string? Name, string? Slug, string? Url,
    int? IntervalSeconds, int? TimeoutSeconds, bool? IsEnabled);

public sealed record MonitorResponse(Guid Id, Guid OwnerId, string Name, string Slug, string Url,
    int IntervalSeconds, int TimeoutSeconds, bool IsEnabled, DateTimeOffset CreatedAt, DateTimeOffset? NextCheckAt);

public sealed record MonitorObservedStatusResponse(Guid MonitorId, string Name, bool IsEnabled, string Status,
    DateTimeOffset? LastCheckedAt, string? LastOutcome, int? LastHttpStatusCode, int? LastResponseTimeMs);
