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
using Insys.Audit.Contracts;
using Insys.Identity.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>GET /api/v1/audit/records/{targetType}/{targetId}: one record's history, newest first, for the record action bar (Wave 0).</summary>
public sealed class RecordHistoryTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ARecordsHistory_ListsEveryChange_NewestFirst_ForAuditReaders()
    {
        var factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "history-admin",
            IdentityPermissions.UserRead, IdentityPermissions.UserCreate, IdentityPermissions.UserUpdate, AuditPermissions.Read);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "history-nobody", IdentityPermissions.UserRead);
        var admin = factory.ClientFor(DatabaseFixture.TenantA, "history-admin");

        var created = await admin.Send(HttpMethod.Post, "/api/v1/identity/users",
            new { username = "history-subject", displayName = "History subject", idpCode = "OIDC", externalSubject = "history-subject", mfaEnrolled = false },
            idempotent: true);
        var userId = (await created.Json()).GetProperty("id").GetGuid();
        var role = await fixture.Database.RoleWithPermissions(DatabaseFixture.TenantA, IdentityPermissions.UserRead);
        (await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role", new { roleCode = role }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await factory.DrainOutbox();

        var page = await admin.GetAsync($"/api/v1/audit/records/UserAccount/{userId}");
        page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
        var body = await page.Json();
        DocumentedSchema.ResponseProblems(body, "core", "GetRecordHistory").ShouldBeEmpty();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBeGreaterThanOrEqualTo(2);
        items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).ShouldBeInOrder(SortDirection.Descending);
        items.ShouldAllBe(i => i.GetProperty("targetId").GetString() == userId.ToString());

        (await factory.ClientFor(DatabaseFixture.TenantA, "history-nobody").GetAsync($"/api/v1/audit/records/UserAccount/{userId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
