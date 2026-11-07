using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class ForwardedHeadersTests : IntegrationTestBase
{
    private static readonly IPAddress CaddyIp = IPAddress.Parse("172.30.77.10");
    private static readonly IPAddress UntrustedIp = IPAddress.Parse("172.30.77.20");

    [Fact]
    public async Task TrustedProxyForwardsClientIp()
    {
        using var factory = StartWithProxyConfiguration("true", CaddyIp.ToString());

        var context = await SendHealthAsync(factory, CaddyIp, forwardedFor: "198.51.100.10");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(IPAddress.Parse("198.51.100.10"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task UntrustedRemoteCannotForwardClientIp()
    {
        using var factory = StartWithProxyConfiguration("true", CaddyIp.ToString());

        var context = await SendHealthAsync(factory, UntrustedIp, forwardedFor: "198.51.100.10");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(UntrustedIp, context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task TrustedProxyForwardsScheme()
    {
        using var factory = StartWithProxyConfiguration("true", CaddyIp.ToString());

        var context = await SendHealthAsync(factory, CaddyIp, forwardedProto: "https");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Fact]
    public async Task UntrustedRemoteCannotForwardScheme()
    {
        using var factory = StartWithProxyConfiguration("true", CaddyIp.ToString());

        var context = await SendHealthAsync(factory, UntrustedIp, forwardedProto: "https");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task DefaultLoopbackProxiesAreNotTrusted(string remoteAddress)
    {
        using var factory = StartWithProxyConfiguration("true", CaddyIp.ToString());
        var remoteIp = IPAddress.Parse(remoteAddress);

        var context = await SendHealthAsync(
            factory,
            remoteIp,
            forwardedFor: "198.51.100.10",
            forwardedProto: "https"
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(remoteIp, context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task OnlyOneForwardedHopIsProcessed()
    {
        using var factory = StartWithProxyConfiguration("true", CaddyIp.ToString());

        var context = await SendHealthAsync(
            factory,
            CaddyIp,
            forwardedFor: "198.51.100.10, 203.0.113.20",
            forwardedProto: "http, https"
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(IPAddress.Parse("203.0.113.20"), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("172.30.77.10, 172.30.77.11")]
    public void EnabledProxyRequiresOneValidTrustedIp(string? trustedIp)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => StartWithProxyConfiguration("true", trustedIp)
        );

        Assert.Contains("ReverseProxy:TrustedIp", error.Message);
    }

    [Fact]
    public async Task DisabledProxyIgnoresForwardedHeadersEvenWithTrustedIp()
    {
        using var factory = StartWithProxyConfiguration("false", CaddyIp.ToString());

        var context = await SendHealthAsync(
            factory,
            CaddyIp,
            forwardedFor: "198.51.100.10",
            forwardedProto: "https"
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(CaddyIp, context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task AbsentProxyModeIgnoresForwardedHeaders()
    {
        using var factory = StartWithProxyConfiguration(null, CaddyIp.ToString());

        var context = await SendHealthAsync(
            factory,
            CaddyIp,
            forwardedFor: "198.51.100.10",
            forwardedProto: "https"
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(CaddyIp, context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    public void InvalidProxyModeFailsStartup(string enabled)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => StartWithProxyConfiguration(enabled, CaddyIp.ToString())
        );

        Assert.Contains("ReverseProxy:Enabled", error.Message);
    }

    private WebApplicationFactory<Program> StartWithProxyConfiguration(string? enabled, string? trustedIp)
    {
        const string enabledKey = "ReverseProxy__Enabled";
        const string trustedIpKey = "ReverseProxy__TrustedIp";
        var previousEnabled = Environment.GetEnvironmentVariable(enabledKey);
        var previousTrustedIp = Environment.GetEnvironmentVariable(trustedIpKey);

        // Program reads these settings before WebApplicationFactory's app configuration callback.
        // The test assembly disables parallel execution, and the values are restored before requests.
        try
        {
            Environment.SetEnvironmentVariable(enabledKey, enabled);
            Environment.SetEnvironmentVariable(trustedIpKey, trustedIp);

            var factory = Factory.WithWebHostBuilder(_ => { });

            try
            {
                _ = factory.Server;
                return factory;
            }
            catch
            {
                factory.Dispose();
                throw;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(enabledKey, previousEnabled);
            Environment.SetEnvironmentVariable(trustedIpKey, previousTrustedIp);
        }
    }

    private static Task<HttpContext> SendHealthAsync(
        WebApplicationFactory<Program> factory,
        IPAddress remoteIp,
        string? forwardedFor = null,
        string? forwardedProto = null
    ) => factory.Server.SendAsync(context =>
    {
        context.Connection.RemoteIpAddress = remoteIp;
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/health";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");

        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        if (forwardedProto is not null)
        {
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        }
    });
}
