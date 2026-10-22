using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Finance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Finance.Infrastructure.Persistence;

internal sealed class LedgerBalances([FromKeyedServices(FinanceDbContext.SchemaName)] NpgsqlDataSource dataSource) : ILedgerBalances
{
    public async Task<decimal> PeriodMovement(Guid tenantId, string accountCode, string periodCode, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(sum(e.debit - e.credit), 0) FROM finance.subledger_entry e
            JOIN finance.journal j ON j.tenant_id = e.tenant_id AND j.id = e.journal_id
            WHERE e.tenant_id = @t AND e.account_code = @account AND j.period_code = @period
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("account", accountCode);
        command.Parameters.AddWithValue("period", periodCode);
        var movement = Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return movement;
    }
}
