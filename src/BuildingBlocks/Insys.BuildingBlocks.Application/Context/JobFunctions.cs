/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using System.Collections.Concurrent;

namespace Insys.BuildingBlocks.Application.Context;

/// <summary>
/// Whether a tenant may run a JOB function now (ADR-026 §2): in force in the catalogue, not disabled by the tenant, and
/// the tenant entitled to its feature. Implemented by Identity, which keeps the catalogue.
/// </summary>
public interface IFunctionAvailability
{
    Task<bool> IsEnabled(Guid tenantId, string functionCode, CancellationToken cancellationToken);

    /// <summary>The JOB functions the tenant has switched off (OQ-098), for the operations view and /health detail.</summary>
    Task<IReadOnlyList<string>> JobsSwitchedOff(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The JOB function each module's event consumers run under (ADR-026 §2): a delivery for a tenant that disabled it parks
/// (FUNCTION_DISABLED). Platform consumers (identity, tenancy, static data, audit, workflow, integration) have none, so a
/// tenant cannot switch off what keeps the platform consistent.
/// </summary>
public static class JobFunctions
{
    private static readonly ConcurrentDictionary<string, string> Consumers = new(StringComparer.Ordinal);

    public static void ForConsumers(string schema, string functionCode) => Consumers[schema] = functionCode;

    public static string? OfConsumers(string schema) => Consumers.GetValueOrDefault(schema);

    public static IReadOnlyDictionary<string, string> All => Consumers;
}

public static class JobTenants
{
    /// <summary>
    /// The active tenants that run the job's function now (ADR-026 §2). A tenant that disabled it is skipped and logged;
    /// its due work stays due, and the job picks it up once the function is enabled again.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> Running(this ITenantDirectory tenants, IFunctionAvailability availability, string functionCode,
        Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        var running = new List<Guid>();
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            if (await availability.IsEnabled(tenantId, functionCode, cancellationToken))
            {
                running.Add(tenantId);
            }
            else
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Skipped tenant {TenantId}: function {FunctionCode} is disabled", tenantId, functionCode);
            }
        }

        return running;
    }
}
