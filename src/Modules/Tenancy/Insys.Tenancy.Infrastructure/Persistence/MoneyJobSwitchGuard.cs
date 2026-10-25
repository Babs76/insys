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
