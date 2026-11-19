/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Application;
using Insys.Assist.Domain;
using Insys.Assist.Infrastructure.Persistence;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.ProductFactory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Assist.Infrastructure;

/// <summary>
/// OQ-190 (b), decided by Ade on 19 November 2026: Product Factory's submission asks this check, and a version AI-1 drafted is refused while its review is
/// incomplete: an Interpreted value (a version field, a rate table, a rule or a simulation case) not yet reviewed, or a version field whose Draft value isn't
/// what was decided (AI Assist SRS §8.1 step 5, AIA-020). The same rule as the provenance report's <c>reviewComplete</c>. Read on Assist's own connection.
/// A version AI-1 didn't draft is unaffected. The hold applies whether or not the tenant has AI Assist today (Ade, 19 November 2026): the review is recorded
/// under ASSIST_PRODUCT_DRAFT_REVIEW, which needs no feature, so it can always be finished.
/// </summary>
internal sealed class ProductDraftSubmissionCheck([FromKeyedServices(AssistDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantContext tenant)
    : IProductSubmissionCheck
{
    public async Task<ProductSubmissionRefusal?> Check(ProductDraftVersion version, ProductDraftVersion? copiedFrom, CancellationToken cancellationToken)
    {
        var items = await Items(tenant.RequireTenantId(), version.ProductCode, version.Version, cancellationToken);
        if (items is null)
        {
            return null;
        }

        var (_, summary) = ProductDraftProvenance.Review(items, version, copiedFrom, new Dictionary<Guid, string?>());
        return summary.ReviewComplete
            ? null
            : new ProductSubmissionRefusal(
                $"Version {version.Version} of {version.ProductCode} was drafted by AI-1 and its review isn't complete: {summary.InterpretedUndecided} Interpreted "
                + $"value(s) not yet reviewed and {summary.DraftDiffers} version field(s) differing from their decision. The configurator who ran the draft reviews "
                + "them first (AI Assist §8.1 step 5).");
    }

    /// <summary>The items of the latest run that drafted the version, or null when AI-1 didn't draft it.</summary>
    private async Task<IReadOnlyList<DraftItemView>?> Items(Guid tenantId, string productCode, int version, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH run AS (
              SELECT id FROM assist.product_draft_run
              WHERE tenant_id = @t AND product_code = @p AND draft_version = @v AND status = 'DRAFTED'
              ORDER BY id DESC LIMIT 1)
            SELECT (SELECT count(*) FROM run), i.id, i.kind, i.field_path, i.value::text, i.flag, i.check_result, i.check_reason, i.in_draft, i.decision,
                   i.edited_value::text
            FROM (SELECT 1) one
            LEFT JOIN assist.draft_item i ON i.tenant_id = @t AND i.run_id = (SELECT id FROM run)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("p", productCode);
        command.Parameters.AddWithValue("v", version);
        var items = new List<DraftItemView>();
        var drafted = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                drafted = reader.GetInt64(0) > 0;
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                items.Add(new DraftItemView(reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), null, null, null, reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return drafted ? items : null;
    }
}
