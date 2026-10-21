using Insys.IntegrationTests.TestModule;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>A freshly migrated database per test class, with two tenants and the test module schema.</summary>
public sealed class DatabaseFixture(PostgresServer server) : IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("0199f000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("0199f000-0000-7000-8000-00000000000b");

    public TestDatabase Database { get; private set; } = null!;

    public PostgresServer Server => server;

    public async ValueTask InitializeAsync()
    {
        Database = await server.CreateMigratedDatabase();
        await Database.SeedTenant(TenantA, "TENA");
        await Database.SeedTenant(TenantB, "TENB");
        await TestModuleSchema.Create(Database);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
