using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Monitoring;
using MonitorEntity = Sentinel.Domain.Entities.Monitor;

namespace Sentinel.Infrastructure.Monitoring;

public sealed class HttpHealthChecker : IHttpHealthChecker
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPAddress, int, CancellationToken, ValueTask<Stream>> _connect;

    public HttpHealthChecker() : this(
        (host, ct) => Dns.GetHostAddressesAsync(host, ct),
        ConnectSocketAsync)
    {
    }

    // Test seam: both doubles still pass through the same destination policy.
    internal HttpHealthChecker(
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect)
    {
        _resolve = resolve;
        _connect = connect;
    }

    public async Task<HttpHealthCheckResult> CheckAsync(MonitorEntity monitor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();

        if (!Uri.TryCreate(monitor.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !PublicDestinationPolicy.IsSafeHost(uri.DnsSafeHost)
            || monitor.TimeoutSeconds <= 0)
            return Result(HttpHealthCheckOutcome.UnsafeDestination, null, clock);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(monitor.TimeoutSeconds));
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = ConnectValidatedAsync,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        try
        {
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            return Result(status is >= 200 and <= 299
                ? HttpHealthCheckOutcome.Healthy : HttpHealthCheckOutcome.HttpError, status, clock);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(HttpHealthCheckOutcome.Timeout, null, clock);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or UnsafeDestinationException or DestinationDnsException)
        {
            var outcome = HasCause<UnsafeDestinationException>(ex) ? HttpHealthCheckOutcome.UnsafeDestination
                : HasCause<DestinationDnsException>(ex) ? HttpHealthCheckOutcome.DnsFailure
                : HasCause<SocketException>(ex) ? HttpHealthCheckOutcome.ConnectionFailure
                : HttpHealthCheckOutcome.NetworkFailure;
            return Result(outcome, null, clock);
        }

        async ValueTask<Stream> ConnectValidatedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var host = context.DnsEndPoint.Host;
            if (!PublicDestinationPolicy.IsSafeHost(host)) throw new UnsafeDestinationException();
            IPAddress[] addresses;
            try { addresses = await _resolve(host, ct); }
            catch (SocketException ex) { throw new DestinationDnsException(ex); }
            if (addresses.Length == 0) throw new DestinationDnsException();
            if (addresses.Any(address => !PublicDestinationPolicy.IsPublicAddress(address)))
                throw new UnsafeDestinationException();
            // Connect by IP, never by hostname: there is no second DNS lookup.
            return await _connect(addresses[0], context.DnsEndPoint.Port, ct);
        }
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    private static bool HasCause<T>(Exception ex) where T : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is T) return true;
        return false;
    }

    private static HttpHealthCheckResult Result(HttpHealthCheckOutcome outcome, int? status, Stopwatch clock) =>
        new(outcome, status, clock.Elapsed);

    private sealed class UnsafeDestinationException : Exception;
    private sealed class DestinationDnsException(Exception? inner = null) : Exception(null, inner);
}

public static class HttpHealthCheckRegistration
{
    public static IServiceCollection AddHttpHealthChecks(this IServiceCollection services) =>
        services.AddSingleton<IHttpHealthChecker, HttpHealthChecker>();
}
