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
using Insys.Party.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// Party's dead ends (docs/reports/2026-11-24-dead-end-sweep.md): a bank account opened by the ID its verification approval
/// carries, with the accounts waiting for a verifier listed tenant-wide (DE-020); and an export waiting for approval that
/// its approver refuses, or its requester cancels (DE-059).
/// </summary>
public sealed class PartyDeadEndTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;
    private HttpClient _verifier = null!;
    private HttpClient _requester = null!;
    private HttpClient _approver = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        await db.UserWithPermissions(T, "de-clerk", PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.BankAccountCreate,
            PartyPermissions.BankAccountRead);
        var verifier = await db.UserWithPermissions(T, "de-verifier", PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify, WorkflowPermissions.ApprovalRead,
            WorkflowPermissions.ApprovalApprove);
        var role = await db.RoleWithPermissions(T);
        await db.AssignRole(T, verifier, role);
        await db.AllowBankAccountVerification(T, role, TestClock.Today.AddDays(-30));
        await db.UserWithPermissions(DatabaseFixture.TenantB, "de-verifier-b", PartyPermissions.BankAccountRead);
        await db.UserWithPermissions(T, "de-requester", PartyPermissions.BulkJobCreate, PartyPermissions.BulkJobRead, PartyPermissions.BulkJobApprove);
        await db.UserWithPermissions(T, "de-approver", PartyPermissions.BulkJobRead, PartyPermissions.BulkJobApprove, PartyPermissions.BulkJobCreate);
        _clerk = _factory.ClientFor(T, "de-clerk");
        _verifier = _factory.ClientFor(T, "de-verifier");
        _requester = _factory.ClientFor(T, "de-requester");
        _approver = _factory.ClientFor(T, "de-approver");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ABankAccountWaitingForVerification_IsOpenedByTheIdItsApprovalCarries_AndListedForTheVerifier_DE020()
    {
        var party = await _clerk.RegisterPerson("Waiting");
        var added = await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/bank-accounts", new
        {
            accountNumber = "62000777666", bankCode = "632005", accountHolderName = "Waiting", accountType = "CURRENT", currency = "ZAR",
        }, idempotent: true);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        var account = (await added.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();

        var approval = (await (await _verifier.GetAsync($"/api/v1/workflow/approvals?businessRefType=BANK_ACCOUNT&businessRefId={account}")).Json())
            .GetProperty("items").EnumerateArray().Single();
        (approval.GetProperty("subjectOperation").GetString(), approval.GetProperty("subjectPath").GetString())
            .ShouldBe(("PartyGetBankAccount", $"/api/v1/party/bank-accounts/{account}"));

        var read = await _verifier.GetAsync(approval.GetProperty("subjectPath").GetString());
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        var view = await read.Json();
        (view.GetProperty("partyId").GetGuid(), view.GetProperty("status").GetString(), view.GetProperty("last4").GetString()).ShouldBe((party, "PENDING_VERIFICATION", "7666"));
        (await read.Content.ReadAsStringAsync()).ShouldNotContain("62000777666");
        (await _verifier.GetAsync($"/api/v1/party/bank-accounts/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _factory.ClientFor(DatabaseFixture.TenantB, "de-verifier-b").GetAsync($"/api/v1/party/bank-accounts/{account}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound, "another tenant's account");

        (await Pending()).ShouldContain(account);
        (await _verifier.GetAsync("/api/v1/party/bank-accounts/pending-verification?cursor=not-a-cursor")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _verifier.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval.GetProperty("id").GetGuid()}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await Pending()).ShouldNotContain(account, "a verified account no longer waits");
    }

    [Fact]
    public async Task AnExportWaitingForApproval_IsRefusedByASecondPersonWithAReason_DE059_SRS002()
    {
        var (job, etag) = await Export();
        var own = await _requester.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/refuse", new { reason = "Mine" }, ifMatch: etag);
        own.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await own.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await _approver.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/refuse", new { reason = "" }, ifMatch: etag)).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "a refusal gives its reason");
        (await _approver.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/refuse", new { reason = "Not needed" })).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionRequired);

        var refused = await _approver.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/refuse", new { reason = "No business purpose given" }, ifMatch: etag);
        refused.StatusCode.ShouldBe(HttpStatusCode.OK, await refused.Content.ReadAsStringAsync());
        var view = await (await _approver.GetAsync($"/api/v1/party/bulk-jobs/{job}")).Json();
        (view.GetProperty("status").GetString(), view.GetProperty("refusalReason").GetString(), view.GetProperty("refusedBy").GetGuid())
            .ShouldBe(("REFUSED", "No business purpose given", await UserId("de-approver")));

        (await (await _requester.GetAsync($"/api/v1/party/bulk-jobs/{job}/content")).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        var afterwards = (await _approver.GetAsync($"/api/v1/party/bulk-jobs/{job}")).ETag();
        (await (await _approver.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/approve", ifMatch: afterwards)).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'RefusePartyExport' AND target_id = @id",
            ("id", job.ToString()))).ShouldBe(1L);
        (await _approver.GetAsync("/api/v1/integration/bulk-jobs?status=REFUSED")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnExportWaitingForApproval_IsCancelledByItsRequesterOnly_DE059()
    {
        var (job, etag) = await Export();
        var other = await _approver.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/cancel", ifMatch: etag);
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _requester.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/cancel")).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);

        var cancelled = await _requester.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/cancel", ifMatch: etag);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());
        (await cancelled.Json()).GetProperty("status").GetString().ShouldBe("CANCELLED");
        var afterwards = (await _requester.GetAsync($"/api/v1/party/bulk-jobs/{job}")).ETag();
        (await (await _requester.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/cancel", ifMatch: afterwards)).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'CancelPartyExport' AND target_id = @id",
            ("id", job.ToString()))).ShouldBe(1L);
    }

    private async Task<(Guid Job, string? ETag)> Export()
    {
        var requested = await _requester.Send(HttpMethod.Post, "/api/v1/party/bulk-jobs/exports", new { status = "ACTIVE" }, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var job = (await requested.Json()).GetProperty("id").GetGuid();
        return (job, (await _requester.GetAsync($"/api/v1/party/bulk-jobs/{job}")).ETag());
    }

    private Task<Guid> UserId(string subject) =>
        fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = @s", ("t", T), ("s", subject));

    private async Task<List<Guid>> Pending()
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await (await _verifier.GetAsync($"/api/v1/party/bank-accounts/pending-verification?limit=50{(cursor is null ? "" : $"&cursor={cursor}")}")).Json();
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.String ? page.GetProperty("nextCursor").GetString() : null;
        }
        while (cursor is not null);
        return ids;
    }
}
