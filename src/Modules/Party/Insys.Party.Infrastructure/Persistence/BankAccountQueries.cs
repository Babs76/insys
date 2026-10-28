/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Party.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>Bank accounts, birth dates and preferred languages for other modules, on the Party module's own connection: status, hash and last four digits.</summary>
internal sealed class BankAccountQueries([FromKeyedServices(PartyDbContext.SchemaName)] NpgsqlDataSource dataSource, IFieldProtector protector)
    : IBankAccounts, IBankAccountDetails, IPersonFacts, IPartyLanguage, IPartyDirectory, IPartyContacts, IPartyConsent
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

    public async Task<bool> Exists(Guid tenantId, Guid partyId, CancellationToken cancellationToken) =>
        await Query(tenantId, "SELECT id FROM party.party WHERE tenant_id = @t AND id = @id AND merged_into_id IS NULL", partyId, r => (object)r.GetGuid(0),
            cancellationToken) is not null;

    public async Task<string?> RegisteredBy(Guid tenantId, Guid partyId, CancellationToken cancellationToken) =>
        await Query(tenantId, "SELECT registered_by FROM party.party WHERE tenant_id = @t AND id = @id", partyId, r => r.IsDBNull(0) ? null : r.GetString(0),
            cancellationToken);

    public async Task<Guid?> MergedInto(Guid tenantId, Guid partyId, CancellationToken cancellationToken) =>
        await Query(tenantId, "SELECT merged_into_id FROM party.party WHERE tenant_id = @t AND id = @id", partyId, r => r.IsDBNull(0) ? null : r.GetGuid(0).ToString(),
            cancellationToken) is { } survivor ? Guid.Parse(survivor) : null;

    public async Task<string?> DisplayName(Guid tenantId, Guid partyId, CancellationToken cancellationToken) =>
        await Query(tenantId, "SELECT display_name FROM party.party WHERE tenant_id = @t AND id = @id", partyId, r => r.GetString(0), cancellationToken);

    public async Task<string?> PreferredLanguage(Guid tenantId, Guid partyId, CancellationToken cancellationToken) =>
        (await Query(tenantId, "SELECT preferred_language FROM party.individual WHERE tenant_id = @t AND party_id = @id", partyId,
            r => r.IsDBNull(0) ? null : r.GetString(0).Trim(), cancellationToken)) is { Length: > 0 } language ? language : null;

    public async Task<string?> Primary(Guid tenantId, Guid partyId, string channelType, CancellationToken cancellationToken) =>
        await Query(tenantId, """
            SELECT value FROM party.contact_point WHERE tenant_id = @t AND party_id = @id AND channel_type = @type AND is_primary
            ORDER BY id DESC LIMIT 1
            """, partyId, r => r.GetString(0), cancellationToken, ("type", channelType));

    public async Task<bool> IsGranted(Guid tenantId, Guid partyId, string purposeCode, CancellationToken cancellationToken) =>
        await Query(tenantId, """
            SELECT granted FROM party.consent_record WHERE tenant_id = @t AND party_id = @id AND purpose_code = @purpose
            ORDER BY captured_at DESC, id DESC LIMIT 1
            """, partyId, r => r.GetBoolean(0) ? "Y" : null, cancellationToken, ("purpose", purposeCode)) is not null;

    public async Task<Guid?> GrantingRecord(Guid tenantId, Guid partyId, string purposeCode, CancellationToken cancellationToken) =>
        await Query(tenantId, """
            SELECT id::text FROM (SELECT id, granted FROM party.consent_record WHERE tenant_id = @t AND party_id = @id AND purpose_code = @purpose
                                  ORDER BY captured_at DESC, id DESC LIMIT 1) latest
            WHERE granted
            """, partyId, r => r.GetString(0), cancellationToken, ("purpose", purposeCode)) is { } id ? Guid.Parse(id) : null;

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
