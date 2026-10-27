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
using Insys.Party.Infrastructure;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// OQ-111 (decided 25 October 2026): releasing a legal hold needs a second person, so no one person can release a hold
/// and dispose of what it protected. While the release waits for its LEGAL_HOLD_RELEASE approval the hold is in force and
/// disposal is refused; once a checker approves it, disposal proceeds under the retention rules.
/// </summary>
public sealed class LegalHoldReleaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ADocument_IsNotDisposedOf_WhileItsHoldsReleaseWaits_AndIsOnceTheReleaseIsApproved_OQ111()
    {
        var (factory, tenant, maker, checker) = await World(DocumentApprovals.LegalHoldReleaseType, DocumentPermissions.DocumentCreate, DocumentPermissions.DocumentRead,
            DocumentPermissions.DocumentDispose, DocumentPermissions.LegalHoldManage);
        await fixture.Database.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "NOTE", TestClock.Today.AddDays(-1), ("category_code", "NOTE"),
            ("label", "Note"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await fixture.Database.SeedStatic(tenant, "document.ref_legal_hold_reason_codes", "ACTIVE", "LITIGATION", TestClock.Today.AddDays(-1),
            ("label", "Pending litigation"), ("authority_role", "COMPLIANCE_OFFICER"));
        var stored = await maker.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "NOTE", businessRefType = "PARTY", businessRefId = Guid.CreateVersion7(), mimeType = "text/plain",
            contentBase64 = Convert.ToBase64String("evidence"u8.ToArray()),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        var document = (await stored.Json()).GetProperty("id").GetGuid();

        // Its retention has ended: only the hold keeps it.
        await fixture.Database.ExecuteAsOwner("UPDATE document.document SET retention_until = current_date - 10 WHERE tenant_id = @t AND id = @id", ("t", tenant),
            ("id", document));
        var placed = await maker.Send(HttpMethod.Post, $"/api/v1/documents/documents/{document}/legal-holds", new { reasonCode = "LITIGATION" });
        placed.StatusCode.ShouldBe(HttpStatusCode.OK, await placed.Content.ReadAsStringAsync());
        var hold = (await placed.Json()).GetProperty("id").GetGuid();

        var requested = await maker.Send(HttpMethod.Post, $"/api/v1/documents/documents/{document}/legal-holds/{hold}/release", new { reason = "Litigation settled" });
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted, await requested.Content.ReadAsStringAsync());
        (await requested.Json()).GetProperty("releaseReason").GetString().ShouldBe("Litigation settled");
        (await maker.Send(HttpMethod.Post, $"/api/v1/documents/documents/{document}/legal-holds/{hold}/release", new { reason = "Again" })).StatusCode
            .ShouldBe(HttpStatusCode.Conflict, "a release already waits");
        await factory.DrainOutbox();

        (await Dispose(maker, document)).ShouldBe("DOCUMENT_ON_LEGAL_HOLD", "the hold is in force while its release waits");
        var view = await (await maker.GetAsync($"/api/v1/documents/documents/{document}")).Json();
        view.GetProperty("legalHold").GetBoolean().ShouldBeTrue();

        // The requester cannot approve their own release (SRS-002); another person can.
        var approval = await Waiting(tenant, DocumentApprovals.LegalHoldBusinessRefType, hold);
        (await maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldNotBe(HttpStatusCode.NoContent);
        (await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await factory.DrainOutbox();

        view = await (await maker.GetAsync($"/api/v1/documents/documents/{document}")).Json();
        view.GetProperty("legalHold").GetBoolean().ShouldBeFalse();
        view.GetProperty("holds")[0].GetProperty("releasedAt").ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null);
        (await Dispose(maker, document)).ShouldBeNull("the release was approved and the retention has ended");
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM document.document WHERE id = @id", ("id", document))).ShouldBe("DISPOSED");
    }

    [Fact]
    public async Task SearchLogEntries_AreKept_WhileTheHoldsReleaseWaits_AndDeletedOnceItIsApproved_OQ111()
    {
        var (factory, tenant, maker, checker) = await World(PartyApprovals.LegalHoldReleaseType, PartyPermissions.SearchLogHold, Audit.Contracts.AuditPermissions.Read);
        var searcher = $"clerk-{Guid.NewGuid():N}"[..20];
        var entry = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO party.party_search_log (tenant_id, id, created_at, created_by, searched_at, searched_by, name_filter, result_count)
            VALUES (@t, @id, now(), 'seed', now() - interval '6 years', @by, 'Held', 1)
            """, ("t", tenant), ("id", entry), ("by", searcher));
        var placed = await maker.Send(HttpMethod.Post, "/api/v1/party/search-log/holds", new { searchedBy = searcher, reason = "Investigation 42" }, idempotent: true);
        placed.StatusCode.ShouldBe(HttpStatusCode.Created, await placed.Content.ReadAsStringAsync());
        var hold = (await placed.Json()).GetProperty("id").GetGuid();
        var requested = await maker.Send(HttpMethod.Post, $"/api/v1/party/search-log/holds/{hold}/release", new { reason = "Investigation closed" });
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted, await requested.Content.ReadAsStringAsync());
        await factory.DrainOutbox();

        await factory.Services.GetRequiredService<SearchLogRetentionJob>().Run(CancellationToken.None);
        (await Exists(entry)).ShouldBeTrue("the hold is in force while its release waits");

        var approval = await Waiting(tenant, PartyApprovals.SearchLogHoldBusinessRefType, hold);
        (await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await factory.DrainOutbox();
        var released = (await (await maker.GetAsync("/api/v1/party/search-log/holds")).Json()).EnumerateArray().Single(h => h.GetProperty("id").GetGuid() == hold);
        released.GetProperty("releasedAt").ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null);

        await factory.Services.GetRequiredService<SearchLogRetentionJob>().Run(CancellationToken.None);
        (await Exists(entry)).ShouldBeFalse("the release was approved, so retention deletes the entry");
    }

    /// <summary>
    /// D-34 (27 October 2026): a legal hold on a record itself. It is placed only on a record that exists (404) and is in
    /// the caller's scope (403 OUT_OF_SCOPE); it keeps the record's documents from disposal; and it is released only when
    /// a second person approves (LEGAL_HOLD_RELEASE), the release request taking If-Match.
    /// </summary>
    [Fact]
    public async Task ARecordsHold_IsPlacedOnARecordThatExistsInScope_KeepsItsDocuments_AndIsReleasedByASecondPerson_D34()
    {
        var (factory, tenant, maker, checker) = await World(DocumentApprovals.LegalHoldReleaseType, DocumentPermissions.DocumentCreate, DocumentPermissions.DocumentRead,
            DocumentPermissions.DocumentDispose, DocumentPermissions.LegalHoldManage);
        await fixture.Database.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "NOTE", TestClock.Today.AddDays(-1), ("category_code", "NOTE"),
            ("label", "Note"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await fixture.Database.SeedStatic(tenant, "document.ref_legal_hold_reason_codes", "ACTIVE", "LITIGATION", TestClock.Today.AddDays(-1),
            ("label", "Pending litigation"), ("authority_role", "COMPLIANCE_OFFICER"));
        var policy = await new RowFiller(fixture.Database, DateTimeOffset.UtcNow).Insert(tenant, "general.policy", ("product_code", "GEN-MOTOR"),
            ("org_node_code", "BR-A"));
        var scopedName = $"home-only-{Guid.NewGuid():N}"[..20];
        var scopedId = await fixture.Database.UserWithPermissions(tenant, scopedName, DocumentPermissions.LegalHoldManage);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['GEN-HOME'] WHERE user_id = @u", ("u", scopedId));
        var scoped = factory.ClientFor(tenant, scopedName);

        foreach (var scope in new[] { "PARTY", "LIFE_POLICY", "GENERAL_POLICY", "HEALTH_MEMBERSHIP", "LIFE_CLAIM", "GENERAL_CLAIM", "HEALTH_CLAIM", "CASE" })
        {
            var missing = await maker.Send(HttpMethod.Post, "/api/v1/documents/legal-holds", new { scopeType = scope, scopeId = Guid.CreateVersion7(), reasonCode = "LITIGATION" },
                idempotent: true);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{scope}: {await missing.Content.ReadAsStringAsync()}");
        }

        var outside = await scoped.Send(HttpMethod.Post, "/api/v1/documents/legal-holds", new { scopeType = "GENERAL_POLICY", scopeId = policy, reasonCode = "LITIGATION" },
            idempotent: true);
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await outside.Code()).ShouldBe("OUT_OF_SCOPE");

        var placed = await maker.Send(HttpMethod.Post, "/api/v1/documents/legal-holds", new { scopeType = "GENERAL_POLICY", scopeId = policy, reasonCode = "LITIGATION" },
            idempotent: true);
        placed.StatusCode.ShouldBe(HttpStatusCode.Created, await placed.Content.ReadAsStringAsync());
        var hold = (await placed.Json()).GetProperty("id").GetGuid();
        (await (await scoped.GetAsync($"/api/v1/documents/legal-holds?scopeId={policy}")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0,
            "a hold on a record outside the caller's scope is not listed");
        (await (await maker.GetAsync($"/api/v1/documents/legal-holds?scopeId={policy}&inForce=true")).Json()).GetProperty("items").GetArrayLength().ShouldBe(1);

        // The policy's document is kept by the hold on the policy, though its own retention has ended and it has no hold of its own.
        var stored = await maker.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "NOTE", businessRefType = "GENERAL_POLICY", businessRefId = policy, mimeType = "text/plain",
            contentBase64 = Convert.ToBase64String("schedule"u8.ToArray()),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        var document = (await stored.Json()).GetProperty("id").GetGuid();
        await fixture.Database.ExecuteAsOwner("UPDATE document.document SET retention_until = current_date - 10 WHERE tenant_id = @t AND id = @id", ("t", tenant),
            ("id", document));
        (await Dispose(maker, document)).ShouldBe("DOCUMENT_ON_LEGAL_HOLD");

        // The release takes If-Match, waits for a second person, and the requester cannot approve it.
        (await maker.Send(HttpMethod.Post, $"/api/v1/documents/legal-holds/{hold}/release", new { reason = "Litigation settled" })).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionRequired);
        var read = await maker.GetAsync($"/api/v1/documents/legal-holds/{hold}");
        var requested = await maker.Send(HttpMethod.Post, $"/api/v1/documents/legal-holds/{hold}/release", new { reason = "Litigation settled" }, ifMatch: read.ETag());
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted, await requested.Content.ReadAsStringAsync());
        await factory.DrainOutbox();
        (await Dispose(maker, document)).ShouldBe("DOCUMENT_ON_LEGAL_HOLD", "the hold is in force while its release waits");

        var approval = await Waiting(tenant, DocumentApprovals.RecordHoldBusinessRefType, hold);
        (await maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldNotBe(HttpStatusCode.NoContent);
        (await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await factory.DrainOutbox();

        (await (await maker.GetAsync($"/api/v1/documents/legal-holds/{hold}")).Json()).GetProperty("releasedAt").ValueKind
            .ShouldNotBe(System.Text.Json.JsonValueKind.Null);
        (await Dispose(maker, document)).ShouldBeNull("the release was approved and the retention has ended");
    }

    /// <summary>A tenant of its own with a maker and a checker holding authority for the approval type (the compliance officer's role).</summary>
    private async Task<(InsysApiFactory Factory, Guid Tenant, HttpClient Maker, HttpClient Checker)> World(string approvalType, params string[] permissions)
    {
        var factory = await fixture.Host();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.CreateVersion7();
        var db = fixture.Database;
        await db.SeedTenant(tenant, "LH" + suffix.ToUpperInvariant());
        string[] all = [.. permissions, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove];
        var makerId = await db.UserWithPermissions(tenant, $"maker-{suffix}", all);
        var checkerId = await db.UserWithPermissions(tenant, $"checker-{suffix}", all);
        await db.AssignRole(tenant, makerId, await db.RoleWithPermissions(tenant));
        var officer = await db.RoleWithPermissions(tenant);
        await db.AssignRole(tenant, checkerId, officer);
        await db.SeedStatic(tenant, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{approvalType}-{suffix}", TestClock.Today.AddYears(-1),
            ("role_code", officer), ("transaction_type", approvalType), ("limit_value", 100_000_000m), ("delegation_allowed", false));
        return (factory, tenant, factory.ClientFor(tenant, $"maker-{suffix}"), factory.ClientFor(tenant, $"checker-{suffix}"));
    }

    private Task<Guid> Waiting(Guid tenant, string businessRefType, Guid businessRefId) =>
        fixture.Database.ScalarAsOwner<Guid>("""
            SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @i AND status = 'REQUESTED'
            """, ("t", tenant), ("r", businessRefType), ("i", businessRefId));

    /// <summary>Disposes of the document; null when it was disposed of, otherwise the refusal's code.</summary>
    private static async Task<string?> Dispose(HttpClient client, Guid document)
    {
        var read = await client.GetAsync($"/api/v1/documents/documents/{document}");
        var response = await client.Send(HttpMethod.Post, $"/api/v1/documents/documents/{document}/dispose", ifMatch: read.ETag());
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Code() ?? response.StatusCode.ToString();
    }

    private async Task<bool> Exists(Guid id) =>
        await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party_search_log WHERE id = @id", ("id", id)) == 1;
}
