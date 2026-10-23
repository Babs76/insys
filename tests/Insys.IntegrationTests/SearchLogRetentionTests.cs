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
using Insys.Party.Infrastructure;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// OQ-073: the party search log keeps full entries for 12 months, then loses the plain-text name filter; entries are
/// deleted after the tenant's SECURITY retention period (5 years by default) unless a legal hold covers them; every run
/// is audited.
/// </summary>
public sealed class SearchLogRetentionTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Retention_MinimisesAfter12Months_DeletesAfterThePeriod_ExceptUnderAHold_AndAuditsEachRun_OQ073()
    {
        var factory = await fixture.Host();
        var db = fixture.Database;
        await db.UserWithPermissions(DatabaseFixture.TenantA, "hold-officer", PartyPermissions.SearchLogHold);
        var recent = await Entry(DatabaseFixture.TenantA, "clerk-1", "Recent", TimeSpan.FromDays(30));
        var yearOld = await Entry(DatabaseFixture.TenantA, "clerk-1", "Older", TimeSpan.FromDays(400));
        var expired = await Entry(DatabaseFixture.TenantA, "clerk-1", "Expired", TimeSpan.FromDays(6 * 365));
        var held = await Entry(DatabaseFixture.TenantA, "clerk-held", "Held", TimeSpan.FromDays(6 * 365));

        // Tenant B's own SECURITY policy is two years.
        await db.SeedStatic(DatabaseFixture.TenantB, "audit.cfg_audit_retention_policies", "EFFECTIVE", "RET-SEC-ZA", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
            ("event_class", "SECURITY"), ("jurisdiction", "ZA"), ("period", new NpgsqlTypes.NpgsqlInterval(24, 0, 0)));
        var threeYears = await Entry(DatabaseFixture.TenantB, "clerk-b", "ThreeYears", TimeSpan.FromDays(3 * 365));

        (await factory.ClientFor(DatabaseFixture.TenantA, "hold-officer").Send(HttpMethod.Post, "/api/v1/party/search-log/holds",
            new { searchedBy = "clerk-held", reason = "Investigation 42" }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await factory.Services.GetRequiredService<SearchLogRetentionJob>().Run(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(2);
        await factory.DrainOutbox();

        (await Name(recent)).ShouldBe("Recent", "kept in full for 12 months");
        (await Name(yearOld)).ShouldBeNull("minimised after 12 months");
        (await Exists(yearOld)).ShouldBeTrue();
        (await Exists(expired)).ShouldBeFalse("deleted after the default 5 years");
        (await Exists(held)).ShouldBeTrue("a legal hold overrides deletion");
        (await Name(held)).ShouldBeNull("the hold does not stop minimisation");
        (await Exists(threeYears)).ShouldBeFalse("tenant B's own policy is two years");

        var runs = await db.ScalarAsOwner<long>(
            "SELECT count(*) FROM audit.audit_event WHERE operation = 'ApplySearchLogRetention' AND tenant_id = ANY(@t)",
            ("t", new[] { DatabaseFixture.TenantA, DatabaseFixture.TenantB }));
        runs.ShouldBeGreaterThanOrEqualTo(2);
    }

    private async Task<Guid> Entry(Guid tenant, string searchedBy, string name, TimeSpan age)
    {
        var id = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO party.party_search_log (tenant_id, id, created_at, created_by, searched_at, searched_by, name_filter, result_count)
            VALUES (@t, @id, now(), 'seed', now() - @age, @by, @name, 1)
            """, ("t", tenant), ("id", id), ("age", age), ("by", searchedBy), ("name", name));
        return id;
    }

    private Task<string?> Name(Guid id) => fixture.Database.ScalarAsOwner<string?>("SELECT name_filter FROM party.party_search_log WHERE id = @id", ("id", id));

    private async Task<bool> Exists(Guid id) =>
        await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party_search_log WHERE id = @id", ("id", id)) == 1;
}
