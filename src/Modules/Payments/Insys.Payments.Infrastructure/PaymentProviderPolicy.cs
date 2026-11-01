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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Payments.Application;
using Insys.Payments.Domain.Payments;
using Insys.Payments.Infrastructure.Persistence;
using Insys.Payments.Infrastructure.StaticData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Insys.Payments.Infrastructure;

/// <summary>
/// The simulator is allowed in Development and Testing, and elsewhere only where the deployment declares itself
/// non-production (<see cref="DeclaredKey"/>), which Production can't do. Never by default.
/// </summary>
internal sealed class PaymentProviderPolicy(IConfiguration configuration, IHostEnvironment environment) : IPaymentProviderPolicy
{
    public const string DeclaredKey = "Payments:Simulator:DeclaredNonProduction";

    public bool Declared => configuration.GetValue<bool>(DeclaredKey);

    public bool Allows(string providerCode) => providerCode != SimulatorGateway.Code
        || environment.IsDevelopment() || environment.IsEnvironment("Testing") || (Declared && !environment.IsProduction());
}

/// <summary>Setting a tenant's provider to the simulator is refused where it isn't allowed.</summary>
internal sealed class PaymentProviderValidator(IPaymentProviderPolicy policy) : IStaticRecordValidator
{
    public string Schema => "payment";

    public string Table => "mst_payment_provider_and_bank_register";

    public string Rules => $"{SimulatorGateway.Code} moves no money, and is refused outside Development and Testing unless the deployment is declared non-production.";

    public string? RefusalCode => PaymentErrorCodes.ProviderNotAllowed;

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record) =>
        record is PaymentProviderAndBankRegister { ProviderCode: var code } && !policy.Allows(code)
            ? new Dictionary<string, string[]> { ["providerCode"] = [$"{code} is not allowed in this environment."] }
            : new Dictionary<string, string[]>();
}

/// <summary>The service won't start where a tenant's configuration names the simulator and it isn't allowed, or where Production claims to be non-production.</summary>
internal sealed class SimulatorGuard(IPaymentProviderPolicy policy, IServiceProvider services, IHostEnvironment environment, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsProduction() && configuration.GetValue<bool>(PaymentProviderPolicy.DeclaredKey))
        {
            throw new InvalidOperationException($"{PaymentProviderPolicy.DeclaredKey} is set in the Production environment. Production can't be declared non-production; remove it.");
        }

        if (policy.Allows(SimulatorGateway.Code))
        {
            return;
        }

        var dataSource = services.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsDbContext.SchemaName);
        var naming = new List<Guid>();
        foreach (var tenant in await services.GetRequiredService<ITenantDirectory>().GetActiveTenantIds(cancellationToken))
        {
            var rows = await ReadinessSql.InTenant(dataSource, tenant, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
                "SELECT code FROM payment.mst_payment_provider_and_bank_register WHERE tenant_id = @t AND provider_code = @p AND record_status <> 'RETIRED' "
                + "UNION ALL SELECT id::text FROM payment.callback_endpoint WHERE tenant_id = @t AND provider_code = @p AND status <> 'REVOKED'", cancellationToken,
                ("t", tenant), ("p", SimulatorGateway.Code)), cancellationToken);
            if (rows.Count > 0)
            {
                naming.Add(tenant);
            }
        }

        if (naming.Count > 0)
        {
            throw new InvalidOperationException($"{SimulatorGateway.Code} is configured for tenants {string.Join(", ", naming)} in the {environment.EnvironmentName} environment. "
                + $"It moves no money and is allowed only in Development and Testing, or where {PaymentProviderPolicy.DeclaredKey} declares the deployment non-production.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
