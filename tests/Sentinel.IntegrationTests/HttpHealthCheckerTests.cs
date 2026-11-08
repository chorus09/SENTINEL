using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Sentinel.Application.Monitoring;
using Sentinel.Infrastructure.Monitoring;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;
using Xunit;

namespace Sentinel.IntegrationTests;

public sealed class HttpHealthCheckerTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("8.8.8.8");

    [Theory]
    [InlineData(200, HttpHealthCheckOutcome.Healthy)]
    [InlineData(404, HttpHealthCheckOutcome.HttpError)]
    [InlineData(500, HttpHealthCheckOutcome.HttpError)]
    [InlineData(302, HttpHealthCheckOutcome.HttpError)]
    public async Task ReportsStatusWithoutFollowingRedirectOrReturningBody(int status, HttpHealthCheckOutcome expected)
    {
        using var server = new OneRequestServer(status,
            "Location: http://169.254.169.254/latest/meta-data/\r\n", "private-response-body");
        var checker = WithServer(server);
        var result = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.StatusCode);
        Assert.True(result.Duration >= TimeSpan.Zero);
        Assert.Equal(1, server.RequestCount);
        Assert.Equal("GET /check HTTP/1.1", server.RequestLine);
        Assert.Contains("Host: example.com", server.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization:", server.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie:", server.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-response-body", result.ToString());
        Assert.Equal(3, typeof(HttpHealthCheckResult).GetProperties().Length);
    }

    [Fact]
    public async Task TimeoutIsReported()
    {
        var checker = new HttpHealthChecker(ResolvePublic, async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        });
        var result = await checker.CheckAsync(Monitor(timeout: 1), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.Timeout, result.Outcome);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task DnsFailureIsReported()
    {
        var checker = new HttpHealthChecker((_, _) =>
            Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
            FailIfConnected);
        var result = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.DnsFailure, result.Outcome);
    }

    [Fact]
    public async Task EmptyDnsAnswerFailsClosed()
    {
        var checker = new HttpHealthChecker((_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            FailIfConnected);
        var result = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.DnsFailure, result.Outcome);
    }

    [Fact]
    public async Task ConnectionFailureIsReported()
    {
        var checker = new HttpHealthChecker(ResolvePublic,
            (_, _, _) => ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)));
        var result = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.ConnectionFailure, result.Outcome);
    }

    [Fact]
    public async Task OtherNetworkFailureIsReportedWithoutExceptionDetails()
    {
        var checker = new HttpHealthChecker(ResolvePublic,
            (_, _, _) => ValueTask.FromException<Stream>(new HttpRequestException("sensitive-network-detail")));
        var result = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.NetworkFailure, result.Outcome);
        Assert.DoesNotContain("sensitive-network-detail", result.ToString());
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:password@example.com")]
    [InlineData("http://localhost/")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("http://node.local/")]
    public async Task InvalidUrlOrLocalHostnameIsRejectedBeforeDns(string url)
    {
        var checker = new HttpHealthChecker((_, _) => throw new Exception("DNS must not run"), FailIfConnected);
        var result = await checker.CheckAsync(Monitor(url), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.UnsafeDestination, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.DoesNotContain("password", result.ToString());
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[::ffff:192.168.1.1]/")]
    public async Task UnsafeLiteralAddressIsRejectedBeforeDns(string url)
    {
        var checker = new HttpHealthChecker((_, _) => throw new Exception("DNS must not run"), FailIfConnected);
        var result = await checker.CheckAsync(Monitor(url), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.UnsafeDestination, result.Outcome);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.0.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("192.31.196.1")]
    [InlineData("192.52.193.1")]
    [InlineData("192.175.48.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:c0a8:0101::")]
    [InlineData("2620:4f:8000::1")]
    public void SpecialUseAddressesAreBlocked(string literal)
    {
        Assert.False(PublicDestinationPolicy.IsPublicAddress(IPAddress.Parse(literal)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreEligible(string literal)
    {
        Assert.True(PublicDestinationPolicy.IsPublicAddress(IPAddress.Parse(literal)));
    }

    [Fact]
    public async Task PublicIpv6LiteralCanBeChecked()
    {
        using var server = new OneRequestServer(200);
        var expected = IPAddress.Parse("2606:4700:4700::1111");
        var checker = new HttpHealthChecker((_, _) => Task.FromResult(new[] { expected }),
            async (address, _, ct) =>
            {
                Assert.Equal(expected, address);
                return await ConnectToServerAsync(server, ct);
            });
        var result = await checker.CheckAsync(Monitor("http://[2606:4700:4700::1111]/check"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.Healthy, result.Outcome);
        Assert.Contains("Host: [2606:4700:4700::1111]", server.RequestText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MixedDnsAnswerFailsClosedBeforeConnection()
    {
        var checker = new HttpHealthChecker((_, _) => Task.FromResult(new[]
            { PublicAddress, IPAddress.Parse("127.0.0.1") }), FailIfConnected);
        var result = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.UnsafeDestination, result.Outcome);
    }

    [Fact]
    public async Task DnsRebindingIsCheckedForEachNewConnection()
    {
        using var server = new OneRequestServer(200);
        var lookups = 0;
        var connections = 0;
        var checker = new HttpHealthChecker((_, _) => Task.FromResult(new[]
        {
            Interlocked.Increment(ref lookups) == 1 ? PublicAddress : IPAddress.Loopback,
        }), async (ip, port, ct) =>
        {
            Interlocked.Increment(ref connections);
            Assert.Equal(PublicAddress, ip);
            return await ConnectToServerAsync(server, ct);
        });

        var first = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        var second = await checker.CheckAsync(Monitor(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.Healthy, first.Outcome);
        Assert.Equal(HttpHealthCheckOutcome.UnsafeDestination, second.Outcome);
        Assert.Equal(2, lookups);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task UntrustedTlsCertificateIsRejected()
    {
        using var certificate = CreateSelfSignedCertificate();
        using var server = new OneRequestServer(200, tlsCertificate: certificate);
        var checker = WithServer(server);
        var result = await checker.CheckAsync(Monitor("https://example.com/check"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpHealthCheckOutcome.NetworkFailure, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.True(server.TlsHandshakeAttempted);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var checker = new HttpHealthChecker(ResolvePublic, async (_, _, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checker.CheckAsync(Monitor(), cancellation.Token));
    }

    private static HttpHealthChecker WithServer(OneRequestServer server) =>
        new(ResolvePublic, async (ip, _, ct) =>
        {
            Assert.Equal(PublicAddress, ip);
            return await ConnectToServerAsync(server, ct);
        });

    private static Task<IPAddress[]> ResolvePublic(string _, CancellationToken ct) =>
        Task.FromResult(new[] { PublicAddress });

    private static ValueTask<Stream> FailIfConnected(IPAddress _, int __, CancellationToken ___) =>
        throw new Exception("Unsafe destination reached the connector.");

    private static async ValueTask<Stream> ConnectToServerAsync(OneRequestServer server, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Port, ct);
            return new NetworkStream(client.Client, ownsSocket: true);
        }
        catch { client.Dispose(); throw; }
    }

    private static MonitorEntity Monitor(string url = "http://example.com/check", int timeout = 5) =>
        new() { Url = url, TimeoutSeconds = timeout };

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("example.com");
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class OneRequestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;
        private int _requestCount;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int RequestCount => _requestCount;
        public string? RequestLine { get; private set; }
        public string? RequestText { get; private set; }
        public bool TlsHandshakeAttempted { get; private set; }

        public OneRequestServer(int status, string extraHeaders = "", string body = "",
            X509Certificate2? tlsCertificate = null)
        {
            _listener.Start();
            _serve = ServeAsync(status, extraHeaders, body, tlsCertificate);
        }

        private async Task ServeAsync(int status, string extraHeaders, string body, X509Certificate2? certificate)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var raw = client.GetStream();
                Stream stream = raw;
                if (certificate is not null)
                {
                    var tls = new SslStream(raw, leaveInnerStreamOpen: true);
                    TlsHandshakeAttempted = true;
                    try { await tls.AuthenticateAsServerAsync(certificate); }
                    catch (System.Security.Authentication.AuthenticationException) { return; }
                    stream = tls;
                }
                var buffer = new byte[4096];
                using var headers = new MemoryStream();
                while (headers.Length < 16_384)
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) return;
                    headers.Write(buffer, 0, read);
                    if (Encoding.ASCII.GetString(headers.GetBuffer(), 0, (int)headers.Length)
                        .Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                RequestText = Encoding.ASCII.GetString(headers.GetBuffer(), 0, (int)headers.Length);
                RequestLine = RequestText.Split("\r\n")[0];
                Interlocked.Increment(ref _requestCount);
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 {status} Test\r\n{extraHeaders}Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes);
                await stream.FlushAsync();
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        public void Dispose()
        {
            _listener.Stop();
            _ = _serve;
        }
    }
}
