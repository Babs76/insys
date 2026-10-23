using System.Collections.Concurrent;
using System.Net;
using Insys.IntegrationTests.TestModule;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// A freshly migrated database per test class: the operator and two tenants, all active, and the test module schema.
/// It also holds the class's API hosts: one per distinct set of settings, built and warmed once, and shared by the class's
/// tests (xUnit makes a new class instance per test, so a host made in InitializeAsync was one host per test).
/// </summary>
public sealed class DatabaseFixture(PostgresServer server) : IAsyncLifetime
{
    public static readonly Guid TenantA = TestTenants.A;
    public static readonly Guid TenantB = TestTenants.B;
    public static readonly Guid Operator = TestTenants.Operator;

    private readonly ConcurrentDictionary<string, Lazy<Task<InsysApiFactory>>> _hosts = new(StringComparer.Ordinal);

    public TestDatabase Database { get; private set; } = null!;

    public PostgresServer Server => server;

    public async ValueTask InitializeAsync()
    {
        Database = await server.CreateMigratedDatabase();
        await Database.SeedTenant(Operator, "OPERATOR");
        await Database.SeedTenant(TenantA, "TENA");
        await Database.SeedTenant(TenantB, "TENB");
        await TestModuleSchema.Create(Database);
    }

    /// <summary>
    /// The class's shared API host for these settings, as a fresh host would be between tests: caches emptied and stub
    /// recordings cleared (InsysApiFactory.ResetState). Tests that need a host of their own (other environments, a second
    /// instance) still make one. Fails if an earlier test left the host stopped or unhealthy.
    /// </summary>
    public async Task<InsysApiFactory> Host(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var key = settings is null ? "" : string.Join(";", settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));
        var factory = await _hosts.GetOrAdd(key, _ => new Lazy<Task<InsysApiFactory>>(() => new InsysApiFactory(Database, settings).Warm())).Value;

        (await factory.CreateClient().GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK, "an earlier test left the shared host unhealthy");
        factory.ResetState();
        return factory;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values.Where(h => h.IsValueCreated))
        {
            await (await host.Value).DisposeAsync();
        }
    }
}
