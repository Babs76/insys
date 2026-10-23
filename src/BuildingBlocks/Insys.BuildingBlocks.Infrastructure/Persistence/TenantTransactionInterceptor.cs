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
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Runs <c>SELECT set_config('app.tenant_id', @tenantId, true)</c> as the first statement of every transaction, so
/// row-level security sees only the current tenant (DBA-001 to DBA-003). With no tenant it throws, and with no
/// transaction the policy's current_setting call fails, so data is never returned without a tenant.
/// </summary>
public sealed class TenantTransactionInterceptor : DbTransactionInterceptor
{
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await SetTenant(connection, result, eventData, cancellationToken);
        return result;
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result) =>
        throw new NotSupportedException("Begin transactions asynchronously.");

    private static async Task SetTenant(DbConnection connection, DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        var context = eventData.Context as ModuleDbContext
            ?? throw new InvalidOperationException("Tenant transactions are only supported on module DbContexts.");
        var tenantId = context.CurrentTenantId;
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
    }
}

public static class TenantSession
{
    public const string SetTenantSql = "SELECT set_config('app.tenant_id', @tenantId, true)";

    public static async Task SetTenant(DbConnection connection, DbTransaction transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SetTenantSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenantId";
        parameter.Value = tenantId.ToString();
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
