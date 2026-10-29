using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sentinel.Infrastructure.Persistence;
using Sentinel.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Sentinel.Api.Endpoints;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection()
    .SetApplicationName("SENTINEL")
    .PersistKeysToFileSystem(
        new DirectoryInfo(
            builder.Configuration["DataProtection:KeysPath"]
            ?? Path.Combine(
                builder.Environment.ContentRootPath,
                ".sentinel-keys")));

var connectionString =
    builder.Configuration.GetConnectionString("Sentinel")
    ?? throw new InvalidOperationException(
        "Connection string 'Sentinel' is missing.");

builder.Services.AddDbContext<SentinelDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services.AddAuthorization();

builder.Services
    .AddIdentityCore<SentinelUser>(options =>
    {
        options.User.RequireUniqueEmail = true;

        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;

        options.Lockout.DefaultLockoutTimeSpan =
            TimeSpan.FromMinutes(15);

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<SentinelDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "Sentinel.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode =
            StatusCodes.Status401Unauthorized;

        return Task.CompletedTask;
    };

    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode =
            StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    };
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-SENTINEL-CSRF";

    options.Cookie.Name = "Sentinel.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    var proxyAddress = builder.Configuration["ReverseProxy:TrustedIp"];

    if (!string.IsNullOrWhiteSpace(proxyAddress))
    {
        options.KnownProxies.Add(IPAddress.Parse(proxyAddress));
    }
});

builder.Services.Configure<SecurityStampValidatorOptions>(
    options =>
    {
        options.ValidationInterval = TimeSpan.FromMinutes(5);
    });
var app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "SENTINEL API",
    Timestamp = DateTimeOffset.UtcNow
}));

app.Run();
