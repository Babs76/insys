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

    /// <summary>
    /// OQ-203 item 4 (Ade, 26 November 2026), HLT-008: the accounts waiting for verification are listed under exactly the party's bank account list's guard
    /// (PARTY_BANK_ACCOUNTS, bank_account.read, the tenant, no product scope, as parties have none, PTY-003). Another tenant's account is not listed; a
    /// product-scoped reader lists it as they list the party's accounts; without the function, or with only the party screen, both are refused alike; and
    /// no account number leaves in any answer.
    /// </summary>
    [Fact]
    public async Task TheAccountsWaitingForVerification_AreListedUnderThePartysListGuard_OQ203_HLT008()
    {
        var party = await _clerk.RegisterPerson("Guarded");
        var added = await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/bank-accounts", new
        {
            accountNumber = "62000888555", bankCode = "632005", accountHolderName = "Guarded", accountType = "CURRENT", currency = "ZAR",
        }, idempotent: true);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        var account = (await added.Json()).GetProperty("id").GetGuid();
        const string pending = "/api/v1/party/bank-accounts/pending-verification?limit=200";
        var partyList = $"/api/v1/party/parties/{party}/bank-accounts";

        async Task<(HttpStatusCode Status, string? Code, bool Shows)> Answer(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            body.ShouldNotContain("62000888555", Case.Sensitive, $"{url}: no answer carries the account number");
            return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? null : JsonDocument.Parse(body).RootElement.GetProperty("code").GetString(),
                response.StatusCode == HttpStatusCode.OK && body.Contains(account.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        (await Answer(_verifier, pending)).Shows.ShouldBeTrue();
        (await Answer(_verifier, partyList)).Shows.ShouldBeTrue();
        (await Answer(_factory.ClientFor(DatabaseFixture.TenantB, "de-verifier-b"), pending)).Shows.ShouldBeFalse("another tenant's account is not listed");

        var scopedId = await fixture.Database.UserWithPermissions(T, "de-scoped-reader", PartyPermissions.BankAccountRead, PartyPermissions.PartyRead);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['OQ203-NONE'] WHERE user_id = @u", ("u", scopedId));
        var scoped = _factory.ClientFor(T, "de-scoped-reader");
        (await Answer(scoped, pending)).Shows.ShouldBe((await Answer(scoped, partyList)).Shows, "a product-scoped reader: as the party's list");

        await fixture.Database.UserWithFunctions(T, "de-parties-only", "PARTIES");
        await fixture.Database.UserWithPermissions(T, "de-no-function");
        foreach (var subject in new[] { "de-parties-only", "de-no-function" })
        {
            var client = _factory.ClientFor(T, subject);
            var listed = await Answer(client, pending);
            (listed.Status, listed.Code).ShouldBe((HttpStatusCode.Forbidden, "FUNCTION_NOT_GRANTED"), subject);
            var byParty = await Answer(client, partyList);
            (byParty.Status, byParty.Code).ShouldBe((listed.Status, listed.Code), $"{subject}: as the party's list");
        }
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

    /// <summary>
    /// OQ-203 (Ade, 26 November 2026): cancelling an export belongs to its requester, whichever export function they used, with no function of its own. A
    /// user holding only PARTY_REQUEST_POLICY_EXPORT cancels their policy export, and one holding only PARTY_REQUEST_EXPORT their party export; neither
    /// cancels the other's (403 FORBIDDEN, the requester rule), and a user with no export-request function is refused the operation (FUNCTION_NOT_GRANTED).
    /// </summary>
    [Fact]
    public async Task EitherExportKindsRequester_CancelsTheirOwnExport_AndNobodyElse_OQ203_DE059()
    {
        await fixture.Database.UserWithFunctions(T, "de-policy-only", "PARTY_BULK_JOBS", "PARTY_REQUEST_POLICY_EXPORT");
        await fixture.Database.UserWithFunctions(T, "de-party-only", "PARTY_BULK_JOBS", "PARTY_REQUEST_EXPORT");
        await fixture.Database.UserWithFunctions(T, "de-approve-only", "PARTY_BULK_JOBS", "PARTY_APPROVE_EXPORT");
        var policyOnly = _factory.ClientFor(T, "de-policy-only");
        var partyOnly = _factory.ClientFor(T, "de-party-only");
        var approveOnly = _factory.ClientFor(T, "de-approve-only");

        async Task<(Guid Job, string? ETag)> Requested(HttpClient client, string path, object body)
        {
            var requested = await client.Send(HttpMethod.Post, path, body, idempotent: true);
            requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
            var id = (await requested.Json()).GetProperty("id").GetGuid();
            return (id, (await client.GetAsync($"/api/v1/party/bulk-jobs/{id}")).ETag());
        }

        var (policyJob, policyTag) = await Requested(policyOnly, "/api/v1/party/bulk-jobs/policy-exports", new { domain = "GENERAL" });
        var (partyJob, partyTag) = await Requested(partyOnly, "/api/v1/party/bulk-jobs/exports", new { status = "ACTIVE" });

        var notTheirs = await partyOnly.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{policyJob}/cancel", ifMatch: policyTag);
        notTheirs.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await notTheirs.Content.ReadAsStringAsync());
        (await notTheirs.Code()).ShouldBe("FORBIDDEN", "the requester rule, not the function: they hold an export function");
        (await (await policyOnly.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{partyJob}/cancel", ifMatch: partyTag)).Code()).ShouldBe("FORBIDDEN");
        var noFunction = await approveOnly.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{policyJob}/cancel", ifMatch: policyTag);
        noFunction.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await noFunction.Code()).ShouldBe("FUNCTION_NOT_GRANTED", "no export-request function");

        var policyCancelled = await policyOnly.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{policyJob}/cancel", ifMatch: policyTag);
        policyCancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await policyCancelled.Content.ReadAsStringAsync());
        (await policyCancelled.Json()).GetProperty("status").GetString().ShouldBe("CANCELLED");
        var partyCancelled = await partyOnly.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{partyJob}/cancel", ifMatch: partyTag);
        partyCancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await partyCancelled.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'CancelPartyExport' AND target_id IN (@a, @b)",
            ("a", policyJob.ToString()), ("b", partyJob.ToString()))).ShouldBe(2L);
    }

    /// <summary>
    /// OQ-203 item 3 (confirmed, Ade, 26 November 2026): refusing is a second person's, under PARTY_APPROVE_EXPORT (party_bulk_job.approve): the requester
    /// holding that function can't refuse their own policy export (409 MAKER_CHECKER_SAME_PERSON), and a requester without it is refused the operation.
    /// </summary>
    [Fact]
    public async Task TheRequester_CannotRefuseTheirOwnExport_EvenHoldingTheApproveFunction_OQ203_SRS002()
    {
        await fixture.Database.UserWithFunctions(T, "de-policy-both", "PARTY_BULK_JOBS", "PARTY_REQUEST_POLICY_EXPORT", "PARTY_APPROVE_EXPORT");
        await fixture.Database.UserWithFunctions(T, "de-policy-asker", "PARTY_BULK_JOBS", "PARTY_REQUEST_POLICY_EXPORT");
        var both = _factory.ClientFor(T, "de-policy-both");
        var asker = _factory.ClientFor(T, "de-policy-asker");
        var requested = await both.Send(HttpMethod.Post, "/api/v1/party/bulk-jobs/policy-exports", new { domain = "LIFE" }, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var job = (await requested.Json()).GetProperty("id").GetGuid();
        var etag = (await both.GetAsync($"/api/v1/party/bulk-jobs/{job}")).ETag();

        var own = await both.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/refuse", new { reason = "Mine" }, ifMatch: etag);
        own.StatusCode.ShouldBe(HttpStatusCode.Conflict, await own.Content.ReadAsStringAsync());
        (await own.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await (await asker.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/refuse", new { reason = "Not mine" }, ifMatch: etag)).Code())
            .ShouldBe("FUNCTION_NOT_GRANTED", "refusing needs PARTY_APPROVE_EXPORT");
        var refused = await _approver.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/refuse", new { reason = "No purpose given" }, ifMatch: etag);
        refused.StatusCode.ShouldBe(HttpStatusCode.OK, await refused.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'RefusePartyExport' AND target_id = @id",
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
