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
public sealed record SourceTotal(string Kind, long Count, decimal? Amount);

/// <summary>B-013: what a batch loaded against what the source system says it held. Every difference is reported; nothing is changed.</summary>
public sealed record ReconcileMigrationBatch(string BatchCode, IReadOnlyList<SourceTotal> SourceTotals) : ICommand<MigrationReconciliationReport>;

public sealed record ReconciliationLine(string Kind, long? SourceCount, long? LoadedCount, decimal? SourceAmount, decimal? LoadedAmount, bool Agrees);

public sealed record MigrationReconciliationReport(string BatchCode, bool Reconciled, IReadOnlyList<ReconciliationLine> Lines);

internal sealed class ReconcileMigrationBatchHandler(IEnumerable<IMigrationTotals> modules, ITenantContext tenant, IAuditTrail audit) : ICommandHandler<ReconcileMigrationBatch, MigrationReconciliationReport>
{
    public async Task<Result<MigrationReconciliationReport>> Handle(ReconcileMigrationBatch command, CancellationToken cancellationToken)
    {
        var loaded = new Dictionary<string, MigrationTotal>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            foreach (var total in await module.For(tenant.RequireTenantId(), command.BatchCode, cancellationToken))
            {
                loaded[total.Kind] = total;
            }
        }

        var source = command.SourceTotals.ToDictionary(t => t.Kind, StringComparer.Ordinal);
        var lines = source.Keys.Union(loaded.Keys).Order(StringComparer.Ordinal).Select(kind =>
        {
            var (from, to) = (source.GetValueOrDefault(kind), loaded.GetValueOrDefault(kind));
            // An opening balance's count is its lines, which the source's trial balance needn't state.
            var countAgrees = kind.StartsWith("OPENING_BALANCE:", StringComparison.Ordinal) || from?.Count == to?.Count;
            var amountAgrees = to?.Amount is null || from?.Amount is null ? from is not null && to is not null : from.Amount == to.Amount;
            return new ReconciliationLine(kind, from?.Count, to?.Count, from?.Amount, to?.Amount, from is not null && to is not null && countAgrees && amountAgrees);
        }).ToList();
        var reconciled = lines.Count > 0 && lines.All(l => l.Agrees);
        audit.Record(new AuditEntry("CHANNEL", "ReconcileMigrationBatch", "MigrationBatch", command.BatchCode, new Dictionary<string, string?>
        {
            ["differences"] = lines.Count(l => !l.Agrees).ToString(CultureInfo.InvariantCulture),
        }));
        return new MigrationReconciliationReport(command.BatchCode, reconciled, lines);
    }
}
