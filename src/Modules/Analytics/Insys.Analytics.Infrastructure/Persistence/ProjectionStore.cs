/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Analytics
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
using System.Text.Json;
using Insys.Analytics.Application;
using Insys.Analytics.Contracts;
using Insys.Analytics.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Analytics.Infrastructure.Persistence;

/// <summary>
/// The projections and their event log, written with SQL in the handler's transaction (the facts are additive upserts:
/// a key's measures accumulate). Row-level security and the tenant filter in each statement keep tenants apart.
/// </summary>
internal sealed class ProjectionStore(AnalyticsDbContext db, ITenantContext tenant, IExecutionContext execution, IClock clock) : IProjectionStore
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<bool> Log(Guid eventId, string eventType, DateTimeOffset occurredAt, ProjectionEntry entry, CancellationToken cancellationToken)
    {
        await using var command = await Command("""
            INSERT INTO analytics.event_log (tenant_id, event_id, event_type, occurred_at, received_at, payload)
            VALUES (@t, @id, @type, @occurred, @received, @payload) ON CONFLICT (tenant_id, event_id) DO NOTHING
            """, cancellationToken);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("received", clock.UtcNow); // one time source (V0093): the application's clock, not the database's
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.AddWithValue("occurred", occurredAt);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(entry, Json) });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task Apply(ProjectionEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = await Command(entry.Kind == ProjectionEntry.Renewal
            ? """
              INSERT INTO analytics.fact_renewals (tenant_id, refreshed_at, month_key, product_code, branch_code, measure, item_count, amount, currency)
              VALUES (@t, @now, @month, @product, @branch, @dimension, @count, @amount, @currency)
              ON CONFLICT (tenant_id, month_key, product_code, branch_code, measure) DO UPDATE SET
                item_count = fact_renewals.item_count + EXCLUDED.item_count,
                amount = fact_renewals.amount + EXCLUDED.amount,
                currency = CASE WHEN trim(fact_renewals.currency) = '' THEN EXCLUDED.currency ELSE fact_renewals.currency END,
                refreshed_at = EXCLUDED.refreshed_at
              """
            : entry.Kind == ProjectionEntry.WrittenPremium
            ? """
              INSERT INTO analytics.fact_written_premium (tenant_id, refreshed_at, date_key, product_code, channel_code, branch_code, policy_count, written_premium, currency)
              VALUES (@t, @now, @day, @product, @dimension, @branch, @count, @amount, @currency)
              ON CONFLICT (tenant_id, date_key, product_code, channel_code, branch_code) DO UPDATE SET
                policy_count = fact_written_premium.policy_count + EXCLUDED.policy_count,
                written_premium = fact_written_premium.written_premium + EXCLUDED.written_premium,
                refreshed_at = EXCLUDED.refreshed_at
              """
            : """
              INSERT INTO analytics.fact_claims_incurred (tenant_id, refreshed_at, month_key, product_code, cause_code, branch_code, claim_count, incurred, currency)
              VALUES (@t, @now, @month, @product, @dimension, @branch, @count, @amount, @currency)
              ON CONFLICT (tenant_id, month_key, product_code, cause_code, branch_code) DO UPDATE SET
                claim_count = fact_claims_incurred.claim_count + EXCLUDED.claim_count,
                incurred = fact_claims_incurred.incurred + EXCLUDED.incurred,
                currency = CASE WHEN trim(fact_claims_incurred.currency) = '' THEN EXCLUDED.currency ELSE fact_claims_incurred.currency END,
                refreshed_at = EXCLUDED.refreshed_at
              """, cancellationToken);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("day", entry.Day);
        command.Parameters.AddWithValue("month", entry.MonthKey);
        command.Parameters.AddWithValue("product", entry.ProductCode);
        command.Parameters.AddWithValue("dimension", entry.Dimension);
        command.Parameters.AddWithValue("branch", entry.Branch);
        command.Parameters.AddWithValue("count", entry.Count);
        command.Parameters.AddWithValue("amount", entry.Amount);
        command.Parameters.AddWithValue("currency", entry.Currency);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(string ProductCode, string Branch)?> ClaimDimensions(string claimNumber, CancellationToken cancellationToken)
    {
        await using var command = await Command("""
            SELECT payload ->> 'productCode', coalesce(payload ->> 'branch', 'ALL') FROM analytics.event_log
            WHERE tenant_id = @t AND payload ->> 'kind' = 'CLAIM_REGISTERED' AND payload ->> 'claimNumber' = @claim LIMIT 1
            """, cancellationToken);
        command.Parameters.AddWithValue("claim", claimNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    public async Task<int> Rebuild(DateTimeOffset now, Func<DateTimeOffset, DateOnly> businessDateOf, CancellationToken cancellationToken)
    {
        await using (var clear = await Command("""
                         DELETE FROM analytics.fact_written_premium WHERE tenant_id = @t; DELETE FROM analytics.fact_claims_incurred WHERE tenant_id = @t;
                         DELETE FROM analytics.fact_renewals WHERE tenant_id = @t
                         """, cancellationToken))
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        var entries = new List<ProjectionEntry>();
        await using (var read = await Command("SELECT payload, occurred_at FROM analytics.event_log WHERE tenant_id = @t ORDER BY occurred_at, event_id", cancellationToken))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = JsonSerializer.Deserialize<ProjectionEntry>(reader.GetString(0), Json)!;
                entries.Add(entry.DatedByOccurrence ? entry with { Day = businessDateOf(reader.GetFieldValue<DateTimeOffset>(1)) } : entry);
            }
        }

        foreach (var entry in entries)
        {
            await Apply(entry, now, cancellationToken);
        }

        return entries.Count;
    }

    public async Task<bool> BeginReleaseTask(string taskCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = await Command("""
            INSERT INTO analytics.release_task (tenant_id, task_code, completed_at) VALUES (@t, @task, @now) ON CONFLICT DO NOTHING
            """, cancellationToken);
        command.Parameters.AddWithValue("task", taskCode);
        command.Parameters.AddWithValue("now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<WrittenPremiumRow>> WrittenPremium(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var command = await Command("", cancellationToken);
        command.CommandText = $"""
            SELECT date_key, product_code, channel_code, branch_code, policy_count, written_premium, currency FROM analytics.fact_written_premium
            WHERE tenant_id = @t AND date_key BETWEEN @from AND @to AND {Scope(command)} ORDER BY date_key, product_code, channel_code, branch_code
            """;
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        var rows = new List<WrittenPremiumRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new WrittenPremiumRow(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetDecimal(5), reader.GetString(6)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<ClaimsIncurredRow>> ClaimsIncurred(string fromMonth, string toMonth, CancellationToken cancellationToken)
    {
        await using var command = await Command("", cancellationToken);
        command.CommandText = $"""
            SELECT month_key, product_code, cause_code, claim_count, incurred, currency, branch_code FROM analytics.fact_claims_incurred
            WHERE tenant_id = @t AND month_key BETWEEN @from AND @to AND {Scope(command)} ORDER BY month_key, product_code, cause_code, branch_code
            """;
        command.Parameters.AddWithValue("from", fromMonth);
        command.Parameters.AddWithValue("to", toMonth);
        var rows = new List<ClaimsIncurredRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClaimsIncurredRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetDecimal(4), reader.GetString(5).Trim(),
                reader.GetString(6)));
        }

        return rows;
    }

    public async Task<DashboardSummary> Summary(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // Money per currency, never added across currencies; claims by the months the dates fall in (their projection is monthly).
        await using var command = await Command("", cancellationToken);
        var scope = Scope(command);
        command.CommandText = $"""
            SELECT 'P', currency, sum(written_premium), sum(policy_count), max(refreshed_at) FROM analytics.fact_written_premium
            WHERE tenant_id = @t AND date_key BETWEEN @from AND @to AND {scope} GROUP BY currency
            UNION ALL
            SELECT 'C', trim(currency), sum(incurred), sum(claim_count), max(refreshed_at) FROM analytics.fact_claims_incurred
            WHERE tenant_id = @t AND month_key BETWEEN @fromMonth AND @toMonth AND {scope} GROUP BY trim(currency)
            """;
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("fromMonth", from.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("toMonth", to.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        var premium = new List<MoneyAmount>();
        var incurred = new List<MoneyAmount>();
        var (policies, claims) = (0L, 0L);
        DateTimeOffset? refreshed = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var isPremium = reader.GetString(0) == "P";
                var currency = reader.GetString(1);
                if (currency.Length > 0)
                {
                    (isPremium ? premium : incurred).Add(new MoneyAmount(currency, reader.GetDecimal(2)));
                }

                if (isPremium) policies += reader.GetInt64(3); else claims += reader.GetInt64(3);
                var at = reader.GetFieldValue<DateTimeOffset>(4);
                refreshed = refreshed is null || at > refreshed ? at : refreshed;
            }
        }

        var (renewals, renewalsRefreshed) = await RenewalFigures(from, to, cancellationToken);
        refreshed = renewalsRefreshed is { } r && (refreshed is null || r > refreshed) ? r : refreshed;
        return new DashboardSummary(clock.UtcNow,
        [
            new DashboardFigure(AnalyticsFigures.WrittenPremium, Amounts: premium.OrderBy(m => m.Currency, StringComparer.Ordinal).ToList()),
            new DashboardFigure(AnalyticsFigures.PoliciesWritten, Count: policies),
            new DashboardFigure(AnalyticsFigures.ClaimsIncurred, Amounts: incurred.OrderBy(m => m.Currency, StringComparer.Ordinal).ToList()),
            new DashboardFigure(AnalyticsFigures.ClaimsReported, Count: claims),
            .. renewals,
        ], refreshed, [AnalyticsFigures.Health]);
    }

    /// <summary>
    /// F027 (addendum §4.7): the renewal figures over the months the dates fall in, within the caller's scope. The renewal rate
    /// is renewed over due; the retention rate, retained over retention cases closed; the average premium change is per currency
    /// over the renewals renewed in it; offers made and accepted are by kind.
    /// </summary>
    private async Task<(IReadOnlyList<DashboardFigure> Figures, DateTimeOffset? RefreshedAt)> RenewalFigures(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var command = await Command("", cancellationToken);
        command.CommandText = $"""
            SELECT measure, trim(currency), sum(item_count), sum(amount), max(refreshed_at) FROM analytics.fact_renewals
            WHERE tenant_id = @t AND month_key BETWEEN @fromMonth AND @toMonth AND {Scope(command)} GROUP BY measure, trim(currency)
            """;
        command.Parameters.AddWithValue("fromMonth", from.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("toMonth", to.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var changes = new List<(string Currency, decimal Amount, long Count)>();
        DateTimeOffset? refreshed = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var (measure, currency, count) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
                counts[measure] = counts.GetValueOrDefault(measure) + count;
                if (measure == RenewalMeasures.Renewed && currency.Length > 0)
                {
                    changes.Add((currency, reader.GetDecimal(3), count));
                }

                var at = reader.GetFieldValue<DateTimeOffset>(4);
                refreshed = refreshed is null || at > refreshed ? at : refreshed;
            }
        }

        long Count(string measure) => counts.GetValueOrDefault(measure);
        decimal? Rate(long of, long over) => over == 0 ? null : Math.Round((decimal)of / over, 4, MidpointRounding.AwayFromZero);
        IReadOnlyList<FigurePart> ByKind(string prefix) =>
            [.. counts.Where(c => c.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => new FigurePart(c.Key[prefix.Length..], c.Value))];
        return (
        [
            new DashboardFigure(AnalyticsFigures.RenewalsDue, Count: Count(RenewalMeasures.Due)),
            new DashboardFigure(AnalyticsFigures.RenewalsInvited, Count: Count(RenewalMeasures.Invited)),
            new DashboardFigure(AnalyticsFigures.RenewalsAccepted, Count: Count(RenewalMeasures.Accepted)),
            new DashboardFigure(AnalyticsFigures.RenewalsRenewed, Count: Count(RenewalMeasures.Renewed)),
            new DashboardFigure(AnalyticsFigures.RenewalsDeclined, Count: Count(RenewalMeasures.Declined)),
            new DashboardFigure(AnalyticsFigures.RenewalsNotRenewed, Count: Count(RenewalMeasures.NotRenewed)),
            new DashboardFigure(AnalyticsFigures.RenewalsLapsed, Count: Count(RenewalMeasures.Lapsed)),
            new DashboardFigure(AnalyticsFigures.RenewalRate, Rate: Rate(Count(RenewalMeasures.Renewed), Count(RenewalMeasures.Due))),
            new DashboardFigure(AnalyticsFigures.RetentionRate, Rate: Rate(Count(RenewalMeasures.RetentionRetained), Count(RenewalMeasures.RetentionClosed))),
            new DashboardFigure(AnalyticsFigures.AveragePremiumChange, Amounts: changes.GroupBy(c => c.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new MoneyAmount(g.Key, Math.Round(g.Sum(c => c.Amount) / g.Sum(c => c.Count), 2, MidpointRounding.AwayFromZero))).ToList()),
            new DashboardFigure(AnalyticsFigures.RetentionOffersMade, Count: counts.Where(c => c.Key.StartsWith(RenewalMeasures.OfferMade, StringComparison.Ordinal)).Sum(c => c.Value),
                By: ByKind(RenewalMeasures.OfferMade)),
            new DashboardFigure(AnalyticsFigures.RetentionOffersAccepted,
                Count: counts.Where(c => c.Key.StartsWith(RenewalMeasures.OfferAccepted, StringComparison.Ordinal)).Sum(c => c.Value), By: ByKind(RenewalMeasures.OfferAccepted)),
        ], refreshed);
    }

    /// <summary>The caller's F103 scope over a fact's product and branch: a fact with branch ALL (no node) is outside every branch scope.</summary>
    private string Scope(NpgsqlCommand command) => ScopeSql.Predicate(execution.CurrentScope, "product_code", "branch_code", command);

    /// <summary>A command on the context's connection, in its current transaction (the pipeline's, with the tenant set).</summary>
    private async Task<NpgsqlCommand> Command(string sql, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.AddWithValue("t", tenant.RequireTenantId());
        return command;
    }
}

/// <summary>Metric totals for other modules (regulatory returns), on Analytics' own connection.</summary>
internal sealed class ProjectionTotals([FromKeyedServices(AnalyticsDbContext.SchemaName)] NpgsqlDataSource dataSource) : IProjectionTotals
{
    public async Task<decimal?> Total(Guid tenantId, string metricCode, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        string? source;
        await using (var metric = new NpgsqlCommand("""
                         SELECT source FROM analytics.cfg_metric_definitions
                         WHERE (tenant_id = @t OR tenant_id IS NULL) AND metric_code = @m AND record_status = ANY(@statuses) AND effective_from <= @day
                           AND (effective_to IS NULL OR effective_to > @day)
                         ORDER BY tenant_id NULLS LAST, version DESC LIMIT 1
                         """, connection, transaction))
        {
            metric.Parameters.AddWithValue("t", tenantId);
            metric.Parameters.AddWithValue("m", metricCode);
            metric.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
            metric.Parameters.AddWithValue("day", to);
            source = await metric.ExecuteScalarAsync(cancellationToken) as string;
        }

        var sql = source switch
        {
            ProjectionSources.WrittenPremium => "SELECT coalesce(sum(written_premium), 0) FROM analytics.fact_written_premium WHERE tenant_id = @t AND date_key BETWEEN @from AND @to",
            ProjectionSources.PolicyCount => "SELECT coalesce(sum(policy_count), 0) FROM analytics.fact_written_premium WHERE tenant_id = @t AND date_key BETWEEN @from AND @to",
            ProjectionSources.ClaimsIncurred => "SELECT coalesce(sum(incurred), 0) FROM analytics.fact_claims_incurred WHERE tenant_id = @t AND month_key BETWEEN @fm AND @tm",
            ProjectionSources.ClaimCount => "SELECT coalesce(sum(claim_count), 0) FROM analytics.fact_claims_incurred WHERE tenant_id = @t AND month_key BETWEEN @fm AND @tm",
            _ => null,
        };
        decimal? total = null;
        if (sql is not null)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("from", from);
            command.Parameters.AddWithValue("to", to);
            command.Parameters.AddWithValue("fm", from.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("tm", to.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
            total = Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(cancellationToken);
        return total;
    }
}
