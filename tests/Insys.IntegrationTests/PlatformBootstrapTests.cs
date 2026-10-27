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
using Insys.DbMigrator;
using Insys.Identity.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-061: a new environment is not deadlocked. The bootstrap creates two platform administrators from the deployment
/// configuration, two distinct named people, as a new tenant gets two first administrators (BR-013). PLATFORM_ADMIN holds
/// TERMINATE_TENANT, which wave2-2 made privileged, so assigning it to anyone else needs a second person's approval.
/// Each test starts from a freshly migrated database with no tenant at all, as a new environment does.
/// </summary>
public sealed class PlatformBootstrapTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly Guid Operator = TestTenants.Operator;
    private static readonly string Script = Path.Combine(RepositoryPaths.Root, "db", "bootstrap", "operator.sql");
    private static readonly PlatformAdministrator Ada = new("operator-ada", "ada.platform", "Ada Platform");
    private static readonly PlatformAdministrator Ben = new("operator-ben", "ben.platform", "Ben O'Platform");

    [Fact]
    public async Task FromAnEmptyDatabase_TwoBootstrappedOperatorsSignIn_AndOneApprovesTheOthersPrivilegedChange_BR061()
    {
        var db = await fixture.Server.CreateMigratedDatabase();
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM tenant.tenant")).ShouldBe(0L);
        var runner = new MigrationRunner(db.OwnerConnectionString);
        await runner.BootstrapOperator(Script, Operator, [Ada, Ben]);
        (await db.ScalarAsOwner<string>("SELECT display_name FROM iam.user_account WHERE external_subject = 'operator-ben'")).ShouldBe("Ben O'Platform");

        await using var host = await new InsysApiFactory(db).Warm();
        var ada = host.ClientFor(Operator, Ada.Subject);
        var ben = host.ClientFor(Operator, Ben.Subject);
        foreach (var client in new[] { ada, ben })
        {
            var me = await client.GetAsync("/api/v1/identity/me");
            me.StatusCode.ShouldBe(HttpStatusCode.OK, await me.Content.ReadAsStringAsync());
        }

        // The change that deadlocked every new environment: a third operator gets PLATFORM_ADMIN, which is privileged.
        var third = await db.UserWithPermissions(Operator, "operator-cy");
        var user = await ada.GetAsync($"/api/v1/identity/users/{third}");
        user.StatusCode.ShouldBe(HttpStatusCode.OK, await user.Content.ReadAsStringAsync());
        var assigned = await ada.Send(HttpMethod.Post, $"/api/v1/identity/users/{third}/assign-role", new { roleCode = "PLATFORM_ADMIN" }, ifMatch: user.ETag());
        assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent, await assigned.Content.ReadAsStringAsync());
        var assignment = (await (await ada.GetAsync($"/api/v1/identity/users/{third}")).Json()).GetProperty("roleAssignments").EnumerateArray()
            .Single(a => a.GetProperty("roleCode").GetString() == "PLATFORM_ADMIN");
        assignment.GetProperty("status").GetString().ShouldBe("PendingApproval");
        await host.DrainOutbox();

        var approval = (await (await ben.GetAsync(
            $"/api/v1/workflow/approvals?status=Requested&businessRefType={IdentityApprovals.BusinessRefType}&businessRefId={assignment.GetProperty("id").GetGuid()}")).Json())
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await ada.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var approved = await ben.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        await host.DrainOutbox();
        (await db.ScalarAsOwner<string>("SELECT status FROM iam.user_role_assignment WHERE user_id = @u AND role_code = 'PLATFORM_ADMIN'", ("u", third)))
            .ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task ASingleConfiguredOperator_OrTheSamePersonTwice_IsRefusedWithAClearMessage_AndNothingIsCreated_BR061()
    {
        var db = await fixture.Server.CreateMigratedDatabase();
        var runner = new MigrationRunner(db.OwnerConnectionString);

        var single = await Should.ThrowAsync<ArgumentException>(() => runner.BootstrapOperator(Script, Operator, [Ada]));
        single.Message.ShouldContain("needs two distinct platform administrators");
        single.Message.ShouldContain("--admin2-subject");
        (await Should.ThrowAsync<ArgumentException>(() => runner.BootstrapOperator(Script, Operator, [Ada, Ada with { Username = "ADA.PLATFORM" }])))
            .Message.ShouldContain("the same person");
        (await Should.ThrowAsync<ArgumentException>(() => runner.BootstrapOperator(Script, Operator, [Ada, Ben with { DisplayName = " " }])))
            .Message.ShouldContain("needs two distinct platform administrators");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM tenant.tenant")).ShouldBe(0L);
    }

    /// <summary>
    /// Every assignment the bootstrap makes is audited, actor bootstrap, with the person: both on a first run, and only
    /// the added one when a rerun completes an environment bootstrapped with one administrator before BR-061.
    /// </summary>
    [Fact]
    public async Task EachAdministratorTheBootstrapAssigns_IsAudited_WithTheActorBootstrapAndThePerson_BR061()
    {
        const string Events = """
            SELECT string_agg(details->>'username' || ':' || (details->>'completedExistingEnvironment'), ',' ORDER BY details->>'username')
            FROM audit.audit_event WHERE operation = 'BootstrapPlatformAdministrator' AND actor_type = 'SERVICE' AND actor_id = 'bootstrap'
              AND target_type = 'UserAccount' AND outcome = 'SUCCESS'
            """;
        var fresh = await fixture.Server.CreateMigratedDatabase();
        await new MigrationRunner(fresh.OwnerConnectionString).BootstrapOperator(Script, Operator, [Ada, Ben]);
        (await fresh.ScalarAsOwner<string>(Events)).ShouldBe("ada.platform:false,ben.platform:false");

        // An environment bootstrapped before BR-061, with one administrator: the rerun adds the second, and says so.
        var old = await fixture.Server.CreateMigratedDatabase();
        await old.ExecuteAsOwner("""
            INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at) VALUES (@t, 'OPERATOR', 'Platform operator', 'C', 'ACTIVE', now());
            INSERT INTO iam.user_account (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, username, display_name, idp_code, external_subject, status, mfa_enrolled)
            VALUES (@t, @u, 1, now(), 'bootstrap', now(), 'bootstrap', 'ada.platform', 'Ada Platform', 'OIDC', 'operator-ada', 'ACTIVE', true);
            INSERT INTO iam.user_role_assignment (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, role_code, valid_from, assigned_by, status)
            VALUES (@t, gen_random_uuid(), 1, now(), 'bootstrap', now(), 'bootstrap', @u, 'PLATFORM_ADMIN', current_date, @u, 'ACTIVE');
            """, ("t", Operator), ("u", Guid.CreateVersion7()));
        await new MigrationRunner(old.OwnerConnectionString).BootstrapOperator(Script, Operator, [Ada, Ben]);
        (await old.ScalarAsOwner<string>(Events)).ShouldBe("ben.platform:true");
        (await old.ScalarAsOwner<string>(
            "SELECT details->>'displayName' FROM audit.audit_event WHERE operation = 'BootstrapPlatformAdministrator'")).ShouldBe("Ben O'Platform");
        (await old.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE role_code = 'PLATFORM_ADMIN' AND status = 'ACTIVE'")).ShouldBe(2L);
    }

    [Fact]
    public async Task RunAgain_TheBootstrapChangesNothing_AndRefusesAThirdAdministrator_BR061()
    {
        var db = await fixture.Server.CreateMigratedDatabase();
        var runner = new MigrationRunner(db.OwnerConnectionString);
        await runner.BootstrapOperator(Script, Operator, [Ada, Ben]);
        await runner.BootstrapOperator(Script, Operator, [Ada, Ben]);
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE role_code = 'PLATFORM_ADMIN' AND status = 'ACTIVE'")).ShouldBe(2L);
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", Operator))).ShouldBe(2L);

        var third = new PlatformAdministrator("operator-cy", "cy.platform", "Cy Platform");
        (await Should.ThrowAsync<Npgsql.PostgresException>(() => runner.BootstrapOperator(Script, Operator, [Ada, third])))
            .MessageText.ShouldContain("would be a third");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE role_code = 'PLATFORM_ADMIN' AND status = 'ACTIVE'")).ShouldBe(2L);
    }
}
