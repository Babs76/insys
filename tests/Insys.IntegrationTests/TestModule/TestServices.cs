using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure;
using Insys.Integration.Infrastructure;
using Insys.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.IntegrationTests.TestModule;

/// <summary>Reads the tenant register as the app role.</summary>
public sealed class RegisterTenantDirectory(NpgsqlDataSource dataSource) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> GetActiveTenantIds(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id FROM tenant.tenant ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}

/// <summary>The building blocks, the Integration module and the test module, wired as the host wires modules.</summary>
public static class TestServices
{
    public static ServiceProvider Build(TestDatabase database, Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = TestTokens.HostSettings(database.AppConnectionString);
        settings["Outbox:MaxAttempts"] = "3";
        settings["Outbox:BaseBackoff"] = "00:00:00";
        configure?.Invoke(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInsysInfrastructure(configuration);
        services.AddSingleton<ITenantDirectory, RegisterTenantDirectory>();
        services.AddIntegrationInfrastructure();
        services.AddModuleDbContext<TestDbContext>(TestDbContext.SchemaName);
        services.AddModuleHandlers<TestDbContext>(typeof(TestDbContext).Assembly);
        services.AddDomainEventMapper<WidgetEventMapper>(TestDbContext.SchemaName);
        services.AddSingleton<ConsumerSwitch>();
        services.AddIntegrationEventHandler<WidgetCreated, WidgetCreatedConsumer, TestDbContext>(TestDbContext.SchemaName);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public static async Task<Result<T>> Send<T>(this IServiceProvider services, Guid tenantId, ICommand<T> command, string actor = "test-user")
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForUser(tenantId, actor, [], null);
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command);
    }
}
