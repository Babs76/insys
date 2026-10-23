/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>
/// V0014: journals balance in the database as well as in code, and posted financial records are never changed:
/// the module roles hold no UPDATE or DELETE on them, and a trigger refuses both (FIN-002, FIN-003, DBA-009).
/// </summary>
public sealed class PostedFinancialRecordsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task BalancedJournal_IsAccepted()
    {
        await using var transaction = await Finance();
        var journal = await Journal(transaction);
        await Line(transaction, journal, 1, debit: 100m, credit: 0m);
        await Line(transaction, journal, 2, debit: 0m, credit: 100m);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task UnbalancedJournal_IsRefusedAtCommit()
    {
        await using var transaction = await Finance();
        var journal = await Journal(transaction);
        await Line(transaction, journal, 1, debit: 100m, credit: 0m);
        await Line(transaction, journal, 2, debit: 0m, credit: 99.99m);

        var error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.MessageText.ShouldContain("does not balance");
    }

    [Fact]
    public async Task OneSidedJournal_IsRefusedAtCommit()
    {
        await using var transaction = await Finance();
        var journal = await Journal(transaction);
        await Line(transaction, journal, 1, debit: 100m, credit: 0m);

        (await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync())).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task JournalMixingCurrencies_IsRefusedAtCommit()
    {
        await using var transaction = await Finance();
        var journal = await Journal(transaction);
        await Line(transaction, journal, 1, debit: 100m, credit: 0m);
        await Line(transaction, journal, 2, debit: 0m, credit: 100m, currency: "USD");

        (await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync())).MessageText.ShouldContain("mixes currencies");
    }

    [Fact]
    public async Task Journal_IsReversedAtMostOnce()
    {
        var original = await PostedJournal();
        var first = await PostedJournal(reverses: original);

        var error = await Should.ThrowAsync<PostgresException>(() => PostedJournal(reverses: original));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        first.ShouldNotBe(original);
    }

    [Fact]
    public async Task ModuleRoles_HoldNoUpdateOrDelete_OnPostedTables_IncludingPartitions_DBA009()
    {
        var grants = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM information_schema.role_table_grants g
            WHERE g.grantee LIKE 'insys\_%' AND g.privilege_type IN ('UPDATE', 'DELETE', 'TRUNCATE')
              AND (g.table_schema, regexp_replace(g.table_name, '_p?[0-9_]+$', '')) IN
                  (('finance', 'journal'), ('finance', 'subledger_entry'), ('claims', 'claim_financial_movement'), ('payment', 'payment_status_history'))
            """);
        grants.ShouldBe(0L);

        var partitions = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent JOIN pg_namespace n ON n.oid = p.relnamespace
            WHERE n.nspname = 'finance' AND p.relname IN ('journal', 'subledger_entry')
            """);
        partitions.ShouldBeGreaterThan(0L);
    }

    [Theory]
    [InlineData("insys_finance", "UPDATE finance.journal SET status = 'POSTED'")]
    [InlineData("insys_finance", "DELETE FROM finance.subledger_entry")]
    [InlineData("insys_claims", "UPDATE claims.claim_financial_movement SET amount = 0")]
    [InlineData("insys_payment", "DELETE FROM payment.payment_status_history")]
    public async Task ModuleRole_CannotChangePostedRecords_FIN002(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor(role));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(transaction);
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task EvenTheOwner_CannotChangeAPostedJournal_TheTriggerRefuses()
    {
        var journal = await PostedJournal();

        foreach (var sql in new[]
                 {
                     "UPDATE finance.journal SET source_domain = 'X' WHERE id = @j", "DELETE FROM finance.journal WHERE id = @j",
                     "UPDATE finance.subledger_entry SET debit = debit + 1 WHERE journal_id = @j",
                 })
        {
            var error = await Should.ThrowAsync<PostgresException>(() => fixture.Database.ExecuteAsOwner(sql, ("j", journal)));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            error.MessageText.ShouldContain("never changed");
        }
    }

    private async Task<Guid> PostedJournal(Guid? reverses = null)
    {
        await using var transaction = await Finance();
        var journal = await Journal(transaction, reverses);
        await Line(transaction, journal, 1, debit: 50m, credit: 0m);
        await Line(transaction, journal, 2, debit: 0m, credit: 50m);
        await transaction.CommitAsync();
        return journal;
    }

    private async Task<NpgsqlTransaction> Finance()
    {
        var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_finance"));
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await SetTenant(transaction);
        return transaction;
    }

    private static async Task SetTenant(NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", transaction.Connection, transaction);
        command.Parameters.AddWithValue("t", DatabaseFixture.TenantA.ToString());
        await command.ExecuteScalarAsync();
    }

    private static async Task<Guid> Journal(NpgsqlTransaction transaction, Guid? reverses = null)
    {
        var id = Guid.CreateVersion7();
        await using var command = new NpgsqlCommand("""
            INSERT INTO finance.journal (tenant_id, id, created_at, created_by, journal_number, accounting_date, period_code, source_event_id, source_domain, status,
                                         posted_at, reverses_journal_id)
            VALUES (@t, @id, now(), 'test', @n, DATE '2026-10-21', '2026-10', gen_random_uuid(), 'BILLING', 'POSTED', now(), @r)
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("t", DatabaseFixture.TenantA);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("n", "JNL-" + id.ToString("N")[..16]);
        command.Parameters.Add(new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)reverses ?? DBNull.Value });
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task Line(NpgsqlTransaction transaction, Guid journal, short lineNo, decimal debit, decimal credit, string currency = "ZAR")
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO finance.subledger_entry (tenant_id, id, created_at, created_by, journal_id, line_no, account_code, debit, credit, currency, base_amount,
                                                 accounting_date, transaction_date, business_ref_type, business_ref_id, posting_rule_code, posting_rule_version)
            VALUES (@t, gen_random_uuid(), now(), 'test', @j, @l, '1100', @d, @c, @cur, @d + @c, DATE '2026-10-21', DATE '2026-10-21', 'INVOICE', gen_random_uuid(), 'PR', 1)
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("t", DatabaseFixture.TenantA);
        command.Parameters.AddWithValue("j", journal);
        command.Parameters.AddWithValue("l", lineNo);
        command.Parameters.AddWithValue("d", debit);
        command.Parameters.AddWithValue("c", credit);
        command.Parameters.AddWithValue("cur", currency);
        await command.ExecuteNonQueryAsync();
    }
}
