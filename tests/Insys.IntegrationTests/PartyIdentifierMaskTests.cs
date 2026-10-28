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
using Insys.Party.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-026: a party's identifier is returned only masked, with its last four characters ("•••• 1234") and never more, also
/// for an identifier added before V0069 stored its last four; the number itself never appears in the party's view.
/// </summary>
public sealed class PartyIdentifierMaskTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Number = "8001015009087";
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO party.ref_identity_document_types (tenant_id, code, version, effective_from, record_status, created_by, change_reason, country, doc_type, label, format_pattern)
            VALUES (@t, 'ZA-NID', 1, current_date - 1, 'ACTIVE', 'seed', 'test seed', 'ZA', 'NATIONAL_ID', 'National identity number', '^[0-9]{13}$'),
                   (@t, 'ZA-PIN', 1, current_date - 1, 'ACTIVE', 'seed', 'test seed', 'ZA', 'SHORT_PIN', 'Short reference', '^[0-9]{4}$')
            ON CONFLICT DO NOTHING
            """, ("t", DatabaseFixture.TenantA));
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "mask-clerk", PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate);
        _clerk = _factory.ClientFor(DatabaseFixture.TenantA, "mask-clerk");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AnIdentifier_IsReturnedMasked_WithAtMostItsLastFour_BR026()
    {
        var party = await _clerk.RegisterPerson("Masked");
        var added = await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number = Number },
            ifMatch: "\"1\"");
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        var shortAdded = await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/identifiers", new { docType = "SHORT_PIN", country = "ZA", number = "4321" },
            ifMatch: (await _clerk.GetAsync($"/api/v1/party/parties/{party}")).ETag());
        shortAdded.StatusCode.ShouldBe(HttpStatusCode.Created, await shortAdded.Content.ReadAsStringAsync());

        async Task<Dictionary<string, string>> Masked()
        {
            var response = await _clerk.GetAsync($"/api/v1/party/parties/{party}");
            var text = await response.Content.ReadAsStringAsync();
            text.ShouldNotContain(Number[..^4], customMessage: "never more than the last four");
            text.ShouldNotContain("4321", customMessage: "a number of four or fewer is never shown");
            return (await response.Json()).GetProperty("identifiers").EnumerateArray()
                .ToDictionary(i => i.GetProperty("docType").GetString()!, i => i.GetProperty("maskedNumber").GetString()!);
        }

        (await Masked()).ShouldBe(new Dictionary<string, string> { ["NATIONAL_ID"] = "•••• 9087", ["SHORT_PIN"] = "••••" });
        (await fixture.Database.ScalarAsOwner<string>("SELECT number_last4 FROM party.party_identifier WHERE party_id = @p AND doc_type = 'NATIONAL_ID'", ("p", party)))
            .ShouldBe("9087");

        // An identifier added before V0069 has no last four stored; it is masked the same, from its number.
        await fixture.Database.ExecuteAsOwner("UPDATE party.party_identifier SET number_last4 = NULL WHERE party_id = @p", ("p", party));
        (await Masked()).ShouldBe(new Dictionary<string, string> { ["NATIONAL_ID"] = "•••• 9087", ["SHORT_PIN"] = "••••" });
    }
}
