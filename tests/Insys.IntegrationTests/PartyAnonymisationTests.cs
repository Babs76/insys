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
using System.Text.Json;
using Insys.Testing;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>
/// D-34, OQ-120: AnonymiseParty fires "Retention ended" only once nothing linked to the party keeps its data. Each party
/// is set up on a host with today's clock and anonymised on one eight years ahead (<see cref="Later"/>), with its records
/// reaching their final status relative to that day (the status history the triggers write). Tenant A has its own record
/// retention policies (policies 5 years, claims 3, payments 7); tenant B has the platform's working default, 7 years for
/// each, except payments: this class retires the platform's payment row, so tenant B has no period for payments. Audit
/// classes are kept 1, 2 and 7 years, so the audit evidence of the setup, written today, has expired eight years on.
/// </summary>
public sealed class PartyAnonymisationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Officer = "anonymisation-officer";
    private static readonly DateOnly From = new(2020, 1, 1);
    private static readonly DateTimeOffset Later = DateTimeOffset.UtcNow.AddYears(8);
    private static readonly DateOnly LaterDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Later, TimeZoneInfo.FindSystemTimeZoneById(TestClock.DefaultTimeZone)).DateTime);

    private InsysApiFactory _today = null!;
    private InsysApiFactory _later = null!;

    public async ValueTask InitializeAsync()
    {
        var db = fixture.Database;
        foreach (var (kind, years) in new[] { ("POLICY", 5), ("CLAIM", 3), ("PAYMENT", 7) })
        {
            await db.SeedStatic(DatabaseFixture.TenantA, "party.cfg_record_retention_policies", "EFFECTIVE", "TENA-" + kind, From,
                ("record_kind", kind), ("jurisdiction", "ZA"), ("period", Period($"{years} years")));
        }

        // Fail closed: with the platform's payment row retired, tenant B has no retention period for payments.
        await db.ExecuteAsOwner("UPDATE party.cfg_record_retention_policies SET record_status = 'RETIRED' WHERE tenant_id IS NULL AND record_kind = 'PAYMENT'");
        foreach (var tenant in new[] { DatabaseFixture.TenantA, DatabaseFixture.TenantB })
        {
            foreach (var (eventClass, years) in new[] { ("SECURITY", 1), ("OPERATIONAL", 2), ("FINANCIAL", 7) })
            {
                await db.SeedStatic(tenant, "audit.cfg_audit_retention_policies", "EFFECTIVE", "AUD-" + eventClass, From,
                    ("event_class", eventClass), ("jurisdiction", "ZA"), ("period", Period($"{years} years")));
            }

            await db.UserWithPermissions(tenant, Officer, "party.create", "party.read", "party.update", "party.anonymise");
        }

        // Two hosts on one database: today's for the setup, the later one for anonymising.
        var settings = new Dictionary<string, string?> { ["Identity:PermissionCache:AllowMultipleInstances"] = "true" };
        _today = new InsysApiFactory(db, settings);
        _later = new InsysApiFactory(db, settings, time: new FakeTimeProvider(Later));
    }

    public async ValueTask DisposeAsync()
    {
        await _today.DisposeAsync();
        await _later.DisposeAsync();
    }

    [Fact]
    public async Task APolicyNotYetEnded_BlocksAnonymisation_AsOpen_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "Open");
        var policy = await LifePolicy(DatabaseFixture.TenantA, party, "IN_FORCE", closedYearsAgo: null);

        var blocks = await Blocked(DatabaseFixture.TenantA, party);
        blocks.ShouldBe([$"OPEN LIFE_POLICY {policy} POLICY"]);
    }

    [Fact]
    public async Task AnEndedPolicy_BlocksForTheTenantsOwnPeriod_FromWhenItEnded_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "PolicyEnded");
        var policy = await GeneralPolicy(DatabaseFixture.TenantA, party, "EXPIRED", closedYearsAgo: 1);

        // Tenant A's own five years replace the platform's seven.
        (await Blocked(DatabaseFixture.TenantA, party)).ShouldBe([$"IN_RETENTION GENERAL_POLICY {policy} POLICY {Ago(1).AddYears(5):yyyy-MM-dd}"]);
    }

    [Fact]
    public async Task WithNoPolicyOfItsOwn_ATenantKeepsAnEndedPolicyForTheWorkingDefault_SevenYears_OQ120()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantB, "DefaultPeriod");
        var policy = await GeneralPolicy(DatabaseFixture.TenantB, party, "CANCELLED", closedYearsAgo: 6);

        (await Blocked(DatabaseFixture.TenantB, party)).ShouldBe([$"IN_RETENTION GENERAL_POLICY {policy} POLICY {Ago(6).AddYears(7):yyyy-MM-dd}"]);
    }

    [Fact]
    public async Task AClosedClaim_BlocksFromWhenItClosed_ByItsStatusHistory_NotItsLastChange_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "ClaimClosed");
        var claim = await GeneralClaim(DatabaseFixture.TenantA, party, "CLOSED", closedYearsAgo: 2);
        // Changed again a year later without a status change: its retention still runs from its closure.
        await fixture.Database.ExecuteAsOwner("UPDATE general.claim SET description = 'Corrected', updated_at = @at WHERE id = @c", ("at", Later.AddYears(-1)), ("c", claim));

        (await Blocked(DatabaseFixture.TenantA, party)).ShouldBe([$"IN_RETENTION GENERAL_CLAIM {claim} CLAIM {Ago(2).AddYears(3):yyyy-MM-dd}"]);
    }

    [Fact]
    public async Task AReopenedClaim_IsOpenAgain_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "Reopened");
        var claim = await GeneralClaim(DatabaseFixture.TenantA, party, "CLOSED", closedYearsAgo: 10);
        await fixture.Database.ExecuteAsOwner("UPDATE general.claim SET status = 'REOPENED', updated_at = @at WHERE id = @c", ("at", Later.AddDays(-1)), ("c", claim));

        (await Blocked(DatabaseFixture.TenantA, party)).ShouldBe([$"OPEN GENERAL_CLAIM {claim} CLAIM"]);
    }

    [Fact]
    public async Task APayment_BlocksFromItsOutcome_InItsStatusHistory_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "Paid");
        var payment = await Payment(DatabaseFixture.TenantA, party, "SUCCEEDED", paidYearsAgo: 6);

        (await Blocked(DatabaseFixture.TenantA, party)).ShouldBe([$"IN_RETENTION PAYMENT {payment} PAYMENT {Ago(6).AddYears(7):yyyy-MM-dd}"]);
    }

    [Fact]
    public async Task TheAuditEvidenceAboutTheParty_BlocksUntilTheLongestAuditClassHasRun_D34()
    {
        // Registered and recorded as deceased today: the audit evidence of both is kept seven years (the longest class).
        var party = await DeceasedParty(DatabaseFixture.TenantA, "Audited");

        var anonymise = await Anonymise(_today, DatabaseFixture.TenantA, party);
        anonymise.StatusCode.ShouldBe(HttpStatusCode.Conflict, await anonymise.Content.ReadAsStringAsync());
        var block = (await anonymise.Json()).GetProperty("blocks").EnumerateArray().Single();
        block.GetProperty("reason").GetString().ShouldBe("IN_RETENTION");
        block.GetProperty("recordType").GetString().ShouldBe("AUDIT_EVIDENCE");
        block.GetProperty("recordId").GetGuid().ShouldBe(party);
        block.GetProperty("retention").GetString().ShouldBe("AUDIT");
        DateOnly.Parse(block.GetProperty("until").GetString()!).ShouldBeGreaterThan(TestClock.Today.AddYears(7).AddDays(-2));
    }

    [Fact]
    public async Task ALegalHoldOnADocumentOfTheClaim_BlocksAfterRetentionHasEnded_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "Held");
        var claim = await GeneralClaim(DatabaseFixture.TenantA, party, "CLOSED", closedYearsAgo: 10);
        var (document, hold) = await HeldDocument(DatabaseFixture.TenantA, "GENERAL_CLAIM", claim);

        var anonymise = await Anonymise(_later, DatabaseFixture.TenantA, party);
        anonymise.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await anonymise.Json();
        body.GetProperty("code").GetString().ShouldBe("PARTY_ANONYMISATION_BLOCKED");
        var block = body.GetProperty("blocks").EnumerateArray().Single();
        block.GetProperty("reason").GetString().ShouldBe("LEGAL_HOLD");
        block.GetProperty("recordType").GetString().ShouldBe("GENERAL_CLAIM");
        block.GetProperty("recordId").GetGuid().ShouldBe(claim);
        block.GetProperty("holdId").GetGuid().ShouldBe(hold);
        block.GetProperty("documentId").GetGuid().ShouldBe(document);
    }

    [Fact]
    public async Task WithNoRetentionPeriodInForceForPayments_APaymentBlocks_FailClosed_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantB, "Unconfigured");
        var payment = await Payment(DatabaseFixture.TenantB, party, "SUCCEEDED", paidYearsAgo: 20);

        (await Blocked(DatabaseFixture.TenantB, party)).ShouldBe([$"NO_RETENTION_POLICY PAYMENT {payment} PAYMENT"]);
    }

    [Fact]
    public async Task ARecordOfAPartyMergedIntoIt_BlocksTheSurvivor_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "Survivor");
        var merged = await Insert(DatabaseFixture.TenantA, "party.party", ("status_code", "MERGED"), ("party_kind", "INDIVIDUAL"), ("merged_into_id", party));
        var membership = await Insert(DatabaseFixture.TenantA, "health.membership", ("status", "ACTIVE"), ("principal_party_id", merged));

        (await Blocked(DatabaseFixture.TenantA, party)).ShouldBe([$"OPEN HEALTH_MEMBERSHIP {membership} POLICY"]);
    }

    [Fact]
    public async Task APartyWhoseRecordsAreAllPastRetention_AndNotHeld_IsAnonymised_D34()
    {
        var party = await DeceasedParty(DatabaseFixture.TenantA, "Expired");
        await LifePolicy(DatabaseFixture.TenantA, party, "MATURED", closedYearsAgo: 5);
        await GeneralPolicy(DatabaseFixture.TenantA, party, "CANCELLED", closedYearsAgo: 6);
        var claim = await GeneralClaim(DatabaseFixture.TenantA, party, "CLOSED", closedYearsAgo: 3);
        await Payment(DatabaseFixture.TenantA, party, "FAILED", paidYearsAgo: 7);
        // A released hold no longer counts.
        var (_, hold) = await HeldDocument(DatabaseFixture.TenantA, "GENERAL_CLAIM", claim);
        await fixture.Database.ExecuteAsOwner("UPDATE document.legal_hold SET released_at = now() WHERE id = @h; UPDATE document.document SET legal_hold = false WHERE id = (SELECT scope_id FROM document.legal_hold WHERE id = @h)", ("h", hold));

        var anonymise = await Anonymise(_later, DatabaseFixture.TenantA, party);
        anonymise.StatusCode.ShouldBe(HttpStatusCode.NoContent, await anonymise.Content.ReadAsStringAsync());
        var read = await (await _later.ClientFor(DatabaseFixture.TenantA, Officer).GetAsync($"/api/v1/party/parties/{party}")).Json();
        read.GetProperty("status").GetString().ShouldBe("Anonymised");
        read.GetProperty("displayName").GetString().ShouldBe("Anonymised");
    }

    [Fact]
    public async Task AnActiveParty_IsRefusedAsAnInvalidTransition_BeforeItsRecordsAreRead_D34()
    {
        var client = _today.ClientFor(DatabaseFixture.TenantA, Officer);
        var party = await client.RegisterPerson("Living");

        var anonymise = await Anonymise(_today, DatabaseFixture.TenantA, party);
        anonymise.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await anonymise.Code()).ShouldBe("INVALID_STATE_TRANSITION");
    }

    /// <summary>The blocks of a refused anonymisation on the later host, as "reason recordType recordId retention [until]".</summary>
    private async Task<string[]> Blocked(Guid tenant, Guid party)
    {
        var anonymise = await Anonymise(_later, tenant, party);
        anonymise.StatusCode.ShouldBe(HttpStatusCode.Conflict, await anonymise.Content.ReadAsStringAsync());
        var body = await anonymise.Json();
        body.GetProperty("code").GetString().ShouldBe("PARTY_ANONYMISATION_BLOCKED");
        return body.GetProperty("blocks").EnumerateArray().Select(b => string.Join(' ', new[]
        {
            b.GetProperty("reason").GetString(), b.GetProperty("recordType").GetString(), b.GetProperty("recordId").GetString(), b.GetProperty("retention").GetString(),
            b.TryGetProperty("until", out var until) && until.ValueKind == JsonValueKind.String ? until.GetString() : null,
        }.Where(s => s is not null))).ToArray();
    }

    private async Task<HttpResponseMessage> Anonymise(InsysApiFactory host, Guid tenant, Guid party)
    {
        var client = host.ClientFor(tenant, Officer);
        var read = await client.GetAsync($"/api/v1/party/parties/{party}");
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/anonymise", ifMatch: read.ETag());
    }

    /// <summary>A person registered and recorded as deceased today, with the audit events of both recorded.</summary>
    private async Task<Guid> DeceasedParty(Guid tenant, string surname)
    {
        var client = _today.ClientFor(tenant, Officer);
        var party = await client.RegisterPerson(surname);
        var read = await client.GetAsync($"/api/v1/party/parties/{party}");
        var death = await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/death", new { deceasedDate = TestClock.Today.AddDays(-1) }, ifMatch: read.ETag());
        death.StatusCode.ShouldBe(HttpStatusCode.NoContent, await death.Content.ReadAsStringAsync());
        await _today.DrainOutbox();
        return party;
    }

    private Task<Guid> LifePolicy(Guid tenant, Guid party, string status, int? closedYearsAgo) =>
        Insert(tenant, "life.policy", Closed(closedYearsAgo, ("status", status), ("policyholder_party_id", party), ("term_end_date", Ago(closedYearsAgo ?? -10))));

    private Task<Guid> GeneralPolicy(Guid tenant, Guid party, string status, int closedYearsAgo) =>
        Insert(tenant, "general.policy", Closed(closedYearsAgo, ("status", status), ("policyholder_party_id", party), ("period_end", Ago(closedYearsAgo))));

    private async Task<Guid> GeneralClaim(Guid tenant, Guid party, string status, int closedYearsAgo)
    {
        var claim = await Insert(tenant, "general.claim", Closed(closedYearsAgo, ("status", status)));
        await Insert(tenant, "general.claim_party", ("claim_id", claim), ("party_id", party), ("role", "CLAIMANT"));
        return claim;
    }

    /// <summary>A payment with its outcome in its status history that many years ago (Payments writes that history), changed since.</summary>
    private async Task<Guid> Payment(Guid tenant, Guid party, string status, int paidYearsAgo)
    {
        var payment = await Insert(tenant, "payment.payment", ("status", status), ("counterparty_party_id", party), ("updated_at", Later.AddDays(-1)));
        await Insert(tenant, "payment.payment_status_history", ("payment_id", payment), ("from_status", "PENDING"), ("to_status", status), ("changed_at", Later.AddYears(-paidYearsAgo)));
        return payment;
    }

    /// <summary>A document of the record with a legal hold in force on it.</summary>
    private async Task<(Guid Document, Guid Hold)> HeldDocument(Guid tenant, string refType, Guid refId)
    {
        var document = await Insert(tenant, "document.document", ("business_ref_type", refType), ("business_ref_id", refId), ("legal_hold", true), ("sha256", new string('a', 64)));
        var hold = await Insert(tenant, "document.legal_hold", ("scope_type", "DOCUMENT"), ("scope_id", document), ("reason_code", "LITIGATION"));
        return (document, hold);
    }

    private static DateOnly Ago(int years) => LaterDay.AddYears(-years);

    /// <summary>The record's last change, which dates a closure recorded only by status, that many years before the later day.</summary>
    private static (string, object)[] Closed(int? yearsAgo, params (string, object)[] values) =>
        yearsAgo is { } years ? [.. values, ("updated_at", Later.AddYears(-years))] : values;

    private static object Period(string years) => new NpgsqlTypes.NpgsqlInterval(int.Parse(years.Split(' ')[0]) * 12, 0, 0);

    /// <summary>Values the table's checks need where a type's default would not do.</summary>
    private static readonly Dictionary<string, (string Column, object Value)[]> Defaults = new(StringComparer.Ordinal)
    {
        ["general.policy"] = [("status", "IN_FORCE"), ("cover_starts_on", "ISSUE"), ("period_start", LaterDay.AddYears(-31))],
        ["life.proposal"] = [("status", "DRAFT")],
        ["document.document"] = [("status", "GENERATED")],
        ["general.claim"] = [("claim_number", "GC-" + Guid.NewGuid().ToString("N")[..10])],
        ["payment.payment"] = [("direction", "OUT")],
    };

    /// <summary>
    /// Inserts a row as the owner: the given values, the table's <see cref="Defaults"/>, a filled parent row for each
    /// required foreign key, and for every other required column with no default a value of its type. Returns its id.
    /// </summary>
    private async Task<Guid> Insert(Guid tenant, string table, params (string Column, object Value)[] values)
    {
        var id = Guid.CreateVersion7();
        var given = new Dictionary<string, object>(StringComparer.Ordinal) { ["tenant_id"] = tenant, ["id"] = id };
        foreach (var (column, value) in Defaults.GetValueOrDefault(table, []).Concat(values))
        {
            given[column] = column == "claim_number" && value is string number && number.StartsWith("GC-", StringComparison.Ordinal)
                ? "GC-" + Guid.NewGuid().ToString("N")[..10] : value;
        }

        var parts = table.Split('.');
        var required = new List<(string Name, string Type, int? Length)>();
        var parents = new List<(string Column, string Table)>();
        await using (var connection = new NpgsqlConnection(fixture.Database.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using (var columns = new NpgsqlCommand("""
                SELECT column_name, data_type, character_maximum_length FROM information_schema.columns
                WHERE table_schema = @s AND table_name = @n AND is_nullable = 'NO' AND column_default IS NULL
                """, connection))
            {
                columns.Parameters.AddWithValue("s", parts[0]);
                columns.Parameters.AddWithValue("n", parts[1]);
                await using var reader = await columns.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    required.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
                }
            }

            // Each foreign key on (tenant_id, column) to another table's (tenant_id, id).
            await using (var keys = new NpgsqlCommand("""
                SELECT a.attname, c.confrelid::regclass::text FROM pg_constraint c
                JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[2]
                WHERE c.contype = 'f' AND c.conrelid = (@s || '.' || @n)::regclass AND array_length(c.conkey, 1) = 2 AND c.confrelid <> c.conrelid
                """, connection))
            {
                keys.Parameters.AddWithValue("s", parts[0]);
                keys.Parameters.AddWithValue("n", parts[1]);
                await using var reader = await keys.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    parents.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        foreach (var (column, parent) in parents.Where(p => !given.ContainsKey(p.Column) && required.Any(r => r.Name == p.Column)))
        {
            given[column] = await Insert(tenant, parent);
        }

        foreach (var (name, type, length) in required.Where(c => !given.ContainsKey(c.Name)))
        {
            given[name] = type switch
            {
                "uuid" => Guid.CreateVersion7(),
                "date" => LaterDay.AddYears(-30),
                "timestamp with time zone" => Later.AddYears(-30),
                "boolean" => false,
                "integer" or "smallint" => 1,
                "bigint" => 1L,
                "numeric" => 1m,
                "character" when length == 3 => "ZAR",
                _ => ("T" + Guid.NewGuid().ToString("N"))[..Math.Min(length ?? 12, 12)],
            };
        }

        await using var insertConnection = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        await insertConnection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO {table} ({string.Join(", ", given.Keys)}) VALUES ({string.Join(", ", given.Keys.Select((_, i) => $"@p{i}"))})", insertConnection);
        foreach (var (value, i) in given.Values.Select((v, i) => (v, i)))
        {
            insert.Parameters.AddWithValue($"p{i}", value);
        }

        await insert.ExecuteNonQueryAsync();
        return id;
    }
}
