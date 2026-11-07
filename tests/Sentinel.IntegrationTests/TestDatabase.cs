using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.IntegrationTests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private const string ExpectedDatabase = "sentinel_test_db";
    private const long AdvisoryLockKey = 0x53454E54494E454C;

    private readonly string _connectionString;
    private readonly NpgsqlConnection _lockConnection;
    private bool _lockHeld;

    private TestDatabase(string connectionString, NpgsqlConnection lockConnection)
    {
        _connectionString = connectionString;
        _lockConnection = lockConnection;
    }

    internal static async Task<TestDatabase> OpenAndPrepareAsync(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);

        if (!string.Equals(target.Database, ExpectedDatabase, StringComparison.Ordinal)
            || !string.Equals(target.Host, "postgres", StringComparison.Ordinal)
            || target.Port != 5432
            || string.IsNullOrWhiteSpace(target.Username)
            || string.IsNullOrWhiteSpace(target.Password))
        {
            throw new InvalidOperationException("The integration test database target is invalid.");
        }

        target.Pooling = false;
        var database = new TestDatabase(connectionString, new NpgsqlConnection(target.ConnectionString));

        try
        {
            await database.PrepareAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private async Task PrepareAsync()
    {
        await _lockConnection.OpenAsync();

        await using (var verify = new NpgsqlCommand("SELECT current_database()", _lockConnection))
        {
            var actualDatabase = (string?)await verify.ExecuteScalarAsync();

            if (!string.Equals(actualDatabase, ExpectedDatabase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The connected database is not sentinel_test_db.");
            }
        }

        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", _lockConnection))
        {
            acquire.Parameters.AddWithValue("key", AdvisoryLockKey);
            await acquire.ExecuteNonQueryAsync();
            _lockHeld = true;
        }

        await AssertLockHeldAsync();

        await using (var transaction = await _lockConnection.BeginTransactionAsync())
        {
            await using (var drop = new NpgsqlCommand("DROP SCHEMA IF EXISTS public CASCADE", _lockConnection, transaction))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await using (var create = new NpgsqlCommand("CREATE SCHEMA public", _lockConnection, transaction))
            {
                await create.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var options = new DbContextOptionsBuilder<SentinelDbContext>()
            .UseNpgsql(_lockConnection)
            .Options;

        await using var db = new SentinelDbContext(options);
        await AssertLockHeldAsync();
        await db.Database.MigrateAsync();
        await AssertLockHeldAsync();
    }

    internal async Task AssertLockHeldAsync()
    {
        if (!_lockHeld || _lockConnection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("The integration test advisory lock connection was lost.");
        }

        await using (var verify = new NpgsqlCommand("SELECT current_database()", _lockConnection))
        {
            if (!string.Equals((string?)await verify.ExecuteScalarAsync(), ExpectedDatabase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The advisory lock connection changed database.");
            }
        }

        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync();

        await using var probe = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", observer);
        probe.Parameters.AddWithValue("key", AdvisoryLockKey);

        if ((bool)(await probe.ExecuteScalarAsync() ?? false))
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", observer);
            release.Parameters.AddWithValue("key", AdvisoryLockKey);
            await release.ExecuteNonQueryAsync();
            throw new InvalidOperationException("The integration test advisory lock was not held.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_lockHeld && _lockConnection.State == System.Data.ConnectionState.Open)
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _lockConnection);
                release.Parameters.AddWithValue("key", AdvisoryLockKey);
                await release.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            _lockHeld = false;
            await _lockConnection.DisposeAsync();
        }
    }
}
