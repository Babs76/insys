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
    private Task<(OftzaSeed Seed, Persona Admin)> Run() => Start(fixture, only: null, accounts: true);

    /// <summary>The seed against this fixture's host, as Program.cs runs it; <paramref name="only"/> as its --only flag.</summary>
    internal static async Task<(OftzaSeed Seed, Persona Admin)> Start(DatabaseFixture fixture, string? only, bool accounts)
    {
        var db = fixture.Database;
        var host = await fixture.Host();
        string[] platform = ["tenant.read", "tenant.create", "tenant.update", "user.read", "user.create", "user.update", "approval.read", "approval.approve", "static_data.read", "static_data.create", "static_data.update", "static_data.approve"];
        await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-1", platform);
        var second = await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-2", platform);
        var role = await db.RoleWithPermissions(DatabaseFixture.Operator);
        await db.AssignRole(DatabaseFixture.Operator, second, role);
        await db.SeedStatic(DatabaseFixture.Operator, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "AUTH-SEED-ADM", new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", "TENANT_ADMINISTRATOR"), ("limit_value", 1m), ("delegation_allowed", false));
        // As the operator's bootstrap gives PLATFORM_ADMIN (db/bootstrap/operator.sql): the other administrator's privileged role assignments.
        await db.SeedStatic(DatabaseFixture.Operator, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "AUTH-SEED-PRIV", new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", "PRIVILEGED_ROLE"), ("limit_value", 1m), ("delegation_allowed", false));
        var seed = new OftzaSeed(new SeedContext(new Persona(host.StrictClientFor(DatabaseFixture.Operator, "seed-platform-1")), new Persona(host.StrictClientFor(DatabaseFixture.Operator, "seed-platform-2")),
            (tenant, subject) => new Persona(host.StrictClientFor(tenant, subject, "insys-web")), () => host.DrainOutbox(), async () =>
            {
                await host.Services.GetRequiredService<Insys.Billing.Infrastructure.BillingJobs>().Run(CancellationToken.None);
                await host.DrainOutbox();
                await host.Services.GetRequiredService<Insys.Life.Infrastructure.LifeJobs>().Run(CancellationToken.None);
                await host.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>().Run(CancellationToken.None);
                await host.DrainOutbox();
            }, Accounts: accounts, Only: only));
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

        // Ade, 18 November 2026: the sample tenant's configuration is complete, so nothing it raises parks for missing configuration, fails or is dead-lettered.
        var stuck = await fixture.Database.ScalarAsOwner<string?>("""
            SELECT string_agg(format('%s %s %s (%s, %s)', status, coalesce(prerequisite_code, ''), coalesce(prerequisite_item, ''), consumer, event_type), ' | '
                              ORDER BY status, consumer, prerequisite_item)
            FROM integration.parked_delivery WHERE tenant_id = @t AND status IN ('PARKED', 'FAILING', 'RETRYING', 'DEAD_LETTERED')
            """, ("t", seed.TenantId));
        stuck.ShouldBeNull("every delivery for the sample tenant is handled");

        (await fixture.Database.ScalarAsOwner<bool>("SELECT sample FROM tenant.tenant WHERE id = @t", ("t", seed.TenantId))).ShouldBeTrue();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM tenant.cfg_locale_defaults WHERE tenant_id = @t", ("t", seed.TenantId))).ShouldBe(1L);

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t AND username LIKE 'oftza-%'", ("t", seed.TenantId))).ShouldBeGreaterThan(15L, "a persona per role template");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN'", ("t", seed.TenantId))).ShouldBe(4L, "the seed's two administrators, and the Preview's ADMIN01 and ADMIN02");
        var users = await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", seed.TenantId));
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM tenant.tenant WHERE id = @t", ("t", seed.TenantId))).ShouldBe("ACTIVE");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party WHERE tenant_id = @t", ("t", seed.TenantId))).ShouldBe(30L, "20 people, 5 companies, 3 providers and 2 brokers");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(DISTINCT product_code) FROM product.cfg_product_definitions_and_versions WHERE tenant_id = @t AND record_status = 'PUBLISHED'", ("t", seed.TenantId))).ShouldBe(5L, "motor, household, funeral, term and the medical scheme, all by the real route");
        async Task<long> Count(string sql) => await fixture.Database.ScalarAsOwner<long>(sql, ("t", seed.TenantId));
        (await Count("SELECT count(*) FROM life.policy WHERE tenant_id = @t")).ShouldBe(12L);
        (await Count("SELECT count(*) FROM life.policy WHERE tenant_id = @t AND status = 'LAPSED'")).ShouldBe(2L);
        // OQ-164 (beta check, 19 November 2026): Life reads a rated premium as one instalment at the policy's frequency. Every sample Life policy is
        // monthly, and its premium is a monthly instalment by the sample rates (sum assured × the age band's rate + the 50 policy fee; every holder is
        // 41 to 65 next birthday): funeral 30,000 × 0.005 + 50 = 200; term 1,000,000 × 0.0012 + 50 = 1,250; term 500,000 × 0.0012 + 50 = 650.
        // The two migrated policies keep their source system's 150. A missed instalment in arrears is the policy's own instalment.
        var lifePremiums = await fixture.Database.ScalarAsOwner<string>(
            "SELECT string_agg(line, '; ' ORDER BY line) FROM (SELECT p.product_code || ' ' || b.sum_assured::numeric(18,0) || ' ' || p.premium_frequency || ' ' || b.premium || ' x' || count(*) AS line "
            + "FROM life.policy p JOIN life.policy_benefit b ON b.tenant_id = p.tenant_id AND b.policy_id = p.id WHERE p.tenant_id = @t "
            + "GROUP BY p.product_code, b.sum_assured, p.premium_frequency, b.premium) lines", ("t", seed.TenantId));
        lifePremiums.ShouldBe("OFT-FUNERAL 30000 MONTHLY 200.00 x3; OFT-TERM 1000000 MONTHLY 1250.00 x3; OFT-TERM 500000 MONTHLY 150.00 x2; OFT-TERM 500000 MONTHLY 650.00 x4");
        (await Count("SELECT count(*) FROM billing.invoice i JOIN life.policy p ON p.tenant_id = i.tenant_id AND p.id = i.policy_ref WHERE i.tenant_id = @t AND i.source_domain = 'LIFE' AND i.total_amount = 100")).ShouldBe(0L,
            "a sample overdue Life premium is the policy's own instalment, not a fixed 100");
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
        (await Count("SELECT count(DISTINCT u.username) FROM iam.user_account u JOIN iam.user_role_assignment r ON r.tenant_id = u.tenant_id AND r.user_id = u.id AND r.status = 'ACTIVE' WHERE u.tenant_id = @t AND u.username ~ '^[a-z]+0[12]$'")).ShouldBe((long)OftzaSeed.PreviewAccounts.Count, "every Preview account, its role in force");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(DISTINCT u.username) FROM iam.user_account u JOIN iam.user_role_assignment r ON r.tenant_id = u.tenant_id AND r.user_id = u.id AND r.status = 'ACTIVE' WHERE u.tenant_id = @t AND u.username IN ('operator01', 'operator02', 'steward01', 'steward02')",
            ("t", DatabaseFixture.Operator))).ShouldBe(4L, "the operator tenant's four, their roles in force");
        (await Count("SELECT count(*) FROM iam.user_subject_link WHERE tenant_id = @t AND status = 'ACTIVE'")).ShouldBe(6L, "a member, a broker and a provider on their portals, as personas and as Preview accounts");
        (await Count("SELECT count(*) FROM reinsurance.cession WHERE tenant_id = @t")).ShouldBeGreaterThan(0L, "General policies ceded to the treaty");
        (await Count("SELECT count(*) FROM commission.commission_statement WHERE tenant_id = @t")).ShouldBe(1L);
        (await Count("SELECT count(*) FROM workflow.service_case WHERE tenant_id = @t")).ShouldBe(5L, "a claim disputed through a complaint, an enquiry, a provider's dispute, and a complaint decided and appealed");
        (await Count("SELECT count(DISTINCT family) FROM workflow.service_case WHERE tenant_id = @t")).ShouldBe(4L, "cases of each family");
        (await Count("SELECT count(*) FROM finance.tax_return WHERE tenant_id = @t")).ShouldBe(1L, "one VAT return prepared");
        (await Count("SELECT count(*) FROM finance.accounting_period WHERE tenant_id = @t AND status = 'CLOSED'")).ShouldBe(1L, "last month closed");

        // BR-140: a channel per portal, so a portal user's calls pass the channel check (CHANNEL_NOT_ENABLED before).
        (await Count("SELECT count(*) FROM channel.cfg_channel_definitions WHERE tenant_id = @t AND record_status IN ('PUBLISHED', 'EFFECTIVE', 'ACTIVE') AND audience IN ('CUSTOMER', 'BROKER', 'PROVIDER')")).ShouldBe(3L);
        var host = await fixture.Host();
        foreach (var (subject, client) in new[] { ("oftza-member", "member-portal"), ("oftza-broker", "broker-portal"), ("oftza-provider", "provider-portal") })
        {
            // Signed in with a second factor, as the broker and provider portals require (BR-008): what's left to refuse is the channel.
            var me = await host.ClientFor(seed.TenantId, subject, client, multiFactor: true).GetAsync("/api/v1/identity/me");
            me.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, $"{subject} on {client}: {await me.Content.ReadAsStringAsync()}");
        }

        // BR-142 and BR-143: a bordereaux layout for the quota share, and consent purposes.
        (await Count("SELECT count(*) FROM reinsurance.cfg_bordereaux_layouts WHERE tenant_id = @t AND record_status IN ('PUBLISHED', 'EFFECTIVE', 'ACTIVE') AND treaty = 'OFT-QS-1'")).ShouldBe(1L);
        (await Count("SELECT count(*) FROM party.cfg_consent_purposes WHERE tenant_id = @t AND record_status IN ('PUBLISHED', 'EFFECTIVE', 'ACTIVE') AND jurisdiction = 'ZA'")).ShouldBe(2L);

        // Legal hold (Ade, 25 October 2026): only the compliance officer approves a release, never the tenant administrator; and the sample reasons are in force.
        (await fixture.Database.ScalarAsOwner<string>("SELECT string_agg(DISTINCT role_code, ',') FROM workflow.cfg_delegation_and_approval_authority WHERE tenant_id = @t AND transaction_type = 'LEGAL_HOLD_RELEASE' AND record_status NOT IN ('WITHDRAWN', 'RETIRED')",
            ("t", seed.TenantId))).ShouldBe("COMPLIANCE_OFFICER");
        (await fixture.Database.ScalarAsOwner<string>("SELECT string_agg(code, ',' ORDER BY code) FROM document.ref_legal_hold_reason_codes WHERE tenant_id = @t AND record_status = 'ACTIVE' AND authority_role = 'COMPLIANCE_OFFICER'",
            ("t", seed.TenantId))).ShouldBe(string.Join(',', OftzaSeed.LegalHoldReasons.Select(r => r.Code).Order(StringComparer.Ordinal)));

        // SRS-002 (Ade, 20 November 2026): the addendum's three BLOCK rules are in force, so INSYS refuses a role or a user holding both sides; no seeded
        // role holds both functions of one.
        (await fixture.Database.ScalarAsOwner<string>("SELECT string_agg(code || '=' || function_a || '/' || function_b, ',' ORDER BY code) FROM iam.cfg_segregation_of_duties_conflict_rules WHERE tenant_id = @t AND record_status IN ('PUBLISHED', 'EFFECTIVE') AND conflict = 'BLOCK'",
            ("t", seed.TenantId))).ShouldBe(string.Join(',', OftzaSeed.SegregationOfDuties.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => $"{r.Code}={r.FunctionA}/{r.FunctionB}")));
        (await Count("""
            SELECT count(*) FROM iam.cfg_segregation_of_duties_conflict_rules r
            WHERE r.tenant_id = @t AND r.conflict = 'BLOCK' AND r.record_status IN ('PUBLISHED', 'EFFECTIVE')
              AND EXISTS (SELECT 1 FROM iam.cfg_role_function_assignments a JOIN iam.cfg_role_function_assignments b ON b.tenant_id = a.tenant_id AND b.role_code = a.role_code
                          WHERE a.tenant_id = r.tenant_id AND a.function_code = r.function_a AND b.function_code = r.function_b
                            AND a.record_status NOT IN ('WITHDRAWN', 'RETIRED') AND b.record_status NOT IN ('WITHDRAWN', 'RETIRED'))
            """)).ShouldBe(0L);
        // OQ-197: the seed's rules are the platform baseline, and no seeded user holds both sides of one through all their roles, so nobody is refused a
        // release or verify side for segregation of duties.
        OftzaSeed.SegregationOfDuties.ShouldBe(Insys.Identity.Domain.Users.SegregationOfDutiesBaseline.Rules.Select(r => (r.Code, r.FunctionA, r.FunctionB)).ToList());
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(DISTINCT u.username || ':' || r.function_b, ',') FROM iam.user_account u
            CROSS JOIN unnest(@a::text[], @b::text[]) AS r(function_a, function_b)
            WHERE u.tenant_id = @t
              AND EXISTS (SELECT 1 FROM iam.user_role_assignment x JOIN iam.cfg_role_function_assignments g ON g.tenant_id = x.tenant_id AND g.role_code = x.role_code
                          WHERE x.tenant_id = u.tenant_id AND x.user_id = u.id AND x.status = 'ACTIVE' AND g.function_code = r.function_a
                            AND g.record_status NOT IN ('WITHDRAWN', 'RETIRED'))
              AND EXISTS (SELECT 1 FROM iam.user_role_assignment x JOIN iam.cfg_role_function_assignments g ON g.tenant_id = x.tenant_id AND g.role_code = x.role_code
                          WHERE x.tenant_id = u.tenant_id AND x.user_id = u.id AND x.status = 'ACTIVE' AND g.function_code = r.function_b
                            AND g.record_status NOT IN ('WITHDRAWN', 'RETIRED'))
            """, ("t", seed.TenantId), ("a", OftzaSeed.SegregationOfDuties.Select(r => r.FunctionA).ToArray()),
            ("b", OftzaSeed.SegregationOfDuties.Select(r => r.FunctionB).ToArray()))).ShouldBeNull("no seeded user holds both sides of a baseline rule");

        // BR-146: the four Life accounts left in arrears show at once, in both buckets (B1, 1 to 30 days; B2, 31 and over), whatever the business date.
        (await Count("SELECT count(DISTINCT bucket_code) FROM billing.arrears_case WHERE tenant_id = @t AND bucket_code IN ('B1', 'B2')")).ShouldBe(2L);
        var inArrears = await host.StrictClientFor(seed.TenantId, "oftza-finance-officer", "insys-web").GetAsync("/api/v1/billing/accounts?status=InArrears&limit=100");
        var arrearsBody = await inArrears.Content.ReadAsStringAsync();
        inArrears.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, arrearsBody);
        System.Text.Json.JsonDocument.Parse(arrearsBody).RootElement.GetProperty("items").GetArrayLength().ShouldBeGreaterThanOrEqualTo(4, arrearsBody);

        // BR-141: a product manager approves another product manager's rating change; a reinsurance officer can't approve it; and nobody approves their own.
        const string Rounding = "/api/v1/rating/static/rounding-and-currency-precision-rules";
        var product01 = host.StrictClientFor(seed.TenantId, "product01");
        var drafted = await product01.Send(HttpMethod.Post, Rounding, new { code = "ZAR-BR141", ruleCode = "ZAR-BR141", currency = "ZAR", step = 0.01m, method = "HALF_UP", stage = "FINAL_PREMIUM", changeReason = "BR-141 test" }, idempotent: true);
        drafted.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, await drafted.Content.ReadAsStringAsync());
        (await product01.Send(HttpMethod.Post, $"{Rounding}/ZAR-BR141/versions/1/submit", ifMatch: drafted.ETag())).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        await host.DrainOutbox();
        var own = await product01.Send(HttpMethod.Post, $"{Rounding}/ZAR-BR141/versions/1/approve");
        own.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        (await own.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await host.StrictClientFor(seed.TenantId, "reins02").Send(HttpMethod.Post, $"{Rounding}/ZAR-BR141/versions/1/approve")).StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden, "rating isn't the reinsurance officer's area");
        var approvedByPeer = await host.StrictClientFor(seed.TenantId, "product02").Send(HttpMethod.Post, $"{Rounding}/ZAR-BR141/versions/1/approve");
        approvedByPeer.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await approvedByPeer.Content.ReadAsStringAsync());

        // BR-141 for the other module roles (Ade, 14 November), shown for the distribution administrator: its own area's table, a second of the role approves.
        const string Hierarchy = "/api/v1/distribution/static/distribution-hierarchy";
        var dist01 = host.StrictClientFor(seed.TenantId, "dist01");
        var node = await dist01.Send(HttpMethod.Post, Hierarchy, new { code = "OFT-BR141", nodeCode = "OFT-BR141", overridingCommission = false, changeReason = "BR-141 test" }, idempotent: true);
        node.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, await node.Content.ReadAsStringAsync());
        (await dist01.Send(HttpMethod.Post, $"{Hierarchy}/OFT-BR141/versions/1/submit", ifMatch: node.ETag())).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        await host.DrainOutbox();
        (await (await dist01.Send(HttpMethod.Post, $"{Hierarchy}/OFT-BR141/versions/1/approve")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await host.StrictClientFor(seed.TenantId, "product02").Send(HttpMethod.Post, $"{Hierarchy}/OFT-BR141/versions/1/approve")).StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden, "distribution isn't the product manager's area");
        var byDist02 = await host.StrictClientFor(seed.TenantId, "dist02").Send(HttpMethod.Post, $"{Hierarchy}/OFT-BR141/versions/1/approve");
        byDist02.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await byDist02.Content.ReadAsStringAsync());

        // Role templates A and B (signed 3 November 2026): underwriters rate, evaluate and acknowledge themselves, audited; the line configuration managers change Health configuration, one making and the other approving.
        string By(string column, string username) => $"{column} IN (SELECT x FROM iam.user_account u, LATERAL (VALUES (u.username), (u.external_subject), (u.id::text)) v(x) WHERE u.tenant_id = @t AND u.username = '{username}' AND x IS NOT NULL)";
        (await Count($"SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'EvaluateRuleSet' AND {By("actor_id", "oftza-life-underwriter")}")).ShouldBeGreaterThan(0L, "the Life underwriter evaluates the rules, audited");
        (await Count($"SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'CalculatePremium' AND {By("actor_id", "oftza-general-underwriter")}")).ShouldBeGreaterThan(0L, "the General underwriter rates the quote, audited");
        (await Count($"SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation IN ('EvaluateRuleSet', 'CalculatePremium') AND {By("actor_id", "oftza-product-manager")}")).ShouldBe(0L, "the product manager no longer rates the business");
        (await Count($"SELECT count(*) FROM channel.disclosure_acknowledgement WHERE tenant_id = @t AND {By("recorded_by", "oftza-integration-administrator")}")).ShouldBe(0L, "nor does the integration administrator record acknowledgements");
        (await Count($"SELECT count(*) FROM channel.disclosure_acknowledgement WHERE tenant_id = @t AND {By("recorded_by", "oftza-health-membership-administrato")}")).ShouldBeGreaterThan(0L, "the membership administrator records them at enrolment");
        (await Count($"SELECT count(*) FROM health.cfg_contribution_rules WHERE tenant_id = @t AND code = 'CR-OFT' AND record_status = 'PUBLISHED' AND {By("created_by", OftzaSeed.LineConfigurationMaker)} AND {By("approved_by", OftzaSeed.LineConfigurationChecker)}"))
            .ShouldBe(1L, "a live tenant's own line configuration managers change its configuration under maker-checker");
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

        // The AI-2 evaluation set (batch 7.3) is drawn from this tenant: its products' premiums and the catalogue's worded refusals.
        await AssistEvaluationSet.Check(await fixture.Host(), seed.TenantId, "product01");

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
