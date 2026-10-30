/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.Identity.Contracts;
using Insys.Payments.Contracts;
using Insys.Payments.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Payments.Infrastructure;

/// <summary>
/// Onboarding readiness PAYMENT_CALLBACK_CLIENT (PAYMENTS_AND_DISTRIBUTION), a blocker: an endpoint that requires a client token
/// refuses every callback until a service identity holds the function that lets its token through (ADR-023 decision 4).
/// IAM records no kind for a service identity, so this asks for an active user holding the function; it is privileged and in no
/// role template, so only a role made for the purpose grants it.
/// </summary>
internal sealed class CallbackClientReadiness([FromKeyedServices(PaymentsDbContext.SchemaName)] NpgsqlDataSource dataSource, IUserRoleQuery roles) : IReadinessCheck
{
    public IReadOnlyCollection<string> Steps { get; } = ["PAYMENTS_AND_DISTRIBUTION"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var requiring = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
            "SELECT DISTINCT provider_code FROM payment.callback_endpoint WHERE tenant_id = @t AND require_client_token AND status <> 'REVOKED' ORDER BY 1", cancellationToken,
            ("t", context.TenantId)), cancellationToken);
        var held = requiring.Count == 0 || await roles.AnyActiveHolder(context.TenantId, PaymentFunctionCodes.CallbackClient, context.Day, cancellationToken);
        return
        [
            ReadinessResult.Of("PAYMENTS_AND_DISTRIBUTION", "PAYMENT_CALLBACK_CLIENT",
                $"A service identity holding {PaymentFunctionCodes.CallbackClient}, for each provider whose callback endpoint requires a client token",
                held ? [] : requiring),
        ];
    }
}
