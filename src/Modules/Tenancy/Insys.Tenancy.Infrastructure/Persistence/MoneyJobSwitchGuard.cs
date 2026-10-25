/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Tenancy.Infrastructure.StaticData;

namespace Insys.Tenancy.Infrastructure.Persistence;

/// <summary>
/// OQ-098: switching off a tenant's Billing or Payments daily job is a privileged function. A draft that disables either is
/// submitted only by a user who holds TENANT_SWITCH_OFF_MONEY_JOBS; its approval is a second person's, as for any Tier 1 row.
/// </summary>
internal sealed class MoneyJobSwitchGuard(ExecutionScope scope) : IStaticChangeGuard
{
    public const string Function = "TENANT_SWITCH_OFF_MONEY_JOBS";

    public static readonly IReadOnlySet<string> MoneyJobs = new HashSet<string>(StringComparer.Ordinal) { "BILLING_DAILY_JOBS", "PAYMENTS_DAILY_JOBS" };

    public string Schema => TenancyDbContext.SchemaName;

    public string Table => "cfg_tenant_function_availability";

    public Result Check(StaticRecord proposed, StaticRecord? current) =>
        proposed is TenantFunctionAvailability { Enabled: false } row && MoneyJobs.Contains(row.FunctionCode)
        && scope.Functions is { } functions && !functions.Usable.Contains(Function)
            ? Error.Forbidden(ErrorCodes.FunctionNotGranted, $"Switching off {row.FunctionCode} needs the privileged function {Function} (OQ-098).")
            : Result.Success();
}

/// <summary>States the OQ-098 rule in the table's create and edit operations; the guard enforces it when the version is submitted.</summary>
internal sealed class MoneyJobSwitchRule : IStaticRecordValidator
{
    public string Schema => TenancyDbContext.SchemaName;

    public string Table => "cfg_tenant_function_availability";

    public string Rules => "A function with no row is enabled. Submitting a version that switches off BILLING_DAILY_JOBS or PAYMENTS_DAILY_JOBS (enabled false) "
        + "needs the privileged function TENANT_SWITCH_OFF_MONEY_JOBS (403 FUNCTION_NOT_GRANTED otherwise; OQ-098). A JOB function switched off holds its "
        + "work: consumers park (FUNCTION_DISABLED) and daily jobs skip the tenant.";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record) => new Dictionary<string, string[]>();
}
