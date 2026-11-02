/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using System.Globalization;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;

namespace Insys.Integration.Application;

/// <param name="Kind">GENERAL_POLICY, LIFE_POLICY, HEALTH_MEMBERSHIP, OUTSTANDING_PREMIUM, or OPENING_BALANCE:{account} with debits less credits.</param>
/// <param name="Currency">Needed with an amount. A batch may hold several currencies, so a kind has one total per currency (BR-128).</param>
public sealed record SourceTotal(string Kind, long Count, decimal? Amount, string? Currency = null);

/// <summary>B-013: what a batch loaded against what the source system says it held. Every difference is reported; nothing is changed.</summary>
public sealed record ReconcileMigrationBatch(string BatchCode, IReadOnlyList<SourceTotal> SourceTotals) : ICommand<MigrationReconciliationReport>;

public sealed record ReconciliationLine(string Kind, string? Currency, long? SourceCount, long? LoadedCount, decimal? SourceAmount, decimal? LoadedAmount, bool Agrees);

public sealed record MigrationReconciliationReport(string BatchCode, bool Reconciled, IReadOnlyList<ReconciliationLine> Lines);

internal sealed class ReconcileMigrationBatchHandler(IEnumerable<IMigrationTotals> modules, ITenantContext tenant, IAuditTrail audit) : ICommandHandler<ReconcileMigrationBatch, MigrationReconciliationReport>
{
    private static (string Kind, string? Currency) Key(SourceTotal total) => (total.Kind, total.Currency?.ToUpperInvariant());

    public async Task<Result<MigrationReconciliationReport>> Handle(ReconcileMigrationBatch command, CancellationToken cancellationToken)
    {
        if (command.SourceTotals.FirstOrDefault(t => t.Amount is not null && t.Currency is not { Length: 3 }) is { } unstated)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"The source total for {unstated.Kind} has an amount, so it needs its currency.");
        }

        if (command.SourceTotals.GroupBy(Key).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"The source totals name {twice.Key.Kind} {twice.Key.Currency} twice.");
        }

        var loaded = new Dictionary<(string Kind, string? Currency), MigrationTotal>();
        foreach (var module in modules)
        {
            foreach (var total in await module.For(tenant.RequireTenantId(), command.BatchCode, cancellationToken))
            {
                loaded[(total.Kind, total.Currency)] = total;
            }
        }

        var source = command.SourceTotals.ToDictionary(Key);
        var lines = source.Keys.Union(loaded.Keys).OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Currency, StringComparer.Ordinal).Select(key =>
        {
            var (from, to) = (source.GetValueOrDefault(key), loaded.GetValueOrDefault(key));
            // An opening balance's count is its lines, which the source's trial balance needn't state.
            var countAgrees = key.Kind.StartsWith("OPENING_BALANCE:", StringComparison.Ordinal) || from?.Count == to?.Count;
            var amountAgrees = to?.Amount is null || from?.Amount is null ? from is not null && to is not null : from.Amount == to.Amount;
            return new ReconciliationLine(key.Kind, key.Currency, from?.Count, to?.Count, from?.Amount, to?.Amount, from is not null && to is not null && countAgrees && amountAgrees);
        }).ToList();
        var reconciled = lines.Count > 0 && lines.All(l => l.Agrees);
        audit.Record(new AuditEntry("CHANNEL", "ReconcileMigrationBatch", "MigrationBatch", command.BatchCode, new Dictionary<string, string?>
        {
            ["differences"] = lines.Count(l => !l.Agrees).ToString(CultureInfo.InvariantCulture),
        }));
        return new MigrationReconciliationReport(command.BatchCode, reconciled, lines);
    }
}
