/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
        services.AddIntegrationEventHandler<WidgetCreated, WidgetLedgerConsumer, TestDbContext>(TestDbContext.SchemaName);
        services.AddIntegrationEventHandler<WidgetRenamed, WidgetLedgerConsumer, TestDbContext>(TestDbContext.SchemaName);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public static async Task<Result<T>> Send<T>(this IServiceProvider services, Guid tenantId, ICommand<T> command, string actor = "test-user")
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForUser(tenantId, actor, [], null);
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command);
    }
}
