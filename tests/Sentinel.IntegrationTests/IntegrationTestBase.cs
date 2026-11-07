using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sentinel.IntegrationTests;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    private TestDatabase? _database;
    private SentinelApiFactory? _factory;
    private HttpClient? _client;

    private protected TestDatabase Database => _database
        ?? throw new InvalidOperationException("The test database is not initialized.");

    private protected SentinelApiFactory Factory => _factory
        ?? throw new InvalidOperationException("The test API is not initialized.");

    protected HttpClient Client => _client
        ?? throw new InvalidOperationException("The test HTTP client is not initialized.");

    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Sentinel")
            ?? throw new InvalidOperationException("The test connection string is missing.");
        var keysPath = Environment.GetEnvironmentVariable("DataProtection__KeysPath")
            ?? throw new InvalidOperationException("The test Data Protection keys path is missing.");

        _database = await TestDatabase.OpenAndPrepareAsync(connectionString);

        try
        {
            Directory.CreateDirectory(keysPath);
            _factory = new SentinelApiFactory(connectionString, keysPath);
            _client = CreateCookieClient();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    protected HttpClient CreateCookieClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false,
    });

    public async ValueTask DisposeAsync()
    {
        try
        {
            _client?.Dispose();
            _factory?.Dispose();
        }
        finally
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
                _database = null;
            }
        }
    }
}
