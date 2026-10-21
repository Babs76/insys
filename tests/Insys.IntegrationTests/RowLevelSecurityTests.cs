using Insys.Testing;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>The database layer of tenant isolation, as the non-owner application role (DBA-001 to DBA-003, SRS §27.4).</summary>
public sealed class RowLevelSecurityTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid RowA = Guid.CreateVersion7();
    private static readonly Guid RowB = Guid.CreateVersion7();

    public async ValueTask InitializeAsync()
    {
        foreach (var (tenant, id) in new[] { (DatabaseFixture.TenantA, RowA), (DatabaseFixture.TenantB, RowB) })
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO integration.dead_letter_message
                  (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, source, message_id, payload, error, first_failed_at, attempts)
                VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', 'outbox:test', @id::text, '{}', 'X', now(), 3)
                ON CONFLICT DO NOTHING
                """, ("t", tenant), ("id", id));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AppRole_IsNotOwner_AndCannotBypassRls_DBA002()
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand("SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetBoolean(0).ShouldBeFalse();
        reader.GetBoolean(1).ShouldBeFalse();
    }

    [Fact]
    public async Task Query_WithoutTenant_FailsInsteadOfReturningData_DBA003()
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM integration.dead_letter_message", connection);

        await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Query_AfterAPreviousTenantTransactionEnded_FailsInsteadOfReturningData_DBA003()
    {
        await using var connection = await Open();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenant(connection, transaction, DatabaseFixture.TenantA);
            await transaction.CommitAsync();
        }

        await using var command = new NpgsqlCommand("SELECT count(*) FROM integration.dead_letter_message", connection);
        await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Query_WithTenant_SeesOnlyThatTenant_TEN004()
    {
        await using var connection = await Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand("SELECT DISTINCT tenant_id FROM integration.dead_letter_message", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var tenants = new List<Guid>();
        while (await reader.ReadAsync())
        {
            tenants.Add(reader.GetGuid(0));
        }

        tenants.ShouldBe([DatabaseFixture.TenantA]);
    }

    [Fact]
    public async Task Insert_ForAnotherTenant_IsRejected_TEN004()
    {
        await using var connection = await Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand("""
            INSERT INTO integration.dead_letter_message
              (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, source, message_id, payload, error, first_failed_at, attempts)
            VALUES (@t, gen_random_uuid(), 1, now(), 'x', now(), 'x', 'x', 'x', '{}', 'x', now(), 1)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", DatabaseFixture.TenantB);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Update_OfAnotherTenantsRow_AffectsNothing_TEN004()
    {
        await using var connection = await Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand("UPDATE integration.dead_letter_message SET resolution = 'DISCARDED' WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", RowB);

        (await command.ExecuteNonQueryAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AuditEvents_AreInsertOnly_DBA016()
    {
        await using var connection = await Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand("UPDATE audit.audit_event SET outcome = 'SUCCESS'", connection, transaction);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task<NpgsqlConnection> Open()
    {
        var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task SetTenant(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction);
        command.Parameters.AddWithValue("t", tenant.ToString());
        await command.ExecuteScalarAsync();
    }
}
