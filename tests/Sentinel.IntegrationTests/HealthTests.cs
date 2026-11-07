using System.Net;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]

namespace Sentinel.IntegrationTests;

public sealed class HealthTests : IntegrationTestBase
{
    [Fact]
    public async Task GetHealthReturnsServiceIdentifier()
    {
        await Database.AssertLockHeldAsync();
        using var response = await Client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("SENTINEL API", document.RootElement.GetProperty("service").GetString());
        await Database.AssertLockHeldAsync();
    }
}
