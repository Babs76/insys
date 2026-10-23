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
using System.Net.Http.Json;
using Insys.Party.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// POST /api/v1/party/parties/search (GAP-022): parties by name, exact identifier, role and status, as summaries with
/// cursor paging, each search written to the party access log with an identifier by hash only (PTY-003, PTY-005).
/// </summary>
public sealed class PartySearchTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Number = "8001015009087";
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO party.ref_identity_document_types (tenant_id, code, version, effective_from, record_status, created_by, change_reason, country, doc_type, label, format_pattern)
            VALUES (@t, 'ZA-NID', 1, current_date - 1, 'ACTIVE', 'seed', 'test seed', 'ZA', 'NATIONAL_ID', 'National identity number', '^[0-9]{13}$')
            ON CONFLICT DO NOTHING
            """, ("t", DatabaseFixture.TenantA));
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "search-clerk", PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate);
        _clerk = _factory.ClientFor(DatabaseFixture.TenantA, "search-clerk");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ByName_ByIdentifier_AndByStatus_ReturnsSummaries_WithCursorPaging_GAP022()
    {
        var first = await _clerk.RegisterPerson("Zwelethu");
        var second = await _clerk.RegisterPerson("Zwelethunzi");
        await _clerk.RegisterPerson("Unrelated");
        (await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{first}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number = Number }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var byName = await Search(new { name = "welethu" });
        byName.Select(p => p.GetProperty("id").GetGuid()).ShouldBe([first, second], ignoreOrder: true);
        byName.Where(p => p.TryGetProperty("identifiers", out _) || p.TryGetProperty("individual", out _)).ShouldBeEmpty();

        (await Search(new { identifier = new { docType = "NATIONAL_ID", country = "za", number = Number } })).Single().GetProperty("id").GetGuid().ShouldBe(first);
        (await Search(new { name = "welethu", status = "Active" })).Count.ShouldBe(2);

        var page = await (await _clerk.PostAsJsonAsync("/api/v1/party/parties/search", new { name = "welethu", limit = 1 })).Json();
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        var next = await (await _clerk.PostAsJsonAsync("/api/v1/party/parties/search", new { name = "welethu", limit = 1, cursor = page.GetProperty("nextCursor").GetString() })).Json();
        next.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldNotBe(page.GetProperty("items")[0].GetProperty("id").GetGuid());
        DocumentedSchema.ResponseProblems(page, "core", "PartySearchParties").ShouldBeEmpty();
    }

    [Fact]
    public async Task EverySearch_IsInTheAccessLog_WithTheIdentifierByHashOnly_PTY005()
    {
        await _clerk.RegisterPerson("Loggable");
        await Search(new { name = "Loggable" });
        await Search(new { identifier = new { docType = "NATIONAL_ID", country = "ZA", number = Number } });

        var log = await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(coalesce(name_filter, '-') || '|' || coalesce(identifier_doc_type, '-') || '|' || coalesce(length(identifier_hash)::text, '-')
                              || '|' || result_count, ',' ORDER BY searched_at)
            FROM party.party_search_log WHERE tenant_id = @t AND searched_at > now() - interval '1 minute'
              AND (name_filter = 'Loggable' OR identifier_doc_type = 'NATIONAL_ID')
            """, ("t", DatabaseFixture.TenantA));
        log!.ShouldContain("Loggable|-|-|1,-|NATIONAL_ID|64|");
        (await fixture.Database.ScalarAsOwner<long>(
                "SELECT count(*) FROM party.party_search_log WHERE row_to_json(party_search_log)::text LIKE '%' || @n || '%'", ("n", Number)))
            .ShouldBe(0, "the identifier number is never logged");
        (await fixture.Database.ScalarAsOwner<string>("SELECT searched_by FROM party.party_search_log WHERE name_filter = 'Loggable' LIMIT 1")).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ASearchNeedsAFilter_AndANameOfThreeCharacters()
    {
        (await (await _clerk.PostAsJsonAsync("/api/v1/party/parties/search", new { })).Code()).ShouldBe("VALIDATION_FAILED");
        (await (await _clerk.PostAsJsonAsync("/api/v1/party/parties/search", new { name = "ab" })).Code()).ShouldBe("VALIDATION_FAILED");
    }

    private async Task<List<System.Text.Json.JsonElement>> Search(object filters)
    {
        var response = await _clerk.PostAsJsonAsync("/api/v1/party/parties/search", filters);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("items").EnumerateArray().ToList();
    }
}
