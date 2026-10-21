using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Insys.Testing;

/// <summary>The real API host (Program) against a migrated test database, connecting as the non-owner role.</summary>
public sealed class InsysApiFactory(TestDatabase database) : WebApplicationFactory<Program>
{
    public TestDatabase Database { get; } = database;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(TestTokens.HostSettings(Database.AppConnectionString)));
    }

    public HttpClient ClientFor(Guid? tenantId, string subject, params string[] permissions)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(tenantId, subject, permissions));
        return client;
    }
}
