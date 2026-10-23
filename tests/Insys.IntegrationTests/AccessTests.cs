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
using Insys.Identity.Contracts;
using Insys.Identity.Infrastructure;
using Insys.Integration.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>PrivilegedAccessGrant (TEN-007) and AccessReview (IAM-005), as in domain-model.json.</summary>
public sealed class AccessTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly string[] AccessPermissions =
    [
        IdentityPermissions.PrivilegedAccessRead, IdentityPermissions.PrivilegedAccessCreate, IdentityPermissions.PrivilegedAccessApprove,
        IdentityPermissions.AccessReviewRead, IdentityPermissions.AccessReviewCreate, IdentityPermissions.AccessReviewUpdate,
    ];

    private InsysApiFactory _factory = null!;
    private Guid _requester;
    private Guid _approver;
    private Guid _beneficiary;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        _requester = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "access-requester", AccessPermissions);
        _approver = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "access-approver", AccessPermissions);
        _beneficiary = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "support-engineer");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task PrivilegedAccess_IsApprovedBySomeoneOtherThanTheRequester_TEN007()
    {
        var requester = _factory.ClientFor(DatabaseFixture.TenantA, "access-requester");
        var requested = await Request(requester, TimeSpan.FromHours(2));
        requested.StatusCode.ShouldBe(HttpStatusCode.Created);
        var url = $"/api/v1/identity/privileged-access/{(await requested.Json()).GetProperty("id").GetGuid()}";
        (await (await requester.GetAsync(url)).Json()).GetProperty("status").GetString().ShouldBe("Requested");

        var selfApproval = await requester.Send(HttpMethod.Post, $"{url}/approve", ifMatch: "\"1\"");
        selfApproval.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await selfApproval.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");

        var approver = _factory.ClientFor(DatabaseFixture.TenantA, "access-approver");
        (await approver.Send(HttpMethod.Post, $"{url}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var grant = await (await approver.GetAsync(url)).Json();
        grant.GetProperty("status").GetString().ShouldBe("Approved");
        grant.GetProperty("approvedBy").GetGuid().ShouldBe(_approver);
        grant.GetProperty("purpose").GetString().ShouldBe("Investigate ticket INC-42");

        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation IN ('RequestAccess', 'ApproveAccess') AND outcome = 'SUCCESS' AND target_id = @id",
            ("id", grant.GetProperty("id").GetGuid().ToString()))).ShouldBe(2);
    }

    [Fact]
    public async Task PrivilegedAccess_IsTimeLimited_TEN007()
    {
        var response = await Request(_factory.ClientFor(DatabaseFixture.TenantA, "access-requester"), TimeSpan.FromDays(3));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Code()).ShouldBe("ACCESS_WINDOW_TOO_LONG");
    }

    [Fact]
    public async Task PrivilegedAccess_Rejection_NeedsAReason_AndIsFinal()
    {
        var requested = await Request(_factory.ClientFor(DatabaseFixture.TenantA, "access-requester"), TimeSpan.FromHours(1));
        var url = $"/api/v1/identity/privileged-access/{(await requested.Json()).GetProperty("id").GetGuid()}";
        var approver = _factory.ClientFor(DatabaseFixture.TenantA, "access-approver");

        (await approver.Send(HttpMethod.Post, $"{url}/reject", new { reason = "" }, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await approver.Send(HttpMethod.Post, $"{url}/reject", new { reason = "Not needed" }, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var again = await approver.Send(HttpMethod.Post, $"{url}/approve", ifMatch: "\"2\"");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Code()).ShouldBe("INVALID_STATE_TRANSITION");
    }

    [Fact]
    public async Task PrivilegedAccess_ExpiresWhenItsWindowEnds_TEN007()
    {
        var requested = await Request(_factory.ClientFor(DatabaseFixture.TenantA, "access-requester"), TimeSpan.FromHours(1));
        var id = (await requested.Json()).GetProperty("id").GetGuid();
        await _factory.ClientFor(DatabaseFixture.TenantA, "access-approver").Send(HttpMethod.Post, $"/api/v1/identity/privileged-access/{id}/approve", ifMatch: "\"1\"");
        await fixture.Database.ExecuteAsOwner("UPDATE iam.privileged_access_grant SET valid_from = now() - interval '2 hours', valid_to = now() - interval '1 minute' WHERE id = @id",
            ("id", id));

        (await _factory.Services.GetRequiredService<PrivilegedAccessExpiry>().ExpireDue(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM iam.privileged_access_grant WHERE id = @id", ("id", id))).ShouldBe("EXPIRED");
    }

    [Fact]
    public async Task Database_RefusesAnApproverWhoIsTheRequester_TEN007()
    {
        var error = await Should.ThrowAsync<PostgresException>(() => fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.privileged_access_grant (tenant_id, id, created_at, created_by, user_id, purpose, approved_by, valid_from, valid_to,
                                                     requested_by, status, row_version, updated_at, updated_by)
            VALUES (@t, gen_random_uuid(), now(), 'x', @u, 'x', @u, now(), now() + interval '1 hour', @u, 'APPROVED', 1, now(), 'x')
            """, ("t", DatabaseFixture.TenantA), ("u", _requester)));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task AccessReview_RevokedAccess_EndsTheRoleWhenTheReviewCompletes_IAM005()
    {
        var clerkId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "reviewed-clerk", IntegrationPermissions.DeadLetterRead);
        var clerk = _factory.ClientFor(DatabaseFixture.TenantA, "reviewed-clerk");
        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var starter = _factory.ClientFor(DatabaseFixture.TenantA, "access-requester");
        var started = await starter.Send(HttpMethod.Post, "/api/v1/identity/access-reviews",
            new { scope = "AllUsers", reviewerId = _approver, dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)) }, idempotent: true);
        started.StatusCode.ShouldBe(HttpStatusCode.Created);
        var url = $"/api/v1/identity/access-reviews/{(await started.Json()).GetProperty("id").GetGuid()}";
        var reviewer = _factory.ClientFor(DatabaseFixture.TenantA, "access-approver");
        var review = await reviewer.GetAsync(url);
        var body = await review.Json();
        var etag = review.ETag();

        var early = await reviewer.Send(HttpMethod.Post, $"{url}/complete", ifMatch: etag);
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await early.Code()).ShouldBe("REVIEW_INCOMPLETE");

        var firstItem = body.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await starter.Send(HttpMethod.Post, $"{url}/items/{firstItem}/confirm", ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        foreach (var item in body.GetProperty("items").EnumerateArray())
        {
            var revoke = item.GetProperty("userId").GetGuid() == clerkId;
            var decided = await reviewer.Send(HttpMethod.Post, $"{url}/items/{item.GetProperty("id").GetGuid()}/{(revoke ? "revoke" : "confirm")}", ifMatch: etag);
            decided.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            etag = decided.ETag();
        }

        (await reviewer.Send(HttpMethod.Post, $"{url}/complete", ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var completed = await (await reviewer.GetAsync(url)).Json();
        completed.GetProperty("status").GetString().ShouldBe("Completed");
        completed.GetProperty("itemsRevoked").GetInt32().ShouldBe(1);

        await _factory.DrainOutbox();
        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private Task<HttpResponseMessage> Request(HttpClient client, TimeSpan window) =>
        client.Send(HttpMethod.Post, "/api/v1/identity/privileged-access", new
        {
            userId = _beneficiary,
            purpose = "Investigate ticket INC-42",
            validFrom = DateTimeOffset.UtcNow.AddMinutes(1),
            validTo = DateTimeOffset.UtcNow.AddMinutes(1).Add(window),
        }, idempotent: true);
}
