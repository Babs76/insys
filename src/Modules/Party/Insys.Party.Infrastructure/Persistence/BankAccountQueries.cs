using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Party.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>Bank accounts and birth dates for other modules, on the Party module's own connection: status, hash and last four digits.</summary>
internal sealed class BankAccountQueries([FromKeyedServices(PartyDbContext.SchemaName)] NpgsqlDataSource dataSource, IFieldProtector protector)
    : IBankAccounts, IBankAccountDetails, IPersonFacts
{
    private const string Columns =
        "id, party_id, currency, status, account_number_hash, account_number_last4, account_number_enc, bank_code, branch_code, account_holder_name, account_type";

    public async Task<BankAccountRef?> Get(Guid tenantId, Guid bankAccountId, CancellationToken cancellationToken) =>
        await Read(tenantId, bankAccountId, r => new BankAccountRef(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)),
            cancellationToken);

    public async Task<PayeeAccountDetails?> ForPayment(Guid tenantId, Guid bankAccountId, CancellationToken cancellationToken) =>
        await Read(tenantId, bankAccountId, r => r.GetString(3) == "ACTIVE"
            ? new PayeeAccountDetails(protector.Unprotect(r.GetFieldValue<byte[]>(6)), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9),
                r.GetString(10), r.GetString(2))
            : null, cancellationToken);

    public async Task<BankAccountRef?> Current(Guid tenantId, Guid partyId, string currency, CancellationToken cancellationToken) =>
        await Query(tenantId, $"SELECT {Columns} FROM party.bank_account WHERE tenant_id = @t AND party_id = @id AND currency = @c AND status = 'ACTIVE'", partyId,
            r => new BankAccountRef(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)), cancellationToken, ("c", currency));

    public async Task<DateOnly?> BirthDate(Guid tenantId, Guid partyId, CancellationToken cancellationToken) =>
        (await Query(tenantId, "SELECT birth_date FROM party.individual WHERE tenant_id = @t AND party_id = @id", partyId,
            r => (object)r.GetFieldValue<DateOnly>(0), cancellationToken)) as DateOnly?;

    private Task<T?> Read<T>(Guid tenantId, Guid bankAccountId, Func<NpgsqlDataReader, T?> map, CancellationToken cancellationToken) where T : class =>
        Query(tenantId, $"SELECT {Columns} FROM party.bank_account WHERE tenant_id = @t AND id = @id", bankAccountId, map, cancellationToken);

    private async Task<T?> Query<T>(Guid tenantId, string sql, Guid id, Func<NpgsqlDataReader, T?> map, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) where T : class
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("id", id);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        T? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = map(reader);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
