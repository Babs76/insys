using System.Text.Json;
using Insys.Analytics.Application;
using Insys.Analytics.Contracts;
using Insys.Analytics.Infrastructure.StaticData;
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
internal sealed class ProjectionStore(AnalyticsDbContext db, ITenantContext tenant) : IProjectionStore
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<bool> Log(Guid eventId, string eventType, DateTimeOffset occurredAt, ProjectionEntry entry, CancellationToken cancellationToken)
    {
        await using var command = await Command("""
            INSERT INTO analytics.event_log (tenant_id, event_id, event_type, occurred_at, received_at, payload)
            VALUES (@t, @id, @type, @occurred, now(), @payload) ON CONFLICT (tenant_id, event_id) DO NOTHING
            """, cancellationToken);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.AddWithValue("occurred", occurredAt);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(entry, Json) });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task Apply(ProjectionEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = await Command(entry.Kind == ProjectionEntry.WrittenPremium
            ? """
              INSERT INTO analytics.fact_written_premium (tenant_id, refreshed_at, date_key, product_code, channel_code, branch_code, policy_count, written_premium, currency)
              VALUES (@t, @now, @day, @product, @dimension, @branch, @count, @amount, @currency)
              ON CONFLICT (tenant_id, date_key, product_code, channel_code, branch_code) DO UPDATE SET
                policy_count = fact_written_premium.policy_count + EXCLUDED.policy_count,
                written_premium = fact_written_premium.written_premium + EXCLUDED.written_premium,
                refreshed_at = EXCLUDED.refreshed_at
              """
            : """
              INSERT INTO analytics.fact_claims_incurred (tenant_id, refreshed_at, month_key, product_code, cause_code, claim_count, incurred, currency)
              VALUES (@t, @now, @month, @product, @dimension, @count, @amount, @currency)
              ON CONFLICT (tenant_id, month_key, product_code, cause_code) DO UPDATE SET
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

    public async Task<string?> ClaimProduct(string claimNumber, CancellationToken cancellationToken)
    {
        await using var command = await Command("""
            SELECT payload ->> 'productCode' FROM analytics.event_log
            WHERE tenant_id = @t AND payload ->> 'kind' = 'CLAIM_REGISTERED' AND payload ->> 'claimNumber' = @claim LIMIT 1
            """, cancellationToken);
        command.Parameters.AddWithValue("claim", claimNumber);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<int> Rebuild(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using (var clear = await Command("DELETE FROM analytics.fact_written_premium WHERE tenant_id = @t; DELETE FROM analytics.fact_claims_incurred WHERE tenant_id = @t",
                         cancellationToken))
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        var entries = new List<ProjectionEntry>();
        await using (var read = await Command("SELECT payload FROM analytics.event_log WHERE tenant_id = @t ORDER BY occurred_at, event_id", cancellationToken))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(JsonSerializer.Deserialize<ProjectionEntry>(reader.GetString(0), Json)!);
            }
        }

        foreach (var entry in entries)
        {
            await Apply(entry, now, cancellationToken);
        }

        return entries.Count;
    }

    public async Task<IReadOnlyList<WrittenPremiumRow>> WrittenPremium(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var command = await Command("""
            SELECT date_key, product_code, channel_code, branch_code, policy_count, written_premium, currency FROM analytics.fact_written_premium
            WHERE tenant_id = @t AND date_key BETWEEN @from AND @to ORDER BY date_key, product_code, channel_code, branch_code
            """, cancellationToken);
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
        await using var command = await Command("""
            SELECT month_key, product_code, cause_code, claim_count, incurred, currency FROM analytics.fact_claims_incurred
            WHERE tenant_id = @t AND month_key BETWEEN @from AND @to ORDER BY month_key, product_code, cause_code
            """, cancellationToken);
        command.Parameters.AddWithValue("from", fromMonth);
        command.Parameters.AddWithValue("to", toMonth);
        var rows = new List<ClaimsIncurredRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClaimsIncurredRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetDecimal(4), reader.GetString(5).Trim()));
        }

        return rows;
    }

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
