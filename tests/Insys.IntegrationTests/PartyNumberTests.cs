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
using System.Net;
using System.Text.RegularExpressions;
using Insys.DbMigrator;
using Insys.Party.Contracts;
using Insys.Party.Domain.Parties;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-029 (decided 28 October 2026): every party gets a number on registration, P + 8 digits from its tenant's counter +
/// a Luhn check digit; it is never changed or reused, carries no personal data, and is searchable. Existing parties are
/// numbered once each, in registration order, per tenant.
/// </summary>
public sealed partial class PartyNumberTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "number-clerk", PartyPermissions.PartyCreate, PartyPermissions.PartyRead);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "number-clerk-b", PartyPermissions.PartyCreate, PartyPermissions.PartyRead);
        _clerk = _factory.ClientFor(DatabaseFixture.TenantA, "number-clerk");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [GeneratedRegex("^P[0-9]{9}$")]
    private static partial Regex Format();

    [Fact]
    public async Task Registration_AssignsTheTenantsNextNumber_WithItsCheckDigit_AndTheSearchFindsIt_BR029()
    {
        var first = await _clerk.RegisterPerson("Numbered");
        var second = await _clerk.RegisterPerson("Numbered");
        var numbers = new List<string>();
        foreach (var party in new[] { first, second })
        {
            var view = await (await _clerk.GetAsync($"/api/v1/party/parties/{party}")).Json();
            numbers.Add(view.GetProperty("partyNumber").GetString()!);
        }

        numbers.ShouldAllBe(n => Format().IsMatch(n) && PartyNumbers.IsValid(n));
        (long.Parse(numbers[1][1..9]) - long.Parse(numbers[0][1..9])).ShouldBeGreaterThan(0, "the tenant's counter goes up");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party p WHERE p.party_number <> party.party_number(substr(p.party_number, 2, 8)::bigint)"))
            .ShouldBe(0, "every number is its counter value and check digit");

        // Each tenant has its own counter.
        var other = await _factory.ClientFor(DatabaseFixture.TenantB, "number-clerk-b").RegisterPerson("Elsewhere");
        (await fixture.Database.ScalarAsOwner<long>("SELECT last_value FROM party.party_number_counter WHERE tenant_id = @t", ("t", DatabaseFixture.TenantB)))
            .ShouldBe(await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party WHERE tenant_id = @t", ("t", DatabaseFixture.TenantB)));
        other.ShouldNotBe(Guid.Empty);

        // Search by number, logged; a number whose check digit is wrong is refused.
        var found = await _clerk.Send(HttpMethod.Post, "/api/v1/party/parties/search", new { partyNumber = numbers[0] });
        found.StatusCode.ShouldBe(HttpStatusCode.OK, await found.Content.ReadAsStringAsync());
        var items = (await found.Json()).GetProperty("items").EnumerateArray().ToList();
        items.Select(p => p.GetProperty("id").GetGuid()).ShouldBe([first]);
        items[0].GetProperty("partyNumber").GetString().ShouldBe(numbers[0]);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party_search_log WHERE party_number_filter = @n", ("n", numbers[0]))).ShouldBe(1);
        var wrong = numbers[0][..9] + (char)('0' + (numbers[0][9] - '0' + 1) % 10);
        (await _clerk.Send(HttpMethod.Post, "/api/v1/party/parties/search", new { partyNumber = wrong })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Never changed, even by the module's own role.
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_party"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenant = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DatabaseFixture.TenantA}', true)", connection, transaction))
        {
            await tenant.ExecuteNonQueryAsync();
        }

        await using var change = new NpgsqlCommand("UPDATE party.party SET party_number = 'P000000026' WHERE id = @id", connection, transaction);
        change.Parameters.AddWithValue("id", first);
        (await Should.ThrowAsync<PostgresException>(() => change.ExecuteNonQueryAsync())).MessageText.ShouldContain("never changed");
    }

    [Fact]
    public void TheCheckDigit_IsLuhnMod10_BR029()
    {
        PartyNumbers.CheckDigit("7992739871").ShouldBe(3, "the textbook Luhn example");
        PartyNumbers.CheckDigit("00000001").ShouldBe(8);
        PartyNumbers.IsValid("P000000018").ShouldBeTrue();
        PartyNumbers.IsValid("P000000019").ShouldBeFalse();
        PartyNumbers.IsValid("p000000018").ShouldBeFalse();
        PartyNumbers.IsValid("P00000001").ShouldBeFalse();
    }

    /// <summary>The migration on a database that already has parties: each is numbered once, in registration order, per tenant.</summary>
    [Fact]
    public async Task ExistingParties_AreNumberedOnceEach_InRegistrationOrder_PerTenant_V0071()
    {
        var database = await fixture.Server.CreateEmptyDatabase();
        var directory = Path.Combine(Path.GetTempPath(), $"insys-v0071-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(RepositoryPaths.Root, "db", "migrations");
        foreach (var file in Directory.GetFiles(source, "V*.sql").Where(f => string.CompareOrdinal(Path.GetFileName(f), "V0071") < 0))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        await new MigrationRunner(database).Migrate(directory);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var registered = new List<(Guid Tenant, Guid Id, int Day)>
        {
            (a, Guid.CreateVersion7(), 3), (a, Guid.CreateVersion7(), 1), (b, Guid.CreateVersion7(), 2), (a, Guid.CreateVersion7(), 2),
        };
        await using (var connection = new NpgsqlConnection(database))
        {
            await connection.OpenAsync();
            foreach (var (tenant, id, day) in registered)
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO party.party (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, party_kind, display_name, status_code)
                    VALUES (@t, @id, 1, @at, 'seed', @at, 'seed', 'INDIVIDUAL', 'Before V0071', 'ACTIVE')
                    """, connection);
                insert.Parameters.AddWithValue("t", tenant);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("at", new DateTimeOffset(2026, 1, day, 9, 0, 0, TimeSpan.Zero));
                await insert.ExecuteNonQueryAsync();
            }
        }

        File.Copy(Directory.GetFiles(source, "V0071__*.sql").Single(), Path.Combine(directory, "V0071__party_number.sql"));
        await new MigrationRunner(database).Migrate(directory);

        await using var check = new NpgsqlConnection(database);
        await check.OpenAsync();
        var numbers = new Dictionary<Guid, string>();
        await using (var read = new NpgsqlCommand("SELECT id, party_number FROM party.party", check))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                numbers[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        string Expected(int n) => $"P{n:D8}{PartyNumbers.CheckDigit($"{n:D8}")}";
        foreach (var (tenant, ids) in registered.GroupBy(r => r.Tenant).Select(g => (g.Key, g.OrderBy(r => r.Day).Select(r => r.Id).ToList())))
        {
            ids.Select(id => numbers[id]).ShouldBe(ids.Select((_, i) => Expected(i + 1)), $"tenant {tenant}: once each, in registration order");
        }

        await using var counter = new NpgsqlCommand("SELECT last_value FROM party.party_number_counter WHERE tenant_id = @t", check);
        counter.Parameters.AddWithValue("t", a);
        ((long)(await counter.ExecuteScalarAsync())!).ShouldBe(3, "the next registration continues after the backfill");
    }
}
