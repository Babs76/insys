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
using Insys.Documents.Contracts;
using Insys.Party.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// F001 (addenda/due-diligence.md §4.1, signed off 29 October 2026): with an active identity verification partner, an
/// identifier added to a party is checked by the provider. A pass marks it verified with the provider's reference, and a
/// failure is a check that leaves it unverified. Without a partner, a person verifies it against a document filed against
/// the party, which is also a check. The number appears in no check, call log row or event.
/// </summary>
public sealed class IdentityVerificationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    /// <summary>Tenant A has an active identity verification partner; tenant B has none (approved partner rows cannot be changed, SDM-002).</summary>
    private static readonly Guid WithProvider = DatabaseFixture.TenantA;
    private static readonly Guid WithoutProvider = DatabaseFixture.TenantB;
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        foreach (var tenant in new[] { WithProvider, WithoutProvider })
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO party.ref_identity_document_types (tenant_id, code, version, effective_from, record_status, created_by, change_reason, country, doc_type, label, format_pattern)
                VALUES (@t, 'ZA-NID', 1, current_date - 1, 'ACTIVE', 'seed', 'test seed', 'ZA', 'NATIONAL_ID', 'National identity number', '^[0-9]{13}$')
                ON CONFLICT DO NOTHING
                """, ("t", tenant));
            await fixture.Database.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "ID_COPY", TestClock.Today.AddDays(-1), ("category_code", "ID_COPY"),
                ("label", "Identity document"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
            await fixture.Database.UserWithPermissions(tenant, "idv-clerk", PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate,
                DocumentPermissions.DocumentCreate, DocumentPermissions.DocumentRead);
        }

        if (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM integration.mst_partner_register WHERE tenant_id = @t AND code = 'IDV-1'", ("t", WithProvider)) == 0)
        {
            await fixture.Database.SeedStatic(WithProvider, "integration.mst_partner_register", "ACTIVE", "IDV-1", TestClock.Today.AddDays(-1), ("partner_code", "IDV-1"),
                ("partner_type", "IDENTITY_VERIFICATION"), ("legal_name", "Identity checks (stub)"), ("status", "ACTIVE"));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task WithoutAProvider_APersonVerifiesAnIdentifier_AgainstADocumentFiledAgainstTheParty_F001()
    {
        _clerk = _factory.ClientFor(WithoutProvider, "idv-clerk");
        var party = await _clerk.RegisterPerson("Manual");
        var identifier = await AddIdentifier(party, "7501015009081");
        await _factory.DrainOutbox();
        (await Checks(party)).GetArrayLength().ShouldBe(0, "no provider is active, so nothing is sent");

        var url = $"/api/v1/party/parties/{party}/identifiers/{identifier}/verify";
        var notFiled = await _clerk.Send(HttpMethod.Post, url, new { verificationRef = "Seen in branch", documentId = await Document(Guid.CreateVersion7()) },
            ifMatch: await ETag(party));
        notFiled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the document is filed against another record");
        var document = await Document(party);
        var verified = await _clerk.Send(HttpMethod.Post, url, new { verificationRef = "Seen in branch", documentId = document }, ifMatch: await ETag(party));
        verified.StatusCode.ShouldBe(HttpStatusCode.NoContent, await verified.Content.ReadAsStringAsync());

        var check = (await Checks(party)).EnumerateArray().Single();
        (check.GetProperty("checkType").GetString(), check.GetProperty("outcome").GetString(), check.GetProperty("documentId").GetGuid(),
            check.GetProperty("partnerCode").ValueKind).ShouldBe(("IDENTITY", "PASSED", document, System.Text.Json.JsonValueKind.Null));
        check.GetProperty("checkedBy").GetGuid().ShouldBe(await fixture.Database.ScalarAsOwner<Guid>(
            "SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'idv-clerk'", ("t", WithoutProvider)));
        (await Identifier(party)).GetProperty("verified").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task WithAnActiveProvider_AnAddedIdentifierIsChecked_APassVerifiesIt_AndAFailureLeavesItUnverified_F001()
    {
        _clerk = _factory.ClientFor(WithProvider, "idv-clerk");

        var passing = await _clerk.RegisterPerson("Provider");
        await AddIdentifier(passing, "8203045009083");
        var failing = await _clerk.RegisterPerson("NoMatch");
        await AddIdentifier(failing, "8203045000000");
        await _factory.DrainOutbox();

        var passed = (await Checks(passing)).EnumerateArray().Single();
        (passed.GetProperty("outcome").GetString(), passed.GetProperty("partnerCode").GetString(), passed.GetProperty("documentId").ValueKind)
            .ShouldBe(("PASSED", "IDV-1", System.Text.Json.JsonValueKind.Null));
        (await Identifier(passing)).GetProperty("verified").GetBoolean().ShouldBeTrue();

        var failed = (await Checks(failing)).EnumerateArray().Single();
        (failed.GetProperty("outcome").GetString(), failed.GetProperty("reason").GetString()).ShouldBe(("FAILED", "NO_MATCH"));
        (await Identifier(failing)).GetProperty("verified").GetBoolean().ShouldBeFalse();

        // The number is sent to the provider only: not in the call log, the checks or Party's events.
        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM integration.external_call_log WHERE operation = 'party.VerifyIdentity' AND row_to_json(external_call_log)::text LIKE '%820304500%'",
                     "SELECT count(*) FROM party.due_diligence_check WHERE row_to_json(due_diligence_check)::text LIKE '%820304500%'",
                     "SELECT count(*) FROM party.outbox_event WHERE payload::text LIKE '%820304500%'",
                 })
        {
            (await fixture.Database.ScalarAsOwner<long>(sql)).ShouldBe(0L, sql);
        }

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM integration.external_call_log WHERE operation = 'party.VerifyIdentity' AND partner_code = 'IDV-1'"))
            .ShouldBe(2L);
    }

    private async Task<Guid> AddIdentifier(Guid party, string number)
    {
        var added = await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number },
            ifMatch: await ETag(party));
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        return (await Identifier(party)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> Document(Guid businessRefId)
    {
        var stored = await _clerk.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "ID_COPY", businessRefType = "PARTY", businessRefId, mimeType = "text/plain", contentBase64 = Convert.ToBase64String("identity document"u8.ToArray()),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        return (await stored.Json()).GetProperty("id").GetGuid();
    }

    private async Task<string> ETag(Guid party) => (await _clerk.GetAsync($"/api/v1/party/parties/{party}")).ETag();

    private async Task<System.Text.Json.JsonElement> Identifier(Guid party) =>
        (await (await _clerk.GetAsync($"/api/v1/party/parties/{party}")).Json()).GetProperty("identifiers").EnumerateArray().Single();

    private async Task<System.Text.Json.JsonElement> Checks(Guid party)
    {
        var response = await _clerk.GetAsync($"/api/v1/party/parties/{party}/identity-checks");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }
}
