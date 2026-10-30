/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Finance.Contracts;
using Insys.Finance.Infrastructure.Persistence;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Finance.Infrastructure;

/// <summary>
/// Onboarding readiness POSTING_RULES: a tenant entitled to HEALTH needs a rule for a reversed claim, or each reversal waits as a
/// posting exception. CLAIM_REVERSAL_ACCOUNTS, a warning, not a blocker: a reversal rule crediting an account no CLAIM_PAID rule
/// debits will not net the claims paid to zero, and finance should see it.
/// </summary>
internal sealed class HealthReversalPostingReadiness([FromKeyedServices(FinanceDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantFunctionSettings settings) : IReadinessCheck
{
    private static readonly string[] RecoveryTypes =
    [
        TransactionTypes.ProviderRecoveryRaised, TransactionTypes.ProviderRecoveryOffset, TransactionTypes.ProviderRecoveryRepaid, TransactionTypes.ProviderRecoveryWrittenOff,
        TransactionTypes.ProviderRecoveryReversed, TransactionTypes.ProviderRecoverySettled,
    ];

    public IReadOnlyCollection<string> Steps { get; } = ["FINANCIAL_FOUNDATIONS"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        if (!(await settings.For(context.TenantId, context.Day, cancellationToken)).EntitledFeatures.Contains("HEALTH"))
        {
            return [];
        }

        var (rules, paidDebits, reversalCredits, recoveryRules) = await ReadinessSql.InTenant(dataSource, context.TenantId, async (connection, transaction) =>
        {
            Task<IReadOnlyList<string>> Accounts(string column, string type) => ReadinessSql.Strings(connection, transaction,
                $"SELECT DISTINCT {column} FROM finance.cfg_posting_rules WHERE tenant_id = @t AND transaction_type = @type AND {ReadinessSql.InForce} ORDER BY 1", cancellationToken,
                ("t", context.TenantId), ("type", type), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day));
            var credits = await Accounts("credit_account", TransactionTypes.ClaimPaidReversed);
            var recoveryRules = await ReadinessSql.Strings(connection, transaction,
                $"SELECT DISTINCT transaction_type FROM finance.cfg_posting_rules WHERE tenant_id = @t AND transaction_type = ANY(@types) AND {ReadinessSql.InForce}", cancellationToken,
                ("t", context.TenantId), ("types", RecoveryTypes), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day));
            return (credits.Count, await Accounts("debit_account", TransactionTypes.ClaimPaid), credits, recoveryRules);
        }, cancellationToken);
        var astray = paidDebits.Count == 0 ? [] : reversalCredits.Except(paidDebits).ToList();
        return
        [
            ReadinessResult.Of("FINANCIAL_FOUNDATIONS", "POSTING_RULES", "A posting rule for premium due, payment received, refund paid, claim reserve up and down, and claim paid",
                [.. rules > 0 ? [] : new[] { TransactionTypes.ClaimPaidReversed }, .. RecoveryTypes.Except(recoveryRules)]),
            ReadinessResult.Warning("FINANCIAL_FOUNDATIONS", "CLAIM_REVERSAL_ACCOUNTS",
                $"{TransactionTypes.ClaimPaidReversed} should credit the account {TransactionTypes.ClaimPaid} debits, or a reversed claim will not net to zero (account listed); a warning, not a blocker",
                astray),
        ];
    }
}
