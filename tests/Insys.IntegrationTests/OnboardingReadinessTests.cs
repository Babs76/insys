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
using Insys.Communications.Contracts;
using Insys.Payments.Contracts;
using Insys.Tenancy.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// Onboarding readiness (docs/specs/addenda/onboarding-readiness.md, TEN-008): a step completes only when its configuration
/// is in force in the tenant; a refusal names every failing check and what is missing; waivers and evidence stand in
/// only where the addendum allows them.
/// </summary>
public sealed class OnboardingReadinessTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string OperatorAdmin = "readiness-operator";
    private static readonly DateOnly From = TestClock.Today.AddDays(-30);
    private InsysApiFactory _factory = null!;
    private HttpClient _operator = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, OperatorAdmin, TenancyPermissions.Read, TenancyPermissions.Create, TenancyPermissions.Update);
        _operator = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin);
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, OperatorAdmin + "-2", TenancyPermissions.Read, TenancyPermissions.Update);
        _checker = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin + "-2");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AStep_CompletesOnlyWhenItsConfigurationIsInForce_ListingEveryFailingCheck_TEN008()
    {
        // It has stalled under CI load only (wave1-1 report): write what the database and thread pool are doing if it does again.
        await using var watchdog = StallWatchdog.Start(fixture.Database, TimeSpan.FromSeconds(45), "OnboardingReadiness");
        var (tenantId, url) = await Register("READY");
        var readiness = await Readiness(url);
        Check(readiness, "TENANT_REGISTRATION", "LOCALE_DEFAULTS").GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["tenant.cfg_locale_defaults"]);
        Check(readiness, "TENANT_REGISTRATION", "TIME_ZONE").GetProperty("ready").GetBoolean().ShouldBeTrue("given at registration");

        // Everything configured except the first administrator and one staff role's channel row.
        await fixture.Database.SeedReadyTenant(tenantId, From);
        await fixture.Database.SeedStaticFilled(tenantId, "iam.cfg_roles_and_permission_sets", "EFFECTIVE", "UNDERWRITER", From, ("role_code", "UNDERWRITER"),
            ("permissions", DBNull.Value));
        await fixture.Database.GrantFunctionsWithin(tenantId, "UNDERWRITER", From, "general_quote.create", "general_quote.read");
        var etag = await Complete(url, (await _operator.GetAsync(url)).ETag(), "PLATFORM_REFERENCE_DATA");
        etag = await Complete(url, etag, "TENANT_REGISTRATION");
        var refused = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", OnboardingSeed.StepBody("ORGANISATION_AND_ACCESS"), ifMatch: etag);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe("ONBOARDING_STEP_NOT_READY");
        var errors = problem.GetProperty("errors");
        errors.EnumerateObject().Select(e => e.Name).Order().ShouldBe(["CHANNELS", "FIRST_ADMINISTRATOR"]);
        errors.GetProperty("CHANNELS").EnumerateArray().Select(m => m.GetString()).ShouldBe(["UNDERWRITER: GENERAL_CREATE_QUOTE"], "reads are not channel-scoped");
        (await (await _operator.GetAsync(url)).Json()).GetProperty("onboardingStep").GetString().ShouldBe("ORGANISATION_AND_ACCESS", "nothing changed");

        await fixture.Database.SeedActiveAdministrators(tenantId, "ready-admin", "user.read");
        await fixture.Database.SeedStaticFilled(tenantId, "channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-UW", From, ("channel_code", "STF"),
            ("role_code", "UNDERWRITER"), ("transaction_type", "general_quote.create"), ("product_code", DBNull.Value), ("allowed", true),
            ("requires_approval", false));
        await fixture.Database.AdoptFunctionCatalogue();
        etag = await Complete(url, etag, "ORGANISATION_AND_ACCESS");
        foreach (var step in new[] { "FINANCIAL_FOUNDATIONS", "SHARED_OPERATIONAL_SETUP", "PAYMENTS_AND_DISTRIBUTION", "DOMAIN_REFERENCE_DATA", "PRODUCT_FACTORY" })
        {
            etag = await Complete(url, etag, step);
        }

        // Reinsurance: no treaties, so the step needs "no reinsurance" with a reason; only the checks that allow it take one.
        var noWaiver = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step = "REINSURANCE" }, ifMatch: etag);
        (await noWaiver.Json()).GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["TREATIES"]);
        var wrongCheck = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step",
            new { step = "REINSURANCE", attestations = new[] { new { check = "SMOKE_RUN", note = "RUN-1" } } }, ifMatch: etag);
        (await wrongCheck.Code()).ShouldBe("ATTESTATION_NOT_ALLOWED");
        foreach (var step in new[] { "REINSURANCE", "INTEGRATION_AND_REPORTING" })
        {
            etag = await Complete(url, etag, step);
        }

        // The end-to-end run, made in a non-production environment: recorded, then approved by a second operator, not the same one.
        (await (await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step = "END_TO_END_TESTS" }, ifMatch: etag)).Json())
            .GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["SMOKE_RUN"]);
        var recorded = await _operator.Send(HttpMethod.Post, $"{url}/smoke-run",
            new { runReference = "UAT-RUN-7", runDate = From.AddDays(29), outcome = "PASSED", ranBy = "QA team" }, ifMatch: etag);
        recorded.StatusCode.ShouldBe(HttpStatusCode.NoContent, await recorded.Content.ReadAsStringAsync());
        etag = recorded.ETag();
        Check(await Readiness(url), "END_TO_END_TESTS", "SMOKE_RUN").GetProperty("ready").GetBoolean().ShouldBeFalse("not yet approved");
        (await (await _operator.Send(HttpMethod.Post, $"{url}/smoke-run/approve", ifMatch: etag)).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var approved = await _checker.Send(HttpMethod.Post, $"{url}/smoke-run/approve", ifMatch: etag);
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        etag = await Complete(url, approved.ETag(), "END_TO_END_TESTS");

        (await _operator.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var done = await Readiness(url);
        done.GetProperty("steps").EnumerateArray().ShouldAllBe(s => s.GetProperty("completed").GetBoolean() && s.GetProperty("ready").GetBoolean());
        var waived = Check(done, "REINSURANCE", "TREATIES").GetProperty("attestation");
        (waived.GetProperty("kind").GetString(), waived.GetProperty("note").GetString()).ShouldBe(("WAIVER", "No reinsurance: test tenant"));
        var run = Check(done, "END_TO_END_TESTS", "SMOKE_RUN").GetProperty("attestation");
        (run.GetProperty("note").GetString(), run.GetProperty("outcome").GetString(), run.GetProperty("ranBy").GetString()).ShouldBe(("UAT-RUN-7", "PASSED", "QA team"));
        run.GetProperty("approvedBy").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task AnOnboardingTenantsUsers_LoadItsConfiguration_ButItsBusinessWaitsForActivation_TEN008()
    {
        var (tenantId, url) = await Register("CONFIG");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        await fixture.Database.UserWithPermissions(tenantId, "config-clerk", "static_data.create", "static_data.update", "static_data.read", "billing_account.create", "billing_account.read");
        var clerk = _factory.ClientFor(tenantId, "config-clerk");

        // Configuration: a static data draft, and its readiness completes the step.
        var draft = await clerk.Send(HttpMethod.Post, "/api/v1/billing/static/billing-frequencies",
            new { code = "WEEKLY", label = "Weekly", periodsPerYear = 52, changeReason = "onboarding" }, idempotent: true);
        draft.StatusCode.ShouldBe(HttpStatusCode.Created, await draft.Content.ReadAsStringAsync());
        await Complete(url, (await _operator.GetAsync(url)).ETag(), "PLATFORM_REFERENCE_DATA");

        // Business: refused until the tenant is activated; reads are allowed.
        object Account() => new { payerPartyId = Guid.CreateVersion7(), currency = "ZAR", collectionMethod = "DEBIT_ORDER" };
        var refused = await clerk.Send(HttpMethod.Post, "/api/v1/billing/accounts/", Account(), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Code()).ShouldBe("TENANT_NOT_ACTIVE");
        (await clerk.GetAsync($"/api/v1/billing/accounts/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "reads are allowed");

        await fixture.Database.SeedActiveAdministrators(tenantId, "config-admin", "user.read");
        var etag = await _operator.CompleteOnboarding(_checker, url, (await _operator.GetAsync(url)).ETag());
        (await _operator.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await clerk.Send(HttpMethod.Post, "/api/v1/billing/accounts/", Account(), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AgeingBuckets_MustCoverEveryDayToTheLastAction_Request46()
    {
        var (tenantId, url) = await Register("GAPPY");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        await fixture.Database.SeedStaticFilled(tenantId, "billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "B3", From, ("bucket_code", "B3"), ("days_from", 25),
            ("days_to", 26), ("action", "REMINDER"));
        var check = Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "COLLECTION_METHODS");
        check.GetProperty("ready").GetBoolean().ShouldBeFalse();
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldContain("ageing buckets overlap from day 25");
    }

    /// <summary>
    /// Batch 5 (decided 28 October 2026): a case type in force whose process is not in force is listed, as "case type:process",
    /// since its cases would open without a process; it clears when the process is in force again.
    /// </summary>
    [Fact]
    public async Task ACaseTypeWhoseProcessIsNotInForce_IsListed_UntilItIs()
    {
        var (tenantId, url) = await Register("NOPROC");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "CASE_TYPE_PROCESS_NOT_IN_FORCE").GetProperty("ready").GetBoolean().ShouldBeTrue();

        await fixture.Database.ExecuteAsOwner(
            "UPDATE workflow.cfg_workflow_definitions SET record_status = 'RETIRED' WHERE tenant_id = @t AND process_code = 'COMPLAINT'", ("t", tenantId));
        var check = Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "CASE_TYPE_PROCESS_NOT_IN_FORCE");
        check.GetProperty("ready").GetBoolean().ShouldBeFalse();
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["COMPLAINT:COMPLAINT"]);

        await fixture.Database.ExecuteAsOwner(
            "UPDATE workflow.cfg_workflow_definitions SET record_status = 'EFFECTIVE' WHERE tenant_id = @t AND process_code = 'COMPLAINT'", ("t", tenantId));
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "CASE_TYPE_PROCESS_NOT_IN_FORCE").GetProperty("ready").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// A sellable General product whose referrals no role is authorised to decide (rules.cfg_authority_matrices) is listed,
    /// since a referred quote of it would wait for nobody; it clears when an authority covers it again.
    /// </summary>
    [Fact]
    public async Task AGeneralProductNoRoleMayDecideReferralsFor_IsListed_UntilOneMay()
    {
        var (tenantId, url) = await Register("NOAUTH");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        Check(await Readiness(url), "PRODUCT_FACTORY", "GENERAL_REFERRAL_AUTHORITY").GetProperty("ready").GetBoolean().ShouldBeTrue();

        await fixture.Database.ExecuteAsOwner(
            "UPDATE rules.cfg_authority_matrices SET record_status = 'RETIRED' WHERE tenant_id = @t AND code = 'AM-READY'", ("t", tenantId));
        var check = Check(await Readiness(url), "PRODUCT_FACTORY", "GENERAL_REFERRAL_AUTHORITY");
        check.GetProperty("ready").GetBoolean().ShouldBeFalse();
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["P-READY"]);

        await fixture.Database.ExecuteAsOwner(
            "UPDATE rules.cfg_authority_matrices SET record_status = 'EFFECTIVE' WHERE tenant_id = @t AND code = 'AM-READY'", ("t", tenantId));
        Check(await Readiness(url), "PRODUCT_FACTORY", "GENERAL_REFERRAL_AUTHORITY").GetProperty("ready").GetBoolean().ShouldBeTrue();
    }

    /// <summary>B-010: an active broker with neither a contact party nor a party is listed as a warning, which does not stop the step.</summary>
    [Fact]
    public async Task ABrokerWithNoContactPartyOrParty_IsListedAsAWarning_B010()
    {
        var (tenantId, url) = await Register("NOCONTACT");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        foreach (var (code, field, party) in new[] { ("BRK-NONE", (string?)null, Guid.Empty), ("BRK-CONTACT", "contact_party_id", Guid.CreateVersion7()), ("BRK-PARTY", "party_id", Guid.CreateVersion7()) })
        {
            (string, object)[] fields = [("intermediary_code", code), ("type", "BROKER"), ("legal_name", code),
                ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", "ACTIVE"), ("hierarchy_node", "NODE-1")];
            await fixture.Database.SeedStatic(tenantId, "commission.mst_intermediary_register", "ACTIVE", code, From, field is null ? fields : [.. fields, (field, party)]);
        }

        var readiness = await Readiness(url);
        var check = Check(readiness, "PAYMENTS_AND_DISTRIBUTION", "INTERMEDIARY_NO_CONTACT");
        (check.GetProperty("ready").GetBoolean(), check.GetProperty("advisory").GetBoolean()).ShouldBe((false, true));
        var listed = check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ToList();
        listed.ShouldContain("BRK-NONE");
        listed.ShouldNotContain("BRK-CONTACT");
        listed.ShouldNotContain("BRK-PARTY");
        var codes = readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == "PAYMENTS_AND_DISTRIBUTION")
            .GetProperty("checks").EnumerateArray().Where(c => !c.GetProperty("ready").GetBoolean() && !c.GetProperty("advisory").GetBoolean()).Select(c => c.GetProperty("code").GetString());
        codes.ShouldNotContain("INTERMEDIARY_NO_CONTACT");
    }

    /// <summary>ADR-023: an endpoint requiring a client token blocks the step until an active user holds the function; a revoked endpoint, a suspended holder and a role without it do not count.</summary>
    [Fact]
    public async Task AnEndpointRequiringAClientToken_BlocksTheStep_UntilAnActiveServiceIdentityHoldsTheFunction_ADR023()
    {
        var (tenantId, url) = await Register("CBCLIENT");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "PAYMENT_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeTrue("no endpoint requires one");

        Task Endpoint(string provider, string status, bool requires) => fixture.Database.ExecuteAsOwner("""
            INSERT INTO payment.callback_endpoint (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, provider_code, key_hash, secret_enc, status,
                                                   require_client_token, issued_at, issued_by, revoked_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @p, rpad(@p, 64, 'x'), decode('00', 'hex'), @s, @r, now(), 'seed', CASE WHEN @s = 'REVOKED' THEN now() END)
            """, ("t", tenantId), ("p", provider), ("s", status), ("r", requires));
        await Endpoint("OLD", "REVOKED", true);
        await Endpoint("OPEN", "ACTIVE", false);
        Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "PAYMENT_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeTrue("revoked, or needing no token");

        await Endpoint("TOKENED", "ACTIVE", true);
        var check = Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "PAYMENT_CALLBACK_CLIENT");
        (check.GetProperty("ready").GetBoolean(), check.GetProperty("advisory").GetBoolean()).ShouldBe((false, false));
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["TOKENED"]);

        await fixture.Database.UserWithPermissions(tenantId, "callback-other", PaymentPermissions.CallbackEndpointRead);
        Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "PAYMENT_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeFalse("a role without the function");

        var holder = await fixture.Database.UserWithFunctions(tenantId, "callback-client", PaymentFunctionCodes.CallbackClient);
        Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "PAYMENT_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeTrue();

        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_account SET status = 'DISABLED' WHERE id = @u", ("u", holder));
        Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "PAYMENT_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeFalse("the holder is no longer active");
    }

    /// <summary>ADR-023: an endpoint requiring a client token blocks the step until an active user holds the function; a revoked endpoint, a suspended holder and a role without it do not count.</summary>
    [Fact]
    public async Task AMessagingEndpointRequiringAClientToken_BlocksTheStep_UntilAnActiveServiceIdentityHoldsTheFunction_ADR023()
    {
        var (tenantId, url) = await Register("CBCOMMS");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "COMMUNICATION_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeTrue("no endpoint requires one");

        Task Endpoint(string provider, string status, bool requires) => fixture.Database.ExecuteAsOwner("""
            INSERT INTO communication.callback_endpoint (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, provider_code, key_hash, secret_enc, status,
                                                   require_client_token, issued_at, issued_by, revoked_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @p, rpad(@p, 64, 'x'), decode('00', 'hex'), @s, @r, now(), 'seed', CASE WHEN @s = 'REVOKED' THEN now() END)
            """, ("t", tenantId), ("p", provider), ("s", status), ("r", requires));
        await Endpoint("OLD", "REVOKED", true);
        await Endpoint("OPEN", "ACTIVE", false);
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "COMMUNICATION_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeTrue("revoked, or needing no token");

        await Endpoint("TOKENED", "ACTIVE", true);
        var check = Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "COMMUNICATION_CALLBACK_CLIENT");
        (check.GetProperty("ready").GetBoolean(), check.GetProperty("advisory").GetBoolean()).ShouldBe((false, false));
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["TOKENED"]);

        await fixture.Database.UserWithPermissions(tenantId, "callback-other", CommunicationPermissions.CallbackEndpointRead);
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "COMMUNICATION_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeFalse("a role without the function");

        var holder = await fixture.Database.UserWithFunctions(tenantId, "callback-client", CommunicationFunctionCodes.CallbackClient);
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "COMMUNICATION_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeTrue();

        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_account SET status = 'DISABLED' WHERE id = @u", ("u", holder));
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "COMMUNICATION_CALLBACK_CLIENT").GetProperty("ready").GetBoolean().ShouldBeFalse("the holder is no longer active");
    }

    /// <summary>A HEALTH tenant whose reversal rule credits an account CLAIM_PAID does not debit gets a warning, not a blocker: the reversal will not net to zero.</summary>
    [Fact]
    public async Task AReversalRuleCreditingAnotherAccount_IsListedAsAWarning_ThatDoesNotStopTheStep()
    {
        var (tenantId, url) = await Register("REVACCT");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        await fixture.Database.EntitleToAllFeatures("REVACCT");
        Check(await Readiness(url), "FINANCIAL_FOUNDATIONS", "CLAIM_REVERSAL_ACCOUNTS").GetProperty("ready").GetBoolean().ShouldBeTrue("the seeded rules use one account");

        await fixture.Database.SeedStaticFilled(tenantId, "finance.cfg_posting_rules", "EFFECTIVE", "PR-REV-OTHER", From, ("rule_code", "PR-REV-OTHER"),
            ("transaction_type", "CLAIM_PAID_REVERSED"), ("debit_account", "1100"), ("credit_account", "2199"));
        var readiness = await Readiness(url);
        var check = Check(readiness, "FINANCIAL_FOUNDATIONS", "CLAIM_REVERSAL_ACCOUNTS");
        (check.GetProperty("ready").GetBoolean(), check.GetProperty("advisory").GetBoolean()).ShouldBe((false, true));
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["2199"]);
        Check(readiness, "FINANCIAL_FOUNDATIONS", "POSTING_RULES").GetProperty("ready").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// BR-113 (decided 30 October 2026): an acceptance or referral rule in force with no product and no domain applies to
    /// every product. It is listed as a warning, which does not stop the step; it clears once the rule has a scope.
    /// </summary>
    [Fact]
    public async Task AnUnscopedUnderwritingRule_IsListedAsAWarning_ThatDoesNotStopTheStep_BR113()
    {
        var (tenantId, url) = await Register("UNSCOPED");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        Check(await Readiness(url), "PRODUCT_FACTORY", "UNSCOPED_UNDERWRITING_RULES").GetProperty("ready").GetBoolean().ShouldBeTrue();

        await fixture.Database.SeedStatic(tenantId, "rules.cfg_acceptance_and_referral_rules", "EFFECTIVE", "UW-SA", From, ("rule_code", "UW-SA"),
            ("condition", "sum_assured > threshold"), ("threshold", 5_000_000m), ("outcome", "REFER"));
        await fixture.Database.SeedStatic(tenantId, "rules.cfg_acceptance_and_referral_rules", "EFFECTIVE", "UW-GEN", From, ("rule_code", "UW-GEN"),
            ("condition", "claims_count >= 1"), ("threshold", 0m), ("outcome", "REFER"), ("domain", "GENERAL"));
        var readiness = await Readiness(url);
        var check = Check(readiness, "PRODUCT_FACTORY", "UNSCOPED_UNDERWRITING_RULES");
        (check.GetProperty("ready").GetBoolean(), check.GetProperty("advisory").GetBoolean()).ShouldBe((false, true));
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["UW-SA"], "the rule with a domain is scoped");
        readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == "PRODUCT_FACTORY").GetProperty("ready").GetBoolean()
            .ShouldBeTrue("a warning does not stop the step");

        // An approved version is immutable (SDM-002): the unscoped one is retired and a version scoped to LIFE takes its place.
        await fixture.Database.ExecuteAsOwner("UPDATE rules.cfg_acceptance_and_referral_rules SET record_status = 'RETIRED' WHERE tenant_id = @t AND code = 'UW-SA'",
            ("t", tenantId));
        await fixture.Database.SeedStatic(tenantId, "rules.cfg_acceptance_and_referral_rules", "EFFECTIVE", "UW-SA", 2, From, null, ("rule_code", "UW-SA"),
            ("condition", "sum_assured > threshold"), ("threshold", 5_000_000m), ("outcome", "REFER"), ("domain", "LIFE"));
        Check(await Readiness(url), "PRODUCT_FACTORY", "UNSCOPED_UNDERWRITING_RULES").GetProperty("ready").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Every approval type the tenant can raise has a role that can decide it (Ade, 29 October 2026). With due diligence on,
    /// the enhanced due diligence approval is listed while no role holds authority for it, or while the role with authority
    /// lacks its permission; with the gates off it cannot be raised, and is not listed.
    /// </summary>
    [Fact]
    public async Task AnApprovalTypeTheTenantCanRaise_WithNoRoleToDecideIt_IsListed_DueDiligenceOn()
    {
        const string enhanced = Party.Contracts.PartyApprovals.EnhancedDueDiligenceType;
        var (tenantId, url) = await Register("NODECIDER");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "APPROVAL_AUTHORITY").GetProperty("ready").GetBoolean().ShouldBeTrue();

        // No authority for the enhanced approval: the delegation row retired.
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.cfg_delegation_and_approval_authority SET record_status = 'RETIRED' WHERE tenant_id = @t AND code = @c",
            ("t", tenantId), ("c", $"AUTH-{enhanced}"));
        var check = Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "APPROVAL_AUTHORITY");
        check.GetProperty("ready").GetBoolean().ShouldBeFalse();
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe([enhanced]);

        // Authority again, but the role no longer holds due_diligence.approve_enhanced: still undecidable.
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.cfg_delegation_and_approval_authority SET record_status = 'EFFECTIVE' WHERE tenant_id = @t AND code = @c",
            ("t", tenantId), ("c", $"AUTH-{enhanced}"));
        await fixture.Database.ExecuteAsOwner("""
            UPDATE iam.cfg_role_function_assignments SET record_status = 'RETIRED'
            WHERE tenant_id = @t AND role_code = 'CLERK' AND function_code IN (SELECT iam.functions_within(ARRAY['due_diligence.approve_enhanced']))
            """, ("t", tenantId));
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "APPROVAL_AUTHORITY").GetProperty("missing").EnumerateArray().Select(m => m.GetString())
            .ShouldBe([enhanced]);

        // With the gates off (the settings row retired: a tenant not entitled to LIFE then has none) the due diligence approvals are never raised.
        await fixture.Database.ExecuteAsOwner("UPDATE party.cfg_due_diligence_settings SET record_status = 'RETIRED' WHERE tenant_id = @t", ("t", tenantId));
        Check(await Readiness(url), "SHARED_OPERATIONAL_SETUP", "APPROVAL_AUTHORITY").GetProperty("ready").GetBoolean().ShouldBeTrue();
    }

    private async Task<(Guid TenantId, string Url)> Register(string code)
    {
        var registered = await _operator.Send(HttpMethod.Post, "/api/v1/tenancy/tenants",
            new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" }, idempotent: true);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var id = (await registered.Json()).GetProperty("id").GetGuid();
        return (id, $"/api/v1/tenancy/tenants/{id}");
    }

    private async Task<JsonElement> Readiness(string url)
    {
        var response = await _operator.GetAsync($"{url}/readiness");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private static JsonElement Check(JsonElement readiness, string step, string code) =>
        readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == step)
            .GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);

    private async Task<string> Complete(string url, string etag, string step)
    {
        var completed = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", OnboardingSeed.StepBody(step), ifMatch: etag);
        completed.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{step}: {await completed.Content.ReadAsStringAsync()}");
        return completed.ETag();
    }
}
