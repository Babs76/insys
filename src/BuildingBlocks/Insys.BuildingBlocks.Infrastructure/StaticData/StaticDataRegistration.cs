/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using System.Reflection;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
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

/// <summary>
/// A module's rule for changes to one of its static data tables, checked when a draft is submitted: the proposed
/// version against the version it supersedes (none for a new code).
/// </summary>
public interface IStaticChangeGuard
{
    string Schema { get; }

    string Table { get; }

    Result Check(StaticRecord proposed, StaticRecord? current);
}

/// <summary>
/// A module's own rule across a static data table's fields (the generated input checks each field alone), checked when a
/// draft is created or edited and on import, so a record the database would refuse is a field error instead.
/// </summary>
public interface IStaticRecordValidator
{
    string Schema { get; }

    string Table { get; }

    /// <summary>Errors by JSON field name; empty when the record is valid.</summary>
    IReadOnlyDictionary<string, string[]> Validate(StaticRecord record);

    /// <summary>The rule in words, added to the create and edit operations' descriptions so the contract states it.</summary>
    string? Rules => null;
}

/// <summary>
/// A module's rule across the records of one table, checked when a draft is submitted: the proposed version against the
/// other codes' versions in force from its effective date (bands that must not overlap or leave gaps, say).
/// </summary>
public interface IStaticSetGuard
{
    string Schema { get; }

    string Table { get; }

    Result Check(StaticRecord proposed, IReadOnlyList<StaticRecord> others);
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
        services.AddIntegrationEventHandler<StaticDataImportApproved, StaticDataImportDecisions<TDbContext>, TDbContext>(schema, "Approved");
        services.AddIntegrationEventHandler<StaticDataImportNotApproved, StaticDataImportDecisions<TDbContext>, TDbContext>(schema, "NotApproved");
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

/// <summary>Applies the Static Data module's decision on an import submitted as one change (BR-016) to the lines this module owns.</summary>
public sealed class StaticDataImportDecisions<TDbContext>(StaticDataCatalogue<TDbContext> catalogue, StaticDataService<TDbContext> service)
    : IIntegrationEventHandler<StaticDataImportApproved>, IIntegrationEventHandler<StaticDataImportNotApproved>
    where TDbContext : ModuleDbContext
{
    private static readonly MethodInfo Approve = typeof(StaticDataService<TDbContext>).GetMethod(nameof(StaticDataService<TDbContext>.ApplyImportApproval))!;
    private static readonly MethodInfo Reject = typeof(StaticDataService<TDbContext>).GetMethod(nameof(StaticDataService<TDbContext>.ApplyImportRejection))!;

    public Task Handle(StaticDataImportApproved integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) =>
        catalogue.Find(integrationEvent.Record) is { } descriptor
            ? (Task)Approve.MakeGenericMethod(descriptor.EntityType).Invoke(service, [descriptor, integrationEvent, cancellationToken])!
            : Task.CompletedTask;

    public Task Handle(StaticDataImportNotApproved integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) =>
        catalogue.Find(integrationEvent.Record) is { } descriptor
            ? (Task)Reject.MakeGenericMethod(descriptor.EntityType).Invoke(service, [descriptor, integrationEvent, cancellationToken])!
            : Task.CompletedTask;
}
