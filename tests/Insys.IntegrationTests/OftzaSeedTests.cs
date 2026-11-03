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
using Insys.Seed;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>Workstream F: the sample tenant, through the governed API alone.</summary>
public sealed class OftzaSeedTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private async Task<(OftzaSeed Seed, Persona Admin)> Run()
    {
        var db = fixture.Database;
        var host = await fixture.Host();
        string[] platform = ["tenant.read", "tenant.create", "tenant.update", "user.read", "user.create", "approval.read", "approval.approve", "static_data.read", "static_data.create", "static_data.update", "static_data.approve"];
        await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-1", platform);
        var second = await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-2", platform);
        var role = await db.RoleWithPermissions(DatabaseFixture.Operator);
        await db.AssignRole(DatabaseFixture.Operator, second, role);
        await db.SeedStatic(DatabaseFixture.Operator, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "AUTH-SEED-ADM", new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", "TENANT_ADMINISTRATOR"), ("limit_value", 1m), ("delegation_allowed", false));
        var seed = new OftzaSeed(new SeedContext(new Persona(host.StrictClientFor(DatabaseFixture.Operator, "seed-platform-1")), new Persona(host.StrictClientFor(DatabaseFixture.Operator, "seed-platform-2")),
            (tenant, subject) => new Persona(host.StrictClientFor(tenant, subject, "insys-web")), () => host.DrainOutbox(), async () =>
            {
                await host.Services.GetRequiredService<Insys.Billing.Infrastructure.BillingJobs>().Run(CancellationToken.None);
                await host.DrainOutbox();
                await host.Services.GetRequiredService<Insys.Life.Infrastructure.LifeJobs>().Run(CancellationToken.None);
                await host.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>().Run(CancellationToken.None);
                await host.DrainOutbox();
            }));
        try
        {
            await seed.Run();
        }
        catch (SeedRefusedException refused)
        {
            throw new InvalidOperationException("GAPS " + string.Join(" | ", seed.Gaps) + " :: " + refused.Message[..Math.Min(refused.Message.Length, 1500)]);
        }

        return (seed, new Persona(host.ClientFor(seed.TenantId, OftzaSeed.FirstAdministrator)));
    }

    [Fact]
    public async Task TheSeed_MakesASampleTenant_WithTwoAdministrators_AndARunAgainChangesNothing()
    {
        var (seed, _) = await Run();
        (await fixture.Database.ScalarAsOwner<bool>("SELECT sample FROM tenant.tenant WHERE id = @t", ("t", seed.TenantId))).ShouldBeTrue();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM tenant.cfg_locale_defaults WHERE tenant_id = @t", ("t", seed.TenantId))).ShouldBe(1L);

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t AND username LIKE 'oftza-%'", ("t", seed.TenantId))).ShouldBeGreaterThan(15L, "a persona per role template");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN'", ("t", seed.TenantId))).ShouldBe(2L);
        var users = await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", seed.TenantId));
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM tenant.tenant WHERE id = @t", ("t", seed.TenantId))).ShouldBe("ACTIVE");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party WHERE tenant_id = @t", ("t", seed.TenantId))).ShouldBe(30L, "20 people, 5 companies, 3 providers and 2 brokers");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(DISTINCT product_code) FROM product.cfg_product_definitions_and_versions WHERE tenant_id = @t AND record_status = 'PUBLISHED'", ("t", seed.TenantId))).ShouldBe(5L, "motor, household, funeral, term and the medical scheme, all by the real route");
        async Task<long> Count(string sql) => await fixture.Database.ScalarAsOwner<long>(sql, ("t", seed.TenantId));
        (await Count("SELECT count(*) FROM life.policy WHERE tenant_id = @t")).ShouldBe(12L);
        (await Count("SELECT count(*) FROM life.policy WHERE tenant_id = @t AND status = 'LAPSED'")).ShouldBe(2L);
        (await Count("SELECT count(*) FROM general.policy WHERE tenant_id = @t")).ShouldBe(12L);
        (await Count("SELECT count(*) FROM general.policy WHERE tenant_id = @t AND status = 'CANCELLED'")).ShouldBe(2L);
        (await Count("SELECT count(*) FROM general.renewal WHERE tenant_id = @t")).ShouldBe(2L, "two policies near their end await renewal");
        (await Count("SELECT count(*) FROM health.membership WHERE tenant_id = @t")).ShouldBe(12L);
        (await Count("SELECT count(*) FROM health.membership WHERE tenant_id = @t AND status = 'SUSPENDED'")).ShouldBe(2L);
        (await Count("SELECT count(*) FROM health.membership WHERE tenant_id = @t AND status = 'TERMINATED'")).ShouldBe(2L);
        (await Count("SELECT count(DISTINCT status) FROM general.claim WHERE tenant_id = @t")).ShouldBeGreaterThanOrEqualTo(4L, "claims at each main stage");
        (await Count("SELECT count(*) FROM general.claim WHERE tenant_id = @t AND status = 'DECLINED'")).ShouldBe(1L);
        (await Count("SELECT count(*) FROM life.claim WHERE tenant_id = @t")).ShouldBe(5L);
        (await Count("SELECT count(*) FROM health.medical_claim WHERE tenant_id = @t AND status = 'PAID'")).ShouldBe(2L);
        (await Count("SELECT count(*) FROM payment.payout_batch WHERE tenant_id = @t AND status IN ('RELEASED', 'SETTLED')")).ShouldBeGreaterThanOrEqualTo(1L, "released by a second person");
        (await Count("SELECT count(*) FROM payment.payment WHERE tenant_id = @t AND status = 'FAILED'")).ShouldBe(1L, "one collection bounced");
        (await Count("SELECT count(*) FROM payment.payment WHERE tenant_id = @t AND status = 'UNKNOWN'")).ShouldBe(1L, "one with no clear answer");
        (await Count("SELECT count(*) FROM finance.journal WHERE tenant_id = @t")).ShouldBeGreaterThan(50L, "the records' journals");
        (await Count("SELECT count(*) FROM billing.refund_request WHERE tenant_id = @t")).ShouldBe(1L);
        (await Count("SELECT count(*) FROM health.provider_statement WHERE tenant_id = @t")).ShouldBe(1L);
        (await Count("SELECT count(*) FROM reinsurance.cession WHERE tenant_id = @t")).ShouldBeGreaterThan(0L, "General policies ceded to the treaty");
        (await Count("SELECT count(*) FROM commission.commission_statement WHERE tenant_id = @t")).ShouldBe(1L);
        (await Count("SELECT count(*) FROM workflow.service_case WHERE tenant_id = @t")).ShouldBe(1L, "a claim disputed through a case");
        (await Count("SELECT count(*) FROM finance.tax_return WHERE tenant_id = @t")).ShouldBe(1L, "one VAT return prepared");
        (await Count("SELECT count(*) FROM finance.accounting_period WHERE tenant_id = @t AND status = 'CLOSED'")).ShouldBe(1L, "last month closed");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.bank_account WHERE tenant_id = @t AND status = 'ACTIVE'", ("t", seed.TenantId))).ShouldBeGreaterThanOrEqualTo(5L, "each provider, broker and payee, verified by a second person");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM commission.mst_intermediary_register WHERE tenant_id = @t AND record_status = 'ACTIVE'", ("t", seed.TenantId))).ShouldBe(2L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM health.mst_provider_register WHERE tenant_id = @t AND record_status = 'ACTIVE' AND party_id IS NOT NULL", ("t", seed.TenantId))).ShouldBe(3L);
        foreach (var loader in new[] { OftzaSeed.LoaderMaker, OftzaSeed.LoaderChecker })
        {
            (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM iam.user_account WHERE tenant_id = @t AND username = @u", ("t", seed.TenantId), ("u", loader))).ShouldBe("DISABLED", "it ends disabled");
            (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment r JOIN iam.user_account u ON u.tenant_id = r.tenant_id AND u.id = r.user_id WHERE r.tenant_id = @t AND u.username = @u AND r.status = 'ACTIVE'", ("t", seed.TenantId), ("u", loader))).ShouldBe(0L, "with its roles revoked");
        }

        // The administrators hold exactly TENANT_ADMIN's template.
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment r JOIN iam.user_account u ON u.tenant_id = r.tenant_id AND u.id = r.user_id WHERE r.tenant_id = @t AND u.username LIKE 'oftza-admin-%' AND r.role_code <> 'TENANT_ADMIN'", ("t", seed.TenantId))).ShouldBe(0L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE tenant_id = @t AND status = 'PENDING_APPROVAL'", ("t", seed.TenantId))).ShouldBe(0L, "no assignment is left waiting, or bypassed");

        var again = await Run();
        again.Seed.TenantId.ShouldBe(seed.TenantId);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", seed.TenantId))).ShouldBe(users);
    }

    [Fact]
    public void AnIdNumber_HasAValidChecksum_AnImpossibleDate_AndACitizenshipDigitNotInUse()
    {
        SampleParties.People.Select(p => p.IdNumber).Distinct().Count().ShouldBe(SampleParties.People.Count);
        foreach (var person in SampleParties.People)
        {
            person.IdNumber.Length.ShouldBe(13);
            person.IdNumber[2..6].ShouldBe("0230", "30 February doesn't exist");
            person.IdNumber[10].ShouldBe('9', "0 and 1 are the digits in use");
            SampleParties.LuhnDigit(person.IdNumber[..12]).ShouldBe(person.IdNumber[12]);
            person.Mobile.ShouldStartWith("+270", customMessage: "not a diallable number");
            person.Email.ShouldEndWith("@example.test");
            person.Surname.ShouldContain("Sample");
        }

        SampleParties.LuhnDigit("800101500908").ShouldBe('7', "a known valid example: 8001015009087");
    }
}
