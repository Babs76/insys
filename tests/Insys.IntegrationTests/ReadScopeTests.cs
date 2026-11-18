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
using Insys.Health.Contracts;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// F103 (addenda/access-scopes.md §1), the read-scope sweep's fix batches from 2 on: a record's child records are read only within the parent's scope, and outside
/// it the parent isn't found. Each caller here holds one product.
/// </summary>
public sealed class ReadScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;
    private RowFiller _rows = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        _rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Scope batch 2 (read sweep row 1): a membership's benefit accumulators are read only within the membership's scope.</summary>
    [Fact]
    public async Task AMembershipsAccumulators_AreReadOnlyWithinItsScope_OutsideItIsNotFound_F103()
    {
        var client = await Scoped("rs-health", "RS-HEALTH", HealthPermissions.AccumulatorRead, HealthPermissions.MembershipRead);
        var outside = await _rows.Insert(T, "health.membership", ("status", "ACTIVE"), ("product_code", "RS-OTHER"), ("member_number", $"RS{Random.Shared.Next(1_000_000, 9_999_999)}"));
        var inside = await _rows.Insert(T, "health.membership", ("status", "ACTIVE"), ("product_code", "RS-HEALTH"), ("member_number", $"RS{Random.Shared.Next(1_000_000, 9_999_999)}"));
        foreach (var membership in new[] { outside, inside })
        {
            await _rows.Insert(T, "health.benefit_accumulator", ("membership_id", membership), ("benefit_category", "GP"));
        }

        var refused = await client.GetAsync($"/api/v1/health/memberships/{outside}/accumulators");
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a read outside the scope finds nothing (§1)");

        var read = await client.GetAsync($"/api/v1/health/memberships/{inside}/accumulators");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        (await read.Json()).GetArrayLength().ShouldBe(1);
    }

    /// <summary>Scope batch 3 (read sweep, Party's policy export): an export holds only the policies its reader's scope allows, filtered in the query.</summary>
    [Fact]
    public async Task APolicyExport_HoldsOnlyThePoliciesItsReadersScopeAllows_F103()
    {
        var suffix = Random.Shared.Next(100_000, 999_999);
        var (inside, outside) = ($"RI{suffix}", $"RO{suffix}");
        await _rows.Insert(T, "general.policy", ("product_code", "RS-GEN"), ("policy_number", inside), ("status", "IN_FORCE"));
        await _rows.Insert(T, "general.policy", ("product_code", "RS-GEN-OTHER"), ("policy_number", outside), ("status", "IN_FORCE"));

        var scoped = await Scoped("rs-exporter", "RS-GEN", Insys.Party.Contracts.PartyPermissions.BulkJobCreate, Insys.Party.Contracts.PartyPermissions.BulkJobRead);
        await fixture.Database.UserWithPermissions(T, "rs-export-checker", Insys.Party.Contracts.PartyPermissions.BulkJobApprove,
            Insys.Party.Contracts.PartyPermissions.BulkJobRead);
        await fixture.Database.UserWithPermissions(T, "rs-export-all", Insys.Party.Contracts.PartyPermissions.BulkJobRead);
        var requested = await scoped.Send(HttpMethod.Post, "/api/v1/party/bulk-jobs/policy-exports", new { domain = "GENERAL" }, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var job = (await requested.Json()).GetProperty("id").GetGuid();
        var checker = _factory.ClientFor(T, "rs-export-checker");
        var etag = (await checker.GetAsync($"/api/v1/party/bulk-jobs/{job}")).Headers.ETag?.Tag;
        var approved = await checker.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/approve", ifMatch: etag);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());

        var mine = await (await scoped.GetAsync($"/api/v1/party/bulk-jobs/{job}/content")).Content.ReadAsStringAsync();
        mine.ShouldContain(inside);
        mine.ShouldNotContain(outside, Case.Sensitive, "a policy outside the reader's scope isn't exported");

        var all = await (await _factory.ClientFor(T, "rs-export-all").GetAsync($"/api/v1/party/bulk-jobs/{job}/content")).Content.ReadAsStringAsync();
        all.ShouldContain(outside, Case.Sensitive, "an unrestricted reader gets every policy");
    }

    /// <summary>
    /// Scope batch 4 (read sweep, OQ-169): a process started about a membership records the membership's scope, and the process, its tasks and the open task
    /// list follow it, filtered in the query.
    /// </summary>
    [Fact]
    public async Task AProcessAndItsTasks_FollowTheirRecordsScope_F103()
    {
        var number = Random.Shared.Next(1_000_000, 9_999_999);
        var inside = await _rows.Insert(T, "health.membership", ("status", "SUSPENDED"), ("product_code", "RS-HP"), ("member_number", $"RT{number}"));
        var outside = await _rows.Insert(T, "health.membership", ("status", "SUSPENDED"), ("product_code", "RS-HP-OTHER"), ("member_number", $"RU{number}"));
        foreach (var (membership, member) in new[] { (inside, $"RT{number}"), (outside, $"RU{number}") })
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO health.outbox_event (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, aggregate_type, aggregate_id,
                                                 event_type, event_version, payload, occurred_at, correlation_id, publish_attempts)
                VALUES (@t, gen_random_uuid(), 1, now(), 'test', now(), 'test', 'Membership', @m, 'health.MembershipTerminationDue', 1,
                        jsonb_build_object('membershipId', @m, 'memberNumber', @n, 'arrearsSince', '2026-09-01'), now(), gen_random_uuid(), 0)
                """, ("t", T), ("m", membership), ("n", member));
        }

        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("SELECT product_code FROM workflow.process_instance WHERE tenant_id = @t AND business_ref_id = @m",
            ("t", T), ("m", inside))).ShouldBe("RS-HP", "the process records its membership's scope when it starts");
        var hidden = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM workflow.process_instance WHERE tenant_id = @t AND business_ref_id = @m", ("t", T), ("m", outside));

        var client = await Scoped("rs-process", "RS-HP", Insys.Workflow.Contracts.WorkflowPermissions.ProcessRead);
        var listed = await (await client.GetAsync("/api/v1/workflow/processes?businessRefType=HEALTH_MEMBERSHIP&limit=200")).Json();
        var ids = listed.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("businessRefId").GetGuid()).ToList();
        ids.ShouldContain(inside);
        ids.ShouldNotContain(outside);
        (await client.GetAsync($"/api/v1/workflow/processes/{hidden}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var tasks = await (await client.GetAsync("/api/v1/workflow/tasks?limit=200")).Content.ReadAsStringAsync();
        tasks.ShouldNotContain(hidden.ToString(), Case.Insensitive, "a task follows its process");
    }

    /// <summary>Scope batch 4 (OQ-169): a document filed against a scoped record follows it; an unattached document keeps its classification only.</summary>
    [Fact]
    public async Task ADocumentFiledAgainstAScopedRecord_FollowsItsScope_AndAnUnattachedOneDoesNot_OQ169()
    {
        var suffix = Random.Shared.Next(100_000, 999_999);
        var inside = await _rows.Insert(T, "general.policy", ("product_code", "RS-DOC"), ("policy_number", $"RD{suffix}"), ("status", "IN_FORCE"));
        var outside = await _rows.Insert(T, "general.policy", ("product_code", "RS-DOC-OTHER"), ("policy_number", $"RE{suffix}"), ("status", "IN_FORCE"));
        await fixture.Database.SeedStatic(T, "document.ref_document_categories", "ACTIVE", "RS_NOTE", TestClock.Today.AddDays(-1), ("category_code", "RS_NOTE"),
            ("label", "Note"), ("classification", "INTERNAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await fixture.Database.UserWithPermissions(T, "rs-doc-maker", Insys.Documents.Contracts.DocumentPermissions.DocumentCreate);
        var maker = _factory.ClientFor(T, "rs-doc-maker");
        async Task<Guid> Store(string refType, Guid refId) => (await (await maker.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "RS_NOTE", businessRefType = refType, businessRefId = refId, mimeType = "text/plain", contentBase64 = Convert.ToBase64String("sample"u8.ToArray()),
        }, idempotent: true)).Json()).GetProperty("id").GetGuid();
        var mine = await Store("GENERAL_POLICY", inside);
        var theirs = await Store("GENERAL_POLICY", outside);
        // Filed against a party, which has no scope (PTY-003): unattached. A party that doesn't exist would fail closed instead.
        var unattached = await Store("PARTY", await _rows.Insert(T, "party.party"));

        var reader = await Scoped("rs-doc-reader", "RS-DOC", Insys.Documents.Contracts.DocumentPermissions.DocumentRead);
        (await reader.GetAsync($"/api/v1/documents/documents/{mine}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reader.GetAsync($"/api/v1/documents/documents/{theirs}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await reader.GetAsync($"/api/v1/documents/documents/{unattached}")).StatusCode.ShouldBe(HttpStatusCode.OK, "unattached: classification only");
        var listed = await (await reader.GetAsync($"/api/v1/documents/documents?businessRefType=GENERAL_POLICY&businessRefId={outside}")).Content.ReadAsStringAsync();
        listed.ShouldNotContain(theirs.ToString(), Case.Insensitive);
    }

    private async Task<HttpClient> Scoped(string subject, string product, params string[] permissions)
    {
        var user = await fixture.Database.UserWithPermissions(T, subject, permissions);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY[@p] WHERE user_id = @u", ("p", product), ("u", user));
        return _factory.ClientFor(T, subject);
    }
}
