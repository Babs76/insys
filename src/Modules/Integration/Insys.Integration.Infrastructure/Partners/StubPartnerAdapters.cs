using System.Diagnostics;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Integration.Infrastructure.Partners;

/// <summary>
/// Stub partner adapters (PROMPT §11: interfaces and stubs only): every call is accepted with a reference and recorded in
/// integration.external_call_log, on Integration's own connection. Only codes and references are logged, never content.
/// </summary>
internal sealed class StubPartnerAdapters([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, IClock clock)
    : IReinsurerAdapter, IRegulatorAdapter
{
    public Task<PartnerCallResult> SendBordereau(Guid tenantId, string partnerCode, string treatyCode, string periodCode, string content,
        CancellationToken cancellationToken) =>
        Call(tenantId, partnerCode, "reinsurance.SendBordereau", cancellationToken);

    public Task<PartnerCallResult> BillRecovery(Guid tenantId, string partnerCode, string treatyCode, string claimNumber, decimal amount, string currency,
        CancellationToken cancellationToken) =>
        Call(tenantId, partnerCode, "reinsurance.BillRecovery", cancellationToken);

    public Task<PartnerCallResult> SubmitReturn(Guid tenantId, string regulator, string returnCode, string periodCode, JsonElement values,
        CancellationToken cancellationToken) =>
        Call(tenantId, regulator, "regulator.SubmitReturn", cancellationToken);

    private async Task<PartnerCallResult> Call(Guid tenantId, string partnerCode, string operation, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var reference = $"STUB-{Guid.CreateVersion7():N}"[..20];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO integration.external_call_log (tenant_id, id, created_at, created_by, partner_code, operation, request_id, status, http_status, latency_ms, called_at)
            VALUES (@t, @id, @now, 'stub-adapter', @partner, @operation, @request, 'ACCEPTED', 202, @latency, @now)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("partner", partnerCode.Length <= 15 ? partnerCode : partnerCode[..15]);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("request", reference);
        command.Parameters.AddWithValue("latency", (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PartnerCallResult(true, reference, null);
    }
}
