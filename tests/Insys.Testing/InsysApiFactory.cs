using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Testing;

/// <summary>The real API host (Program) against a migrated test database, connecting as the non-owner role, with any extra settings.</summary>
public sealed class InsysApiFactory(TestDatabase database, IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public TestDatabase Database { get; } = database;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(TestTokens.HostSettings(Database)).AddInMemoryCollection(settings ?? new Dictionary<string, string?>()));
    }

    /// <summary>A client whose token carries only sub and tenant_id.</summary>
    public HttpClient ClientFor(Guid? tenantId, string subject)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(tenantId, subject));
        return client;
    }

    /// <summary>Publishes every pending outbox event to its consumers now (the relay is off in tests).</summary>
    /// <summary>Relays outbox events until none are left, so chains of events (a consumer publishing in turn) complete.</summary>
    public async Task<int> DrainOutbox()
    {
        var processor = Services.GetRequiredService<Insys.BuildingBlocks.Infrastructure.Messaging.OutboxProcessor>();
        var total = 0;
        for (var pass = 0; pass < 10; pass++)
        {
            var relayed = await processor.ProcessOnce(CancellationToken.None);
            if (relayed == 0)
            {
                break;
            }

            total += relayed;
        }

        return total;
    }
}
