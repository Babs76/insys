using System.Reflection;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Infrastructure.StaticData;

/// <summary>The static data tables a module owns.</summary>
public sealed class StaticDataCatalogue<TDbContext>(IReadOnlyList<StaticDataDescriptor> descriptors)
    where TDbContext : ModuleDbContext
{
    public IReadOnlyList<StaticDataDescriptor> Descriptors { get; } = descriptors;

    public StaticDataDescriptor? Find(StaticRecordRef record) =>
        Descriptors.FirstOrDefault(d => d.Schema == record.Schema && d.Table == record.Table);
}

/// <summary>Every static data table in the platform, for platform-wide checks.</summary>
public sealed record ModuleStaticDataTables(string ModuleRoute, IReadOnlyList<StaticDataDescriptor> Descriptors);

public static class StaticDataRegistration
{
    /// <summary>Registers a module's generated static data tables, its lifecycle service and the approval outcome handlers.</summary>
    public static IServiceCollection AddStaticData<TDbContext>(this IServiceCollection services, string schema, IReadOnlyList<StaticDataDescriptor> descriptors)
        where TDbContext : ModuleDbContext
    {
        services.AddSingleton(new StaticDataCatalogue<TDbContext>(descriptors));
        services.AddSingleton(new ModuleStaticDataTables(descriptors.FirstOrDefault()?.ModuleRoute ?? schema, descriptors));
        services.AddScoped<StaticDataContextFactory<TDbContext>>();
        services.AddScoped<StaticDataService<TDbContext>>();
        services.AddIntegrationEventHandler<StaticDataChangeApproved, StaticDataApprovalHandler<TDbContext>, TDbContext>(schema);
        services.AddIntegrationEventHandler<StaticDataChangeRejected, StaticDataRejectionHandler<TDbContext>, TDbContext>(schema);
        return services;
    }
}

/// <summary>
/// Applies an approval decided by the Static Data module to a row this module owns (SDM-003). Every module
/// receives every decision and ignores the ones for other modules' tables.
/// </summary>
public sealed class StaticDataApprovalHandler<TDbContext>(StaticDataCatalogue<TDbContext> catalogue, StaticDataService<TDbContext> service)
    : IIntegrationEventHandler<StaticDataChangeApproved>
    where TDbContext : ModuleDbContext
{
    private static readonly MethodInfo Apply = typeof(StaticDataService<TDbContext>).GetMethod(nameof(StaticDataService<TDbContext>.ApplyApproval))!;

    public Task Handle(StaticDataChangeApproved integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) =>
        catalogue.Find(integrationEvent.Record) is { } descriptor
            ? (Task)Apply.MakeGenericMethod(descriptor.EntityType).Invoke(service, [descriptor, integrationEvent, cancellationToken])!
            : Task.CompletedTask;
}

/// <summary>Applies a rejection decided by the Static Data module: the version returns to draft.</summary>
public sealed class StaticDataRejectionHandler<TDbContext>(StaticDataCatalogue<TDbContext> catalogue, StaticDataService<TDbContext> service)
    : IIntegrationEventHandler<StaticDataChangeRejected>
    where TDbContext : ModuleDbContext
{
    private static readonly MethodInfo Apply = typeof(StaticDataService<TDbContext>).GetMethod(nameof(StaticDataService<TDbContext>.ApplyRejection))!;

    public Task Handle(StaticDataChangeRejected integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) =>
        catalogue.Find(integrationEvent.Record) is { } descriptor
            ? (Task)Apply.MakeGenericMethod(descriptor.EntityType).Invoke(service, [descriptor, integrationEvent, cancellationToken])!
            : Task.CompletedTask;
}
