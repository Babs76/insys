using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure;
using Insys.Audit.Infrastructure;
using Insys.Integration.Infrastructure;
using Insys.Tenancy.Infrastructure;
using Insys.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests.TestModule;

/// <summary>The building blocks, the Tenancy, Audit and Integration modules and the test module, wired as the host wires modules.</summary>
public static class TestServices
{
    public static ServiceProvider Build(TestDatabase database, Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = TestTokens.HostSettings(database);
        settings["Outbox:MaxAttempts"] = "3";
        settings["Outbox:BaseBackoff"] = "00:00:00";
        configure?.Invoke(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInsysInfrastructure(configuration);
        services.AddTenancyInfrastructure();
        services.AddAuditInfrastructure();
        services.AddIntegrationInfrastructure();
        services.AddModuleDbContext<TestDbContext>(TestDbContext.SchemaName, "TestModule");
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
