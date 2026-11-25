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
using Insys.BuildingBlocks.Api;
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
    /// Scope batch 5 (read sweep, paging): renewals and legal holds outside the scope never cut a page short. Three newer ones out of scope come first, and a
    /// page of two still holds the reader's own.
    /// </summary>
    [Fact]
    public async Task APageIsFull_WhateverIsOutOfScopeBeforeIt_RenewalsAndLegalHolds_F103()
    {
        var suffix = Random.Shared.Next(100_000, 999_999);
        var mine = await _rows.Insert(T, "general.policy", ("product_code", "RS-RN"), ("policy_number", $"RN{suffix}"), ("status", "IN_FORCE"));
        var myRenewal = await _rows.Insert(T, "general.renewal", ("policy_id", mine), ("status", "REVIEWING"));
        for (var i = 0; i < 3; i++)
        {
            var theirs = await _rows.Insert(T, "general.policy", ("product_code", "RS-RN-OTHER"), ("policy_number", $"RX{suffix}{i}"), ("status", "IN_FORCE"));
            await _rows.Insert(T, "general.renewal", ("policy_id", theirs), ("status", "REVIEWING"));
            await _rows.Insert(T, "document.record_hold", ("scope_type", "GENERAL_POLICY"), ("scope_id", theirs));
        }

        // Renewals are listed newest first and holds oldest first: the reader's renewal is the oldest, and its hold the newest.
        var myHold = await _rows.Insert(T, "document.record_hold", ("scope_type", "GENERAL_POLICY"), ("scope_id", mine));

        var reader = await Scoped("rs-pager", "RS-RN", Insys.General.Contracts.GeneralPermissions.PolicyRead, Insys.Documents.Contracts.DocumentPermissions.LegalHoldRead);
        (await (await reader.GetAsync("/api/v1/general/renewals?limit=2")).Content.ReadAsStringAsync()).ShouldContain(myRenewal.ToString(), Case.Insensitive);
        (await (await reader.GetAsync("/api/v1/documents/legal-holds?scopeType=GENERAL_POLICY&limit=2")).Content.ReadAsStringAsync())
            .ShouldContain(myHold.ToString(), Case.Insensitive);
    }

    /// <summary>Scope batch 5 (OQ-081): a refund on an account with no schedules is covered by no scoped caller, as its list and Payments already decide.</summary>
    [Fact]
    public async Task ARefundOnAnAccountWithNoSchedules_IsAnUnrestrictedReadersOnly_OQ081()
    {
        var account = await _rows.Insert(T, "billing.billing_account", ("status", "ACTIVE"));
        var refund = await _rows.Insert(T, "billing.refund_request", ("billing_account_id", account), ("status", "REQUESTED"), ("payer_party_id", Guid.CreateVersion7()));
        var reader = await Scoped("rs-refund", "RS-RF", Insys.Billing.Contracts.BillingPermissions.RefundRead);
        (await reader.GetAsync($"/api/v1/billing/refunds/{refund}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Scope batch 5 (read sweep, paging): payment runs whose claims are all outside the scope never cut a page short. Three such runs come first, and a page
    /// of two still holds the run paying the reader's claim.
    /// </summary>
    [Fact]
    public async Task APageOfPaymentRuns_IsFull_WhateverIsOutOfScopeBeforeIt_F103()
    {
        var from = Cursor.Encode(Guid.CreateVersion7());
        var other = await Membership("RS-PR-OTHER");
        for (var i = 0; i < 3; i++)
        {
            await RunPaying(other);
        }

        var mine = await RunPaying(await Membership("RS-PR"));
        var reader = await Scoped("rs-runs", "RS-PR", HealthPermissions.RunRead);
        var page = await reader.GetAsync($"/api/v1/health/payment-runs?limit=2&cursor={Uri.EscapeDataString(from)}");
        page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
        (await page.Content.ReadAsStringAsync()).ShouldContain(mine.ToString(), Case.Insensitive);
    }

    /// <summary>Scope batch 5: a member's portal list of claims applies the scope the single read already applies, in the query.</summary>
    [Fact]
    public async Task AMembersPortalClaims_AreListedOnlyWithinTheScopeTheSingleReadApplies_F103()
    {
        var party = Guid.CreateVersion7();
        var inside = await Claim(await Membership("RS-PC", party), party);
        var outside = await Claim(await Membership("RS-PC-OTHER", party), party);
        var user = await fixture.Database.UserWithPermissions(T, "rs-member", HealthPermissions.ClaimRead, HealthPermissions.MembershipRead);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['RS-PC'] WHERE user_id = @u", ("u", user));
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               provider_id, status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'PARTY', @p, NULL, NULL, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", T), ("u", user), ("p", party));
        await fixture.Database.SeedStatic(T, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-RSMEM", new DateOnly(2026, 1, 1), ("channel_code", "RSMEM"),
            ("audience", "CUSTOMER"), ("name", "Customer portal"), ("client_ids", new[] { "member-portal" }));
        var member = _factory.ClientFor(T, "rs-member", "member-portal");

        var single = await member.GetAsync($"/api/v1/portal/health/claims/{outside}");
        single.StatusCode.ShouldBe(HttpStatusCode.NotFound, await single.Content.ReadAsStringAsync());
        var list = await member.GetAsync("/api/v1/portal/health/claims");
        var body = await list.Content.ReadAsStringAsync();
        list.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldContain(inside.ToString(), Case.Insensitive);
        body.ShouldNotContain(outside.ToString(), Case.Insensitive);
    }

    private Task<Guid> Membership(string product, Guid? principal = null) => _rows.Insert(T, "health.membership", ("status", "ACTIVE"), ("product_code", product),
        ("member_number", $"RS{Random.Shared.Next(1_000_000, 9_999_999)}"), ("principal_party_id", principal ?? Guid.CreateVersion7()));

    private Task<Guid> Claim(Guid membership, Guid patient, Guid? run = null, string? provider = null) => _rows.Insert(T, "health.medical_claim",
        ("membership_id", membership), ("patient_party_id", patient), ("claim_number", $"RC{Random.Shared.Next(10_000_000, 99_999_999)}"), ("source", "PROVIDER"),
        ("status", run is null ? "RECEIVED" : "PAID"), ("payment_run_id", (object?)run ?? DBNull.Value), ("provider_id", (object?)provider ?? DBNull.Value));

    private async Task<Guid> RunPaying(Guid membership)
    {
        var run = await _rows.Insert(T, "health.provider_payment_run", ("run_code", $"RR{Random.Shared.Next(1_000_000, 9_999_999)}"), ("status", "RELEASED"));
        var provider = $"RP{Random.Shared.Next(1_000_000, 9_999_999)}";
        await _rows.Insert(T, "health.remittance_advice", ("run_id", run), ("provider_id", provider));
        await Claim(membership, Guid.CreateVersion7(), run, provider);
        return run;
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
