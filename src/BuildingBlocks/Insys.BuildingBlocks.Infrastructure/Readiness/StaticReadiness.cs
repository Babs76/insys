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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Readiness;

/// <summary>
/// A readiness check that some static data is in force in the tenant on the day (onboarding readiness §2): versions with
/// an in-force status of the table's lifecycle and an effective period covering the day, matching <see cref="Where"/>.
/// <see cref="GlobalRows"/> counts global rows too, for tables a tenant may inherit (calendars, authentication policies).
/// </summary>
public sealed record StaticCheck(string Step, string Code, string Table, string What, string? Where = null, bool GlobalRows = false, int Minimum = 1);

/// <summary>Runs SQL for readiness on a module's own connection, in the tenant (row-level security applies).</summary>
public static class ReadinessSql
{
    public static async Task<T> InTenant<T>(NpgsqlDataSource dataSource, Guid tenantId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        var result = await work(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public static async Task<long> Count(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<IReadOnlyList<string>> Strings(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        }

        return values;
    }

    /// <summary>The in-force condition for a static table's versions on @day (with @inForce, the lifecycle's in-force statuses).</summary>
    public const string InForce = "record_status = ANY(@inForce) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)";
}

internal sealed class StaticReadinessCheck(NpgsqlDataSource dataSource, IReadOnlyList<StaticDataDescriptor> descriptors, IReadOnlyList<StaticCheck> checks)
    : IReadinessCheck
{
    public Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant<IReadOnlyList<ReadinessResult>>(dataSource, context.TenantId, async (connection, transaction) =>
        {
            var results = new List<ReadinessResult>();
            foreach (var check in checks)
            {
                var descriptor = descriptors.Single(d => $"{d.Schema}.{d.Table}" == check.Table);
                var owner = check.GlobalRows ? "(tenant_id = @t OR tenant_id IS NULL)" : "tenant_id = @t";
                var count = await ReadinessSql.Count(connection, transaction,
                    $"SELECT count(*) FROM {check.Table} WHERE {owner} AND {ReadinessSql.InForce}{(check.Where is null ? "" : $" AND ({check.Where})")}",
                    cancellationToken, ("t", context.TenantId), ("inForce", descriptor.Lifecycle.InForce), ("day", context.Day));
                results.Add(ReadinessResult.Of(check.Step, check.Code, check.What, count >= check.Minimum ? [] : [check.Table]));
            }

            return results;
        }, cancellationToken);
}

internal sealed class SqlReadinessCheck(NpgsqlDataSource dataSource,
    Func<ReadinessContext, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<IReadOnlyList<ReadinessResult>>> check) : IReadinessCheck
{
    public Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => check(context, connection, transaction, cancellationToken), cancellationToken);
}

public static class ReadinessRegistration
{
    /// <summary>Registers a module's own readiness check, run in the tenant on the module's own connection.</summary>
    public static IServiceCollection AddSqlReadiness(this IServiceCollection services, string schema,
        Func<ReadinessContext, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<IReadOnlyList<ReadinessResult>>> check) =>
        services.AddSingleton<IReadinessCheck>(sp => new SqlReadinessCheck(sp.GetRequiredKeyedService<NpgsqlDataSource>(schema), check));

    /// <summary>Registers a module's static-data readiness checks, run on its own connection (the data source keyed by its schema).</summary>
    public static IServiceCollection AddStaticReadiness(this IServiceCollection services, string schema, IReadOnlyList<StaticDataDescriptor> descriptors,
        params StaticCheck[] checks) =>
        services.AddSingleton<IReadinessCheck>(sp => new StaticReadinessCheck(sp.GetRequiredKeyedService<NpgsqlDataSource>(schema), descriptors, checks));
}
