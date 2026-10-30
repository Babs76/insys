/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : EndToEndTests
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
using System.Net.Http.Json;
using System.Text.Json;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.ClaimsFramework.Contracts;
using Insys.Documents.Contracts;
using Insys.Finance.Contracts;
using Insys.General.Contracts;
using Insys.Integration.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.EndToEndTests;

/// <summary>
/// The General processes of domain-model.json, through the API with the real migrations and every module involved:
/// "General quote to cash" (§27.3 scenarios 2–4) and "General claim to settlement" (§27.3 scenario 5, GEN-006 to
/// GEN-008), with their compensations, and endorsement and cancellation (GEN-003, GEN-004). The commission and
/// reinsurance steps are in EnterpriseProcessTests.
/// </summary>
public sealed class GeneralProcessTests(PostgresServer server) : IAsyncLifetime
{
    private const string Product = "E2E-GMOT";
    private static readonly Guid Tenant = TestTenants.A;
    private static readonly DateOnly Today = WindowClock.Today;
    private static readonly Guid Assessor = Guid.CreateVersion7();
    private static readonly Guid Repairer = Guid.CreateVersion7();

    private static readonly string[] Permissions =
    [
        GeneralPermissions.RiskRead, GeneralPermissions.RiskCreate, GeneralPermissions.RiskUpdate, GeneralPermissions.QuoteRead, GeneralPermissions.QuoteCreate,
        GeneralPermissions.QuoteUpdate, GeneralPermissions.QuoteUnderwrite, GeneralPermissions.PolicyRead, GeneralPermissions.PolicyCreate, GeneralPermissions.PolicyUpdate,
        GeneralPermissions.PolicyCancel, GeneralPermissions.EndorsementRead, GeneralPermissions.EndorsementCreate, GeneralPermissions.ClaimRead, GeneralPermissions.ClaimCreate,
        GeneralPermissions.ClaimUpdate, GeneralPermissions.ClaimDecide, GeneralPermissions.AssessmentUpdate, GeneralPermissions.RecoveryUpdate,
        GeneralPermissions.AggregationAlertRead,
        RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead, BillingPermissions.AccountRead, BillingPermissions.InvoiceRead,
        PaymentPermissions.PaymentRead, PaymentPermissions.PaymentResolve, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease,
        PaymentPermissions.CallbackEndpointUpdate, ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead, ClaimsPermissions.FinancialsUpdate,
        ClaimsPermissions.InstructionRead, ClaimsPermissions.InstructionRelease, ClaimsPermissions.InstructionCreate, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate,
        DocumentPermissions.DocumentRead, DocumentPermissions.DocumentCreate, Insys.Party.Contracts.PartyPermissions.PartyCreate, Insys.Party.Contracts.PartyPermissions.PartyUpdate,
        Insys.Party.Contracts.PartyPermissions.PartyRead, Insys.Party.Contracts.PartyPermissions.BankAccountCreate,
        Insys.Party.Contracts.PartyPermissions.BankAccountRead, Insys.Party.Contracts.PartyPermissions.BankAccountVerify,
        Insys.Party.Contracts.PartyPermissions.DueDiligenceRead, Insys.Party.Contracts.PartyPermissions.DueDiligenceReviewHit,
        GeneralPermissions.RenewalRespond, GeneralPermissions.PolicyNonRenew,
    ];

    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
        _watchdog = StallWatchdog.Start(_db, TimeSpan.FromSeconds(90), $"GeneralProcessTests.{TestContext.Current.TestMethod?.MethodName}");
        await _db.SeedTenant(Tenant, "TENA");
        var underwriter = await _db.UserWithPermissions(Tenant, "underwriter", Permissions);
        var checker = await _db.UserWithPermissions(Tenant, "senior-underwriter", Permissions);
        var approverRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, checker, approverRole);
        await _db.AllowBankAccountVerification(Tenant, approverRole, Today.AddYears(-1));
        var from = Today.AddDays(-30);

        foreach (var type in new[] { GeneralApprovals.EndorsementApprovalType, GeneralApprovals.SettlementApprovalType, PaymentApprovals.PayoutApprovalType, ClaimsApprovals.InstructionApprovalType,
                     PartyApprovals.MatchResolutionType, GeneralApprovals.NonRenewalApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}",
                ("role_code", approverRole), ("transaction_type", type), ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        // Underwriting authority (§7.4): the senior underwriter decides referrals up to 500 000, the underwriter up to 50 000.
        var underwriterRole = (await _db.ScalarAsOwner<string>("SELECT role_code FROM iam.user_role_assignment WHERE user_id = @u", ("u", underwriter)))!;
        await Seed("rules.cfg_authority_matrices", "EFFECTIVE", "AM-SENIOR", ("role_code", approverRole), ("product_code", Product), ("max_sum_assured", 500_000m),
            ("decision_types", new[] { "ACCEPT", "DECLINE" }));
        await Seed("rules.cfg_authority_matrices", "EFFECTIVE", "AM-UW", ("role_code", underwriterRole), ("product_code", Product), ("max_sum_assured", 50_000m),
            ("decision_types", new[] { "ACCEPT", "DECLINE" }));

        // A published General motor product: coverages, a limit and an excess schedule, and its document templates.
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'GENERAL', 'Motor', @coverages, 'GEN-MOTOR', 'MOTOR', 'ISSUE')
            """, ("t", Tenant), ("p", Product), ("from", from), ("coverages", new[] { "OWN_DAMAGE", "THEFT" }));
        await Seed("product.cfg_limit_and_deductible_schedules", "EFFECTIVE", "MOT-LIM", ("schedule_code", "MOT-LIM"), ("coverage", "OWN_DAMAGE"), ("limit_type", "LIMIT"),
            ("amount", 500_000m), ("rule", "FIXED"), ("currency", "ZAR"));
        await Seed("product.cfg_limit_and_deductible_schedules", "EFFECTIVE", "MOT-EXC", ("schedule_code", "MOT-EXC"), ("coverage", "OWN_DAMAGE"), ("limit_type", "EXCESS"),
            ("amount", 5_000m), ("percentage", 5m), ("rule", "GREATER_OF"), ("currency", "ZAR"));
        foreach (var (type, fields) in new[]
                 {
                     ("POLICY_SCHEDULE", new[] { "policy_number", "annual_premium" }), ("CERTIFICATE", new[] { "policy_number", "period_start" }),
                     ("ENDORSEMENT", new[] { "endorsement_number", "effective_date" }),
                 })
        {
            await Seed("document.ref_document_categories", "ACTIVE", type, ("category_code", type), ("label", type), ("classification", "PERSONAL"), ("domain", "ALL"),
                ("allowed_roles", Array.Empty<string>()));
            await Seed("document.cfg_document_templates", "EFFECTIVE", $"TPL-{type}", ("template_code", $"TPL-{type}"), ("language", "en"), ("document_type", type),
                ("body_ref", $"doc://templates/{type}/v1"), ("merge_fields", fields));
            if (type != "CERTIFICATE")
            {
                await AssignTemplate(type);
            }
        }

        // Rating: premium = sum insured × 0.03 for vehicle group G3, plus a fee of 50.
        await Seed("rating.cfg_rating_factor_definitions", "EFFECTIVE", "VEHICLE_GROUP", ("factor_code", "VEHICLE_GROUP"), ("data_type", "CODE"), ("source_attribute", "vehicle_group"));
        await _db.ExecuteAsOwner("""
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'GMOT-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'GMOT-BASE', ARRAY['VEHICLE_GROUP'], '[{"key":["G3"],"value":0.03}]', 'NONE')
            """, ("t", Tenant), ("from", from));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GMOT-P1", ("parameter_code", "GMOT-BASE"), ("product_version", $"{Product} v1"), ("value", 1m), ("unit", "FACTOR"));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GMOT-P2", ("parameter_code", "POLICY_FEE"), ("product_version", $"{Product} v1"), ("value", 50m), ("unit", "AMOUNT"));
        await Seed("rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", ("rule_code", "ZAR-2DP"), ("currency", "ZAR"), ("step", 0.01m),
            ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));

        // General: the vehicle type, quote validity, numbering, cancellation, endorsement and renewal rules, codes and panels.
        await _db.ExecuteAsOwner("""
            INSERT INTO general.cfg_risk_object_types_and_attribute_schemas (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                                             object_type, attributes, validation)
            VALUES (@t, 'VEHICLE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'VEHICLE', '["registration:String(10):Y","vehicle_group:String(5):Y","year:Integer:Y"]',
                    '["year <= current_year"]')
            """, ("t", Tenant), ("from", from));
        await Seed("general.cfg_quote_validity_periods", "EFFECTIVE", "QV-GMOT", ("product_code", Product), ("days_valid", 30), ("re_rate_on_expiry", false));
        await Seed("general.cfg_policy_numbering_schemes", "EFFECTIVE", "GP", ("product_line", "GENERAL"), ("prefix", "GP"), ("sequence_length", 9));
        await Seed("general.cfg_claim_party_roles", "EFFECTIVE", "WITNESS", ("label", "Witness"));
        await Seed("general.cfg_cancellation_reasons_and_refund_rules", "EFFECTIVE", "INS_REQUEST", ("reason_code", "INS_REQUEST"), ("initiator", "INSURED"),
            ("refund_basis", "PRO_RATA"), ("fee", 0m));
        await Seed("general.cfg_endorsement_types", "EFFECTIVE", "CHANGE_SI", ("premium_method", "PRO_RATA"), ("documents", new[] { "ENDORSEMENT" }), ("requires_underwriting", false));
        await Seed("general.ref_loss_cause_codes", "ACTIVE", "COLLISION", ("cause_code", "COLLISION"), ("peril", "ACC"), ("label", "Collision"));
        await Seed("general.ref_peril_codes", "ACTIVE", "ACC", ("peril_code", "ACC"), ("label", "Accident"), ("line", "MOTOR"), ("catastrophe", false));
        await Seed("general.ref_recovery_types", "ACTIVE", "SUBROGATION", ("label", "Subrogation"));
        await Seed("general.mst_repairer_and_assessor_panels", "ACTIVE", "ASSESSOR-1", ("party", Assessor.ToString()), ("panel_type", "ASSESSOR"), ("location", "CPT"),
            ("approved_lines", new[] { "MOTOR" }), ("status", "ACTIVE"));
        await Seed("general.mst_repairer_and_assessor_panels", "ACTIVE", "REPAIRER-1", ("party", Repairer.ToString()), ("panel_type", "REPAIRER"), ("location", "CPT"),
            ("approved_lines", new[] { "MOTOR" }), ("status", "ACTIVE"));

        // Billing, Payments, Claims and Finance, as in Phase 3.
        await Seed("billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", ("label", "Monthly"), ("periods_per_year", 12));
        await Seed("billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("payment_method", "DEBIT_ORDER"), ("lead_time", TimeSpan.FromDays(2)));
        await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "INS_REQUEST", ("label", "Cancelled at the insured's request"),
            ("requires_approval", false), ("accounting_impact", "CREDIT"));
        await Seed("payment.ref_payment_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("direction", "COLLECT"), ("currencies", new[] { "ZAR" }),
            ("countries", new[] { "ZA" }));
        await Seed("payment.ref_payment_methods", "ACTIVE", "EFT", ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await Seed("payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"),
            ("supported_methods", new[] { "DEBIT_ORDER", "EFT" }), ("credential_ref", "vault:simulator"));
        foreach (var (status, platform) in new[] { ("PAID", "SUCCEEDED"), ("BOUNCED", "FAILED") })
        {
            await Seed("payment.ref_payment_status_mappings", "ACTIVE", $"SIM-{status}", ("provider_code", "SIMULATOR"), ("provider_status", status),
                ("platform_status", platform), ("final", true));
        }

        await Seed("claims.ref_reserve_types", "ACTIVE", "INDEMNITY", ("category", "INDEMNITY"), ("posting_rule", "PR-CLM-UP"));
        await Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "GENERAL", ("domain", "GENERAL"), ("prefix", "GEN"), ("sequence_length", 8));
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"),
            ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        foreach (var account in new[] { "1100", "1200", "2100", "4000", "5000", "6000" })
        {
            await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", ("account_code", account), ("name", account), ("type", "ASSET"),
                ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        foreach (var (rule, type, debit, credit) in new[]
                 {
                     ("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"), ("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"),
                     ("PR-CREDIT", TransactionTypes.PremiumCredited, "4000", "1100"), ("PR-CLM-UP", TransactionTypes.ClaimReserveIncrease, "5000", "2100"),
                     ("PR-CLM-PAID", TransactionTypes.ClaimPaid, "2100", "1200"), ("PR-CLM-REC", TransactionTypes.ClaimRecovery, "1200", "5000"),
                     ("PR-REVERSED", TransactionTypes.PaymentReversed, "1100", "1200"),
                 })
        {
            await Seed("finance.cfg_posting_rules", "EFFECTIVE", rule, ("rule_code", rule), ("transaction_type", type), ("debit_account", debit), ("credit_account", credit));
        }

        var month = new DateOnly(Today.Year, Today.Month, 1);
        await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{month:yyyy-MM}", ("period_code", $"{month:yyyy-MM}"), ("start_date", month),
            ("end_date", month.AddMonths(1).AddDays(-1)));

        // Fast retries, so a failing consumer is dead-lettered within one drain of the outbox.
        _factory = new InsysApiFactory(_db, new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoff"] = "00:00:00" }, time: WindowClock.Time);
        _maker = _factory.ClientFor(Tenant, "underwriter");
        _checker = _factory.ClientFor(Tenant, "senior-underwriter");
        (await _maker.PostAsync($"/api/v1/finance/periods/{month:yyyy-MM}/open", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private StallWatchdog? _watchdog;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (_watchdog is not null)
        {
            await _watchdog.DisposeAsync();
        }
    }

    /// <summary>
    /// F103 (addenda/access-scopes.md): a role assignment scoped to a region covers its branches only; one scoped to a
    /// product covers that product only. Lists show only records in scope, a single read of another's record is not found,
    /// an action on it is refused with OUT_OF_SCOPE, and a quote cannot be created outside the scope.
    /// </summary>
    [Fact]
    public async Task ScopedAssignments_SeeAndActOnlyWithinTheirOrganisationAndProduct_F103()
    {
        foreach (var (code, type, parent) in new[] { ("ORG-ROOT", "LEGAL_ENTITY", (string?)null), ("ORG-NORTH", "REGION", "ORG-ROOT"), ("ORG-BR-A", "BRANCH", "ORG-NORTH"),
                     ("ORG-BR-B", "BRANCH", "ORG-ROOT") })
        {
            await Seed("tenant.mst_organisation_structure", "ACTIVE", code, ("node_code", code), ("type", type), ("parent", (object?)parent ?? DBNull.Value), ("name", code));
        }

        var inA = await QuoteAt("ORG-BR-A");
        var inB = await QuoteAt("ORG-BR-B");

        // A user scoped to the North region (which covers branch A), and one scoped to another product.
        var north = await _db.UserWithPermissions(Tenant, "scoped-north", Permissions);
        await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_branches = '{ORG-NORTH}' WHERE user_id = @u", ("u", north));
        var otherProduct = await _db.UserWithPermissions(Tenant, "scoped-product", Permissions);
        await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = '{OTHER-PROD}' WHERE user_id = @u", ("u", otherProduct));
        var northClient = _factory.ClientFor(Tenant, "scoped-north");
        var productClient = _factory.ClientFor(Tenant, "scoped-product");

        var listed = (await (await northClient.GetAsync("/api/v1/general/quotes?limit=200")).Json()).GetProperty("items").EnumerateArray()
            .Select(q => q.GetProperty("id").GetGuid()).ToList();
        listed.ShouldContain(inA);
        listed.ShouldNotContain(inB);
        (await northClient.GetAsync($"/api/v1/general/quotes/{inA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await northClient.GetAsync($"/api/v1/general/quotes/{inB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a record outside the scope is not revealed");
        var refused = await northClient.PostAsync($"/api/v1/general/quotes/{inB}/issue", null);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("OUT_OF_SCOPE");

        // Creating: inside the scope is allowed; another branch is refused.
        var inside = await northClient.Send(HttpMethod.Post, "/api/v1/general/quotes", QuoteBody(await Vehicle(), "ORG-BR-A"), idempotent: true);
        inside.StatusCode.ShouldBe(HttpStatusCode.Created, await inside.Content.ReadAsStringAsync());
        var outside = await northClient.Send(HttpMethod.Post, "/api/v1/general/quotes", QuoteBody(await Vehicle(), "ORG-BR-B"), idempotent: true);
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await outside.Code()).ShouldBe("OUT_OF_SCOPE");

        // A product scope: none of this product's quotes are in it.
        (await productClient.GetAsync($"/api/v1/general/quotes/{inA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await productClient.GetAsync("/api/v1/general/quotes?limit=200")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);

        // GAP-046 decision 2: the dashboard's figures are projected by the policy's branch, so a branch scope sees its own.
        await IssuedPolicy(orgNodeCode: "ORG-BR-A");
        await _factory.DrainOutbox();
        foreach (var (subject, branch, expected) in new[] { ("figures-north", "ORG-NORTH", 1L), ("figures-b", "ORG-BR-B", 0L) })
        {
            var user = await _db.UserWithPermissions(Tenant, subject, Insys.Analytics.Contracts.AnalyticsPermissions.ProjectionRead);
            await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_branches = ARRAY[@n] WHERE user_id = @u", ("n", branch), ("u", user));
            var figures = await (await _factory.ClientFor(Tenant, subject).GetAsync($"/api/v1/analytics/summary?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).Json();
            figures.GetProperty("figures").EnumerateArray().Single(f => f.GetProperty("code").GetString() == "POLICIES_WRITTEN").GetProperty("count").GetInt64()
                .ShouldBe(expected, subject);
        }
    }

    /// <summary>
    /// F005 (addenda/channel-permissions.md): once a tenant has channels, a command comes through the channel of its
    /// token's client, and the caller's roles need an allowed row for that channel, transaction and product. Reads are not
    /// channel-scoped. The channel is recorded on the quote.
    /// </summary>
    [Fact]
    public async Task ChannelPermissions_AllowOnlyTheTransactionsAndProductsTheChannelGivesTheRoles_F005()
    {
        // Risk objects are General commands too, so the vehicles are registered before the tenant has channels.
        var vehicles = new Queue<Guid>();
        for (var i = 0; i < 6; i++)
        {
            vehicles.Enqueue(await Vehicle());
        }

        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-BRK", ("channel_code", "BRK"), ("audience", "BROKER"), ("name", "Broker portal"),
            ("client_ids", new[] { "broker-portal" }), ("enabled_products", new[] { Product }));
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-SVC", ("channel_code", "SVC"), ("audience", "CUSTOMER"), ("name", "Self service"),
            ("client_ids", new[] { "self-service" }), ("enabled_transactions", new[] { GeneralPermissions.QuoteRead }));

        async Task<HttpClient> User(string subject, string? product, bool requiresApproval, bool withRow = true)
        {
            var role = await _db.RoleWithPermissions(Tenant, Permissions);
            await _db.AssignRole(Tenant, await _db.UserWithPermissions(Tenant, subject), role);
            if (withRow)
            {
                await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", $"CP-{subject}", ("channel_code", "BRK"), ("role_code", role),
                    ("transaction_type", GeneralPermissions.QuoteCreate), ("product_code", (object?)product ?? DBNull.Value), ("allowed", true),
                    ("requires_approval", requiresApproval));
                await _db.AdoptFunctionCatalogue();
            }

            return _factory.ClientFor(Tenant, subject, "broker-portal");
        }

        async Task<HttpResponseMessage> Quote(HttpClient client) =>
            await client.Send(HttpMethod.Post, "/api/v1/general/quotes", QuoteBody(vehicles.Dequeue(), null), idempotent: true);

        var allowed = await User("ch-any-product", null, false);
        var created = await Quote(allowed);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var quote = (await created.Json()).GetProperty("id").GetGuid();
        (await _db.ScalarAsOwner<string>("SELECT channel_code FROM general.quote WHERE id = @q", ("q", quote))).ShouldBe("BRK", "the channel is recorded (§3)");

        foreach (var (client, code) in new[]
                 {
                     (await User("ch-other-product", "OTHER-PROD", false), "CHANNEL_NOT_ALLOWED"),
                     (await User("ch-approval", null, true), "CHANNEL_APPROVAL_REQUIRED"),
                     (await User("ch-no-row", null, false, withRow: false), "CHANNEL_NOT_ALLOWED"),
                     (_factory.ClientFor(Tenant, "ch-any-product"), "CHANNEL_NOT_ENABLED"),
                     (_factory.ClientFor(Tenant, "ch-any-product", "self-service"), "CHANNEL_NOT_ENABLED"),
                 })
        {
            var refused = await Quote(client);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, code);
            (await refused.Code()).ShouldBe(code);
        }

        // Reads are not channel-scoped: the same user reads the quote with no client at all.
        (await _factory.ClientFor(Tenant, "ch-any-product").GetAsync($"/api/v1/general/quotes/{quote}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// F062 §1 (addenda/aggregation-limits.md): a quote that would take a zone and peril past its limit is referred to an
    /// underwriter instead of issued, and issues once they approve; an endorsement over it tells its approver which limit.
    /// </summary>
    [Fact]
    public async Task ZoneLimit_RefersAQuoteOverIt_AndNamesItToTheEndorsementApprover_F062()
    {
        await Seed("general.ref_risk_zones", "ACTIVE", "ZHAIL", ("zone_code", "ZHAIL"), ("peril", "HAIL"), ("geography", JsonSerializer.SerializeToElement(new { region = "Hail belt" })));
        await Seed("general.ref_underwriting_decision_codes", "ACTIVE", "ACCEPT", ("label", "Accepted"), ("requires_terms", false));
        await Seed("general.cfg_aggregation_zones_and_limits", "EFFECTIVE", "ZL-HAIL", ("zone", "ZHAIL"), ("peril", "HAIL"), ("max_exposure", 150_000m), ("currency", "ZAR"));

        // In force: 100 000 in the zone, within the limit.
        var (policy, vehicle) = await IssuedPolicy(riskZone: "ZHAIL");

        // Another 100 000 there would make 200 000: referred, not issued.
        var second = await Created("/api/v1/general/risk-objects", new
        {
            objectType = "VEHICLE", attributes = new { registration = "CA654321", vehicle_group = "G3", year = Today.Year - 2 }, riskZone = "ZHAIL",
        });
        var quote = await Created("/api/v1/general/quotes", QuoteBody(second, null));
        await Rate(GeneralRatingSubjects.Quote, quote, 100_000m);
        var referred = await (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).Json();
        referred.GetProperty("status").GetString().ShouldBe("Referred");
        referred.GetProperty("referrals").EnumerateArray().Single().GetProperty("reasonCode").GetString().ShouldBe("AGG_LIMIT");

        // The underwriter may exceed the capacity limit: approved, the quote issues.
        (await _checker.PostAsJsonAsync($"/api/v1/general/quotes/{quote}/referral-decision", new { approved = true, decisionCode = "ACCEPT", specialConditions = Array.Empty<string>() }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).Json()).GetProperty("status").GetString().ShouldBe("Issued");

        // An endorsement adding 100 000 there: its approval says which limit it exceeds.
        var endorsement = await Created($"/api/v1/general/policies/{policy}/endorsements", new
        {
            endorsementType = "CHANGE_SI", effectiveDate = Today,
            changes = new[] { new { action = "ChangeRisk", riskObjectId = vehicle, sumInsured = 200_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 200_000m } } } },
        });
        await Rate(GeneralRatingSubjects.Endorsement, endorsement, 200_000m);
        (await _maker.PostAsync($"/api/v1/general/endorsements/{endorsement}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("SELECT reason FROM workflow.approval WHERE business_ref_id = @e", ("e", endorsement)))!
            .ShouldContain("exceeds aggregation limit ZHAIL/HAIL");

        // The daily alert: approved, the policy holds 200 000 of the 150 000 limit, past the 80% default. A limit with its own
        // level (99%) in another zone with no exposure raises nothing.
        await Seed("general.cfg_aggregation_zones_and_limits", "EFFECTIVE", "ZL-FLOOD", ("zone", "ZFLOOD"), ("peril", "FLOOD"), ("max_exposure", 1_000m),
            ("currency", "ZAR"), ("alert_at_pct", 99m));
        await Decide(GeneralApprovals.EndorsementBusinessRefType, endorsement, approve: true);
        await _db.UserWithPermissions(Tenant, "job-runner");
        var run = await _factory.ClientFor(Tenant, "job-runner").PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = Today });
        run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(payload::jsonb ->> 'zone', ',') FROM general.outbox_event WHERE event_type = 'general.AggregationLimitApproached'
            """)).ShouldBe("ZHAIL", "raised once, for the zone past its alert level");

        // GAP-051: the alert is recorded, listed by zone and peril, and opens one review task for the underwriting role.
        // A second run the same day records nothing more; the next day's alert is recorded but opens no second task.
        var jobs = _factory.ClientFor(Tenant, "job-runner");
        (await jobs.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = Today })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var alerts = (await (await _maker.GetAsync("/api/v1/general/aggregation-alerts?zone=ZHAIL&peril=HAIL")).Json()).GetProperty("items").EnumerateArray().ToList();
        var alert = alerts.ShouldHaveSingleItem("once a day");
        alert.GetProperty("exposure").GetDecimal().ShouldBe(200_000m);
        alert.GetProperty("maxExposure").GetDecimal().ShouldBe(150_000m);
        alert.GetProperty("alertAtPct").GetDecimal().ShouldBe(80m);
        alert.GetProperty("percentUsed").GetDecimal().ShouldBe(133.33m);
        alert.GetProperty("status").GetString().ShouldBe("EXCEEDED", "BR-035: over its maximum that day");
        alert.GetProperty("asAt").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));
        var zoneReference = alert.GetProperty("zoneReference").GetGuid();
        (await _maker.GetAsync($"/api/v1/general/aggregation-alerts/{alert.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _maker.GetAsync("/api/v1/general/aggregation-alerts?zone=ZFLOOD")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);

        const string OpenReviews = """
            SELECT t.queue_code FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
            WHERE p.process_code = 'AGGREGATION_REVIEW' AND p.business_ref_type = 'AGGREGATION_ZONE' AND p.business_ref_id = @z AND t.status = 'OPEN'
            """;
        (await _db.ScalarAsOwner<string>(OpenReviews, ("z", zoneReference))).ShouldBe("UNDERWRITING");

        (await jobs.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = Today.AddDays(1) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await (await _maker.GetAsync($"/api/v1/general/aggregation-alerts?zoneReference={zoneReference}")).Json()).GetProperty("items").GetArrayLength()
            .ShouldBe(2, "listed by the reference the task names");
        (await _db.ScalarAsOwner<long>(OpenReviews.Replace("t.queue_code", "count(*)", StringComparison.Ordinal), ("z", zoneReference))).ShouldBe(1L,
            "one open review per zone and peril");
    }

    /// <summary>
    /// F101 (addenda/communications.md §6.3): a claim's status notice reaches the policyholder by email, in the tenant's
    /// templates, and the claim's correspondence log records it as outbound.
    /// </summary>
    [Fact]
    public async Task ClaimStatusNotice_ReachesThePolicyholder_AndTheClaimLogsIt_F101()
    {
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "GPT", (Insys.Communications.Contracts.NotificationTypes.ClaimStatus, "Claim {claim_number}",
            "Your claim {claim_number} is {status} ({status_date})."));
        var (policy, vehicle) = await IssuedPolicy();
        var holder = await _maker.RegisterPerson("Holder");
        await _db.ExecuteAsOwner("UPDATE general.policy SET policyholder_party_id = @h WHERE id = @p", ("h", holder), ("p", policy));
        await _db.AddContact(Tenant, holder, "EMAIL", "holder@example.com");
        var claim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC",
        });
        var number = (await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json()).GetProperty("claimNumber").GetString()!;
        await _factory.DrainOutbox();
        await _factory.Services.GetRequiredService<Insys.Communications.Infrastructure.NotificationSender>().RunForTenant(Tenant, CancellationToken.None);
        await _factory.DrainOutbox();

        (await _db.ScalarAsOwner<string>("""
            SELECT status || ':' || channel || ':' || body FROM communication.notification
            WHERE recipient_party_id = @h AND type_code = 'CLAIM_STATUS' AND source_type = 'GENERAL_CLAIM' AND source_id = @c
            """, ("h", holder), ("c", claim))).ShouldBe($"SENT:EMAIL:Your claim {number} is REGISTERED ({Today:yyyy-MM-dd}).");
        (await _db.ScalarAsOwner<string>("SELECT direction || ':' || channel FROM claims.claim_communication WHERE claim_number = @n", ("n", number)))
            .ShouldBe("OUT:EMAIL", "the claim's correspondence log records the notice");
    }

    /// <summary>
    /// F006, Wave 4 addition 1 and Ade's decision of 29 October 2026: an instalment unpaid past its due date starts grace and
    /// tells the policyholder when cover ends. A loss in grace without cover is always accepted (notified, registered,
    /// assessed, approved); its payment is held until the arrears are paid, and is then released. By the tenant's rule it is
    /// paid less the arrears instead, which settles them. If grace ends unpaid, the policy is cancelled for non-payment, the
    /// policyholder told, and the claim declined with the rule's reason, its held payment withdrawn.
    /// </summary>
    [Fact]
    public async Task ALossInGrace_IsAccepted_ItsPaymentHeldUntilPaid_OrPaidLessTheArrears_OrDeclinedIfGraceEndsUnpaid_F006()
    {
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "GRC",
            (Insys.Communications.Contracts.NotificationTypes.GraceNotice, "Grace {policy_number}", "{policy_number}: cover ends on {cover_ends_on} unless paid."),
            (Insys.Communications.Contracts.NotificationTypes.NonPaymentCancellation, "Cancelled {policy_number}", "{policy_number} was cancelled from {cancelled_from}."),
            (Insys.Communications.Contracts.NotificationTypes.ClaimStatus, "Claim {claim_number}", "{claim_number} {status}: {amount} less premium {premium_deducted}, paid {net_amount}."),
            (Insys.Communications.Contracts.NotificationTypes.ClaimDecisionRights, "Your rights on {claim_number}", "{claim_number} declined: {decision_reasons}"));
        // Billing ages an overdue invoice into arrears only with a bucket in force (otherwise it parks it, and no grace starts).
        await Seed("billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "D1", ("bucket_code", "D1"), ("days_from", 1), ("days_to", 9999), ("action", "REMINDER"));
        await Seed("general.cfg_cancellation_reasons_and_refund_rules", "EFFECTIVE", "NON_PAYMENT", ("reason_code", "NON_PAYMENT"), ("initiator", "INSURER"),
            ("refund_basis", "NONE"), ("fee", 0m));
        await Seed("claims.ref_claim_decision_reason_codes", "ACTIVE", "NON_PAYMENT", ("decision_type", "DECLINE"),
            ("customer_wording", "The premium for the loss date was not paid."));
        // OQ-137: the non-cash premium receipt clears the premium receivable (1100) against the bank side (1200) that CLAIM_PAID credits.
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-PREM-OFFSET", ("rule_code", "PR-PREM-OFFSET"), ("transaction_type", TransactionTypes.PremiumOffset),
            ("debit_account", "1200"), ("credit_account", "1100"));
        // A 30 % retention quota share on the product: a recovery is on the gross claim paid.
        await Seed("reinsurance.cfg_treaty_definitions", "EFFECTIVE", "QS-1", ("treaty_code", "QS-1"), ("type", "QUOTA_SHARE"), ("retention", "30"), ("capacity", 5_000_000m),
            ("period_from", Today.AddYears(-1)), ("period_to", Today.AddYears(1)), ("participants", JsonSerializer.SerializeToElement(new[] { new { reinsurer = "RE-1", share = 100 } })));
        await Seed("reinsurance.cfg_cession_rules", "EFFECTIVE", "CR-1", ("rule_code", "CR-1"), ("treaty", "QS-1"), ("products", new[] { Product }),
            ("allocation_basis", "SUM_INSURED"));
        // Monthly: held until paid. Annual (the tenant's other rule): paid less the arrears. Neither keeps cover in grace.
        await Seed("general.cfg_grace_period_rules", "EFFECTIVE", $"GRACE-{Product}-MONTHLY", ("product_code", Product), ("frequency", "MONTHLY"), ("grace_days", 15),
            ("cover_during_grace", false), ("cancellation_reason", "NON_PAYMENT"), ("claim_decline_reason", "NON_PAYMENT"));
        await Seed("general.cfg_grace_period_rules", "EFFECTIVE", $"GRACE-{Product}-ANNUAL", ("product_code", Product), ("frequency", "ANNUAL"), ("grace_days", 15),
            ("cover_during_grace", false), ("cancellation_reason", "NON_PAYMENT"), ("claim_decline_reason", "NON_PAYMENT"), ("deduct_arrears_from_claims", true));

        // The policy starts on its first unpaid instalment's due date, so a cancellation from that date is within its period.
        async Task<(Guid Policy, Guid Vehicle, Guid Holder, string Number)> Unpaid(string name, DateOnly due, string frequency)
        {
            var (policy, vehicle) = await IssuedPolicy(start: due);
            var holder = await _maker.RegisterPerson(name);
            await _db.AddContact(Tenant, holder, "EMAIL", $"{name.ToLowerInvariant()}@example.com");
            await _db.ExecuteAsOwner("UPDATE general.policy SET policyholder_party_id = @h, billing_frequency = @f WHERE id = @p", ("h", holder), ("f", frequency), ("p", policy));
            await _factory.DrainOutbox();
            await _db.ExecuteAsOwner("UPDATE billing.invoice SET due_date = @d, issue_date = @d WHERE source_domain = 'GENERAL' AND policy_ref = @p", ("d", due), ("p", policy));
            await _db.ExecuteAsOwner("""
                UPDATE billing.premium_instalment i SET due_date = @d FROM billing.premium_schedule s
                WHERE s.id = i.schedule_id AND s.policy_ref = @p AND i.status = 'INVOICED'
                """, ("d", due), ("p", policy));
            return (policy, vehicle, holder, (await Policy(policy)).GetProperty("policyNumber").GetString()!);
        }

        // A loss today: notified, registered, assessed and its settlement approved; returns the claim and its payment instruction.
        async Task<(Guid Claim, Guid Instruction)> Approved(Guid policy, Guid vehicle, string payeeName)
        {
            var claim = await Created("/api/v1/general/claims", new
            {
                policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC", description = "In grace",
            });
            var registered = await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json();
            registered.GetProperty("status").GetString().ShouldBe("Registered", "a loss in grace is always accepted");
            var number = registered.GetProperty("claimNumber").GetString()!;
            (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 50_000m, currency = "ZAR", reason = "Assessed" }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Assessor })).StatusCode.ShouldBe(HttpStatusCode.OK);
            await _factory.DrainOutbox();
            var assessment = (await (await _maker.GetAsync($"/api/v1/general/claims/{claim}/assessments")).Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
            (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{assessment}/report", new { assessedAmount = 20_000m })).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var payee = await _maker.RegisterPerson(payeeName);
            var account = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, $"62 {Random.Shared.Next(1000, 9999)} {Random.Shared.Next(1000, 9999)}");
            (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/settlement",
                new { payeePartyId = payee, bankAccountId = account, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Within the assessed amount" }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            await _factory.DrainOutbox();
            await Decide(GeneralApprovals.SettlementBusinessRefType, claim, approve: true);
            await _factory.DrainOutbox();
            return (claim, await _db.ScalarAsOwner<Guid>("SELECT id FROM claims.claim_payment_instruction WHERE claim_number = @n", ("n", number)));
        }

        async Task<(string Status, string? Held, decimal Amount)> Instruction(Guid id)
        {
            var view = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{id}")).Json();
            return (view.GetProperty("status").GetString()!, view.GetProperty("heldReason").ValueKind == JsonValueKind.Null ? null : view.GetProperty("heldReason").GetString(),
                view.GetProperty("amount").GetDecimal());
        }

        var held = await Unpaid("Held", Today.AddDays(-5), "MONTHLY");
        var netted = await Unpaid("Netted", Today.AddDays(-5), "ANNUAL");
        var lapsing = await Unpaid("Lapsing", Today.AddDays(-20), "MONTHLY");
        await _factory.Services.GetRequiredService<Insys.Billing.Infrastructure.BillingJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var grace = (await Policy(held.Policy)).GetProperty("grace");
        (grace.GetProperty("arrearsSince").GetString(), grace.GetProperty("graceUntil").GetString(), grace.GetProperty("coverDuringGrace").GetBoolean())
            .ShouldBe((Today.AddDays(-5).ToString("yyyy-MM-dd"), Today.AddDays(10).ToString("yyyy-MM-dd"), false));

        // Path 1: held until the arrears are paid, then released; a person cannot release it before.
        var (_, heldInstruction) = await Approved(held.Policy, held.Vehicle, "Held Repairs");
        (await Instruction(heldInstruction)).ShouldBe(("Approved", "PREMIUM_ARREARS", 15_000m));
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/claims/payment-instructions/{heldInstruction}/release",
            ifMatch: (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{heldInstruction}")).ETag())).Code()).ShouldBe("PREMIUM_ARREARS");
        foreach (var payment in await _db.ScalarAsOwner<Guid[]>("""
                     SELECT array_agg(p.id) FROM payment.payment p JOIN billing.invoice i ON i.id = p.business_ref_id WHERE i.policy_ref = @p
                     """, ("p", held.Policy)) ?? [])
        {
            (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _factory.DrainOutbox();
        (await Policy(held.Policy)).GetProperty("grace").ValueKind.ShouldBe(JsonValueKind.Null);
        (await Instruction(heldInstruction)).ShouldBe(("Released", null, 15_000m));

        // Path 2: by the tenant's rule, paid less the arrears, which settles them.
        var owed = await _db.ScalarAsOwner<decimal>("SELECT sum(total_amount - allocated_amount) FROM billing.invoice WHERE source_domain = 'GENERAL' AND policy_ref = @p",
            ("p", netted.Policy));
        owed.ShouldBeGreaterThan(0m);
        var (_, nettedInstruction) = await Approved(netted.Policy, netted.Vehicle, "Netted Repairs");
        (await Instruction(nettedInstruction)).ShouldBe(("Released", null, 15_000m - owed));
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM billing.invoice WHERE source_domain = 'GENERAL' AND policy_ref = @p AND status <> 'PAID'", ("p", netted.Policy)))
            .ShouldBe(0L, "the arrears were settled from the claim");
        (await Policy(netted.Policy)).GetProperty("grace").ValueKind.ShouldBe(JsonValueKind.Null);

        // OQ-137: paid, the claim records the gross; Finance posts claims paid at the gross and a non-cash premium receipt for
        // the deduction, each journal balanced and the bank side netting to what was paid; the recovery is on the gross.
        await PayOut(nettedInstruction, "PAID");
        var nettedClaim = await _db.ScalarAsOwner<Guid>("SELECT domain_claim_id FROM claims.claim_register r JOIN claims.claim_payment_instruction i ON i.claim_number = r.claim_number WHERE i.id = @i",
            ("i", nettedInstruction));
        var settledClaim = await Claim(nettedClaim);
        settledClaim.GetProperty("status").GetString().ShouldBe("Settled");
        var split = settledClaim.GetProperty("paymentSplit");
        (split.GetProperty("gross").GetDecimal(), split.GetProperty("premiumDeducted").GetDecimal(), split.GetProperty("net").GetDecimal()).ShouldBe((15_000m, owed, 15_000m - owed));
        var nettedNumber = settledClaim.GetProperty("claimNumber").GetString()!;
        var paidMovement = (await (await _maker.GetAsync($"/api/v1/claims/claims/{nettedNumber}/financials")).Json()).GetProperty("movements").EnumerateArray()
            .Single(m => m.GetProperty("movementType").GetString() == "Payment");
        (paidMovement.GetProperty("amount").GetDecimal(), paidMovement.GetProperty("premiumOffset").GetDecimal(), paidMovement.GetProperty("netAmount").GetDecimal())
            .ShouldBe((15_000m, owed, 15_000m - owed));
        var journals = await Journals("CLAIM_MOVEMENT", paidMovement.GetProperty("id").GetGuid());
        journals.Count.ShouldBe(2, "claims paid, and the non-cash premium receipt");
        var lines = journals.SelectMany(j => j.GetProperty("lines").EnumerateArray()).ToList();
        foreach (var journal in journals)
        {
            var entries = journal.GetProperty("lines").EnumerateArray().ToList();
            entries.Sum(l => l.GetProperty("debit").GetDecimal()).ShouldBe(entries.Sum(l => l.GetProperty("credit").GetDecimal()), "each journal balances");
        }

        decimal Net(string account) => lines.Where(l => l.GetProperty("accountCode").GetString() == account).Sum(l => l.GetProperty("debit").GetDecimal() - l.GetProperty("credit").GetDecimal());
        (Net("2100"), Net("1200"), Net("1100")).ShouldBe((15_000m, -(15_000m - owed), -owed), "claims paid gross; the bank pays the net; the receivable is cleared");
        await _db.UserWithPermissions(Tenant, "ri-reader", Insys.Reinsurance.Contracts.ReinsurancePermissions.RecoveryRead);
        var recoveries = await _factory.ClientFor(Tenant, "ri-reader").GetAsync($"/api/v1/reinsurance/recoveries?claimNumber={nettedNumber}");
        recoveries.StatusCode.ShouldBe(HttpStatusCode.OK, await recoveries.Content.ReadAsStringAsync());
        var recovery = (await recoveries.Json()).GetProperty("items").EnumerateArray().Single();
        (recovery.GetProperty("paidAmount").GetDecimal(), recovery.GetProperty("recoverable").GetDecimal()).ShouldBe((15_000m, 10_500m), "the recovery is on the gross");

        // Path 3: grace ends unpaid. The policy is cancelled for non-payment from the first unpaid due date; the claim, approved
        // and held, is declined with the rule's reason and its payment withdrawn.
        var (lapsedClaim, lapsedInstruction) = await Approved(lapsing.Policy, lapsing.Vehicle, "Lapsed Repairs");
        (await Instruction(lapsedInstruction)).Held.ShouldBe("PREMIUM_ARREARS");
        await _factory.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var cancelled = await Policy(lapsing.Policy);
        (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("cancellations")[0].GetProperty("reasonCode").GetString(),
            cancelled.GetProperty("cancellations")[0].GetProperty("effectiveDate").GetString()).ShouldBe(("Cancelled", "NON_PAYMENT", Today.AddDays(-20).ToString("yyyy-MM-dd")));
        var declined = await Claim(lapsedClaim);
        (declined.GetProperty("status").GetString(), declined.GetProperty("decisionCode").GetString()).ShouldBe(("Declined", "NON_PAYMENT"));
        (await Instruction(lapsedInstruction)).ShouldBe(("Rejected", "PREMIUM_ARREARS", 15_000m));
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM payment.payment WHERE business_ref_id = @i", ("i", lapsedInstruction))).ShouldBe(0L, "nothing is paid");
        (await Policy(held.Policy)).GetProperty("status").GetString().ShouldBe("InForce", "paid in time");

        // The lapsed policy's notices, sent once each; the declined claim's rights (OQ-102); the settled claim's gross, deduction and net.
        await _factory.Services.GetRequiredService<Insys.Communications.Infrastructure.NotificationSender>().RunForTenant(Tenant, CancellationToken.None);
        var lapsedNumber = (await Claim(lapsedClaim)).GetProperty("claimNumber").GetString();
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(status || ':' || body, '|') FROM communication.notification WHERE recipient_party_id = @h AND type_code = 'CLAIM_DECISION_RIGHTS'
            """, ("h", lapsing.Holder))).ShouldBe($"SENT:{lapsedNumber} declined: The premium for the loss date was not paid.");
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(body, '|') FROM communication.notification WHERE recipient_party_id = @h AND type_code = 'CLAIM_STATUS' AND body LIKE '% SETTLED:%'
            """, ("h", netted.Holder))).ShouldBe($"{nettedNumber} SETTLED: 15000.00 less premium {owed:0.00}, paid {15_000m - owed:0.00}.");
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(type_code || ':' || status || ':' || body, '|' ORDER BY type_code) FROM communication.notification WHERE recipient_party_id = @h
              AND type_code IN ('GRACE_NOTICE', 'NON_PAYMENT_CANCELLATION')
            """, ("h", lapsing.Holder))).ShouldBe(string.Join('|',
            $"GRACE_NOTICE:SENT:{lapsing.Number}: cover ends on {Today.AddDays(-21):yyyy-MM-dd} unless paid.",
            $"NON_PAYMENT_CANCELLATION:SENT:{lapsing.Number} was cancelled from {Today.AddDays(-20):yyyy-MM-dd}."));
    }

    /// <summary>
    /// Wave 4 §4.2 (F015 to F019; OQ-131; addition 2): before the invitation, a reviewed product's renewal is reviewed. It reads
    /// the claims history, re-rates the renewal as a renewal quote priced through Rating, times the claims experience factor,
    /// and the product's underwriting rules refer it. The senior underwriter, whose authority covers the sum insured, offers it
    /// with a loading. The invitation states the offered premium, sent with the longer notice changed terms need, and the
    /// policy renews on the offered terms.
    /// </summary>
    [Fact]
    public async Task ARenewal_IsReviewedReRatedAndReferred_ThenOfferedInvitedAndRenewedOnTheOfferedTerms_F015_F019()
    {
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "RNW",
            (Insys.Communications.Contracts.NotificationTypes.RenewalInvitation, "Renew {policy_number}", "{policy_number} renews at {renewal_premium} {currency}."));
        await Seed("general.cfg_renewal_rules", "EFFECTIVE", "RR-GMOT", ("product_code", Product), ("notice_days", 31), ("auto_renew", true), ("re_rating", true),
            ("invitation_template", "TPL-RENEWAL_NOTICE"), ("reminder_days", new[] { 10 }), ("review", true));
        await Seed("general.cfg_renewal_review_rules", "EFFECTIVE", "RV-GMOT", ("product_code", Product), ("underwriting_rules", true), ("claims_lookback_years", 3),
            ("review_days_before_invitation", 14));
        await Seed("general.cfg_claims_experience_factors", "EFFECTIVE", "CF-GMOT-1", ("product_code", Product), ("claims_count_from", 1), ("factor", 1.2m));
        await Seed("general.ref_underwriting_decision_codes", "ACTIVE", "ACCEPT", ("label", "Accepted"), ("requires_terms", false));
        await Seed("rules.cfg_acceptance_and_referral_rules", "EFFECTIVE", "UW-REN-CLAIMS", ("rule_code", "UW-REN-CLAIMS"), ("condition", "claims_count >= 1"),
            ("threshold", 0m), ("outcome", "REFER"));

        async Task<JsonElement> Get(string url) => await (await _maker.GetAsync(url)).Json();
        var (policy, vehicle) = await IssuedPolicy();
        var holder = await _maker.RegisterPerson("Renewing");
        await _db.AddContact(Tenant, holder, "EMAIL", "renewing@example.com");
        await _db.ExecuteAsOwner("UPDATE general.policy SET policyholder_party_id = @h, period_start = @s, period_end = @e WHERE id = @p", ("h", holder),
            ("s", Today.AddYears(-1).AddDays(21)), ("e", Today.AddDays(20)), ("p", policy));
        var claim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC", description = "One claim",
        });
        (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // The review: claims history, re-rating through Rating, the factor, and the underwriting rules' referral.
        var jobs = _factory.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var renewal = (await Get($"/api/v1/general/renewals?policyId={policy}")).GetProperty("items").EnumerateArray().Single();
        var id = renewal.GetProperty("id").GetGuid();
        ShouldMatchDocumented(renewal, "GeneralGetRenewal");
        (renewal.GetProperty("status").GetString(), renewal.GetProperty("claimsCount").GetInt32(), renewal.GetProperty("factor").GetDecimal(),
            renewal.GetProperty("ratedPremium").GetDecimal()).ShouldBe(("Referred", 1, 1.2m, 3_050m), renewal.ToString());
        renewal.GetProperty("checks").EnumerateArray().Select(c => $"{c.GetProperty("checkCode").GetString()}:{c.GetProperty("outcome").GetString()}")
            .ShouldContain("UNDERWRITING_RULES:REFER");
        renewal.GetProperty("quoteId").GetGuid().ShouldNotBe(Guid.Empty);
        (await _db.ScalarAsOwner<string>("""
            SELECT CASE WHEN p.ended_at IS NULL THEN 'OPEN' ELSE 'ENDED' END || ':' || t.queue_code
            FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
            WHERE p.process_code = 'GENERAL_RENEWAL_REVIEW' AND p.business_ref_id = @r
            """, ("r", id))).ShouldStartWith("OPEN:", customMessage: "an underwriter's task, routed by authority");

        // F019: the underwriter's authority (50 000) does not cover the 100 000 sum insured; the senior underwriter's does.
        var version = $"\"{renewal.GetProperty("rowVersion").GetInt64()}\"";
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/general/renewals/{id}/decision", new { offer = true, decisionCode = "ACCEPT", loadingPct = 10m },
            ifMatch: version)).Code()).ShouldBe(Insys.General.Domain.GeneralErrorCodes.UnderwritingAuthorityExceeded);
        var decided = await _checker.Send(HttpMethod.Post, $"/api/v1/general/renewals/{id}/decision", new { offer = true, decisionCode = "ACCEPT", loadingPct = 10m },
            ifMatch: version);
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, await decided.Content.ReadAsStringAsync());
        var offered = await decided.Json();
        (offered.GetProperty("status").GetString(), offered.GetProperty("offeredPremium").GetDecimal(), offered.GetProperty("changedTerms").GetBoolean())
            .ShouldBe(("Offered", 4_026m, true), "3 050 re-rated, times 1.2 for the claim, plus a 10 % loading");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM workflow.process_instance WHERE process_code = 'GENERAL_RENEWAL_REVIEW' AND business_ref_id = @r AND ended_at IS NULL",
            ("r", id))).ShouldBe(0, "the task closed with the decision");

        // The invitation states the offered premium; changed terms are invited at least the tenant's 31 days before (here at once).
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        await _factory.Services.GetRequiredService<Insys.Communications.Infrastructure.NotificationSender>().RunForTenant(Tenant, CancellationToken.None);
        var number = (await Policy(policy)).GetProperty("policyNumber").GetString();
        (await _db.ScalarAsOwner<string>("SELECT status || ':' || body FROM communication.notification WHERE recipient_party_id = @h AND type_code = 'RENEWAL_INVITATION'",
            ("h", holder))).ShouldBe($"SENT:{number} renews at 4026.00 ZAR.");

        // OQ-130: the offer is 32 % above the current premium, more than the tenant's 15 %: it renews only once the customer accepts.
        var answer = (await Get($"/api/v1/general/renewals/{id}")).GetProperty("answer");
        (answer.GetProperty("acceptance").GetString(), answer.GetProperty("needsAcceptance").GetBoolean()).ShouldBe(("AUTOMATIC", true));
        var accepted = await _maker.Send(HttpMethod.Post, $"/api/v1/general/renewals/{id}/accept",
            ifMatch: $"\"{(await Get($"/api/v1/general/renewals/{id}")).GetProperty("rowVersion").GetInt64()}\"");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());

        // At the period's end it renews on the offered terms.
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @e WHERE id = @p", ("e", Today.AddDays(-1)), ("p", policy));
        await _db.ExecuteAsOwner("UPDATE general.renewal SET period_end = @e WHERE id = @r", ("e", Today.AddDays(-1)), ("r", id));
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var renewed = await _db.ScalarAsOwner<Guid>("SELECT id FROM general.policy WHERE renewal_of_id = @p", ("p", policy));
        (await Policy(renewed)).GetProperty("annualPremium").GetDecimal().ShouldBe(4_026m);
        var done = await Get($"/api/v1/general/renewals/{id}");
        (done.GetProperty("status").GetString(), done.GetProperty("answer").GetProperty("acceptedChannel").GetString(),
            done.GetProperty("answer").GetProperty("renewalPolicyId").GetGuid()).ShouldBe(("Renewed", "STAFF", renewed));
    }

    /// <summary>
    /// Wave 4 §4.4, §4.6 (F022, OQ-130): a product renewing ON_ACCEPTANCE is invited with an offer that waits for the customer.
    /// The member accepts through the portal and the policy renews at its period end; another member's renewal, not accepted,
    /// lapses and the policy expires (LAPSED). A member sees only their own renewal.
    /// </summary>
    [Fact]
    public async Task ARenewalOnAcceptance_RenewsOnceTheMemberAcceptsThroughThePortal_AndOneNotAcceptedLapses_F022_OQ130()
    {
        await Seed("general.cfg_renewal_rules", "EFFECTIVE", "RR-GMOT", ("product_code", Product), ("notice_days", 31), ("auto_renew", true), ("re_rating", false),
            ("invitation_template", "TPL-RENEWAL_NOTICE"), ("acceptance", "ON_ACCEPTANCE"));
        var (accepting, _) = await IssuedPolicy();
        var (lapsing, _) = await IssuedPolicy();
        var holder = (await Policy(accepting)).GetProperty("policyholderPartyId").GetGuid();
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_start = @s, period_end = @e WHERE id IN (@a, @b)", ("s", Today.AddYears(-1).AddDays(21)),
            ("e", Today.AddDays(20)), ("a", accepting), ("b", lapsing));
        var jobs = _factory.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        async Task<JsonElement> RenewalOf(Guid policy) =>
            (await (await _maker.GetAsync($"/api/v1/general/renewals?policyId={policy}")).Json()).GetProperty("items").EnumerateArray().Single();
        var invited = await RenewalOf(accepting);
        (invited.GetProperty("status").GetString(), invited.GetProperty("answer").GetProperty("acceptance").GetString(),
            invited.GetProperty("answer").GetProperty("needsAcceptance").GetBoolean()).ShouldBe(("Offered", "ON_ACCEPTANCE", true));

        // The member's portal: their renewal only, with its terms; they accept it.
        var memberRole = await PortalUser("member-renewal", "PARTY", holder, null, GeneralPermissions.PolicyRead, GeneralPermissions.RenewalRespond);
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-MEM", ("channel_code", "MEM"), ("audience", "CUSTOMER"), ("name", "Member portal"),
            ("client_ids", new[] { "member-portal" }));
        await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-MEM-R", ("channel_code", "MEM"), ("role_code", memberRole),
            ("transaction_type", GeneralPermissions.RenewalRespond), ("allowed", true));
        await _db.AdoptFunctionCatalogue();
        var member = _factory.ClientFor(Tenant, "member-renewal", "member-portal");
        var listed = (await (await member.GetAsync("/api/v1/portal/general/renewals")).Json()).GetProperty("items").EnumerateArray().ToList();
        listed.Select(r => r.GetProperty("policyId").GetGuid()).ShouldBe([accepting]);
        (listed[0].GetProperty("status").GetString(), listed[0].GetProperty("needsAcceptance").GetBoolean()).ShouldBe(("OFFERED", true));
        listed[0].TryGetProperty("factor", out _).ShouldBeFalse("the review is not shown");
        var other = await RenewalOf(lapsing);
        (await member.Send(HttpMethod.Post, $"/api/v1/portal/general/renewals/{other.GetProperty("id").GetGuid()}/accept",
            ifMatch: $"\"{other.GetProperty("rowVersion").GetInt64()}\"")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "another member's renewal");
        var accepted = await member.Send(HttpMethod.Post, $"/api/v1/portal/general/renewals/{listed[0].GetProperty("id").GetGuid()}/accept",
            ifMatch: $"\"{invited.GetProperty("rowVersion").GetInt64()}\"");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await accepted.Json()).GetProperty("status").GetString().ShouldBe("ACCEPTED");

        // The period ends: the accepted one renews; the other lapses, and its policy expires as LAPSED.
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @e WHERE id IN (@a, @b)", ("e", Today.AddDays(-1)), ("a", accepting), ("b", lapsing));
        await _db.ExecuteAsOwner("UPDATE general.renewal SET period_end = @e WHERE policy_id IN (@a, @b)", ("e", Today.AddDays(-1)), ("a", accepting), ("b", lapsing));
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var renewedPolicy = await _db.ScalarAsOwner<Guid>("SELECT id FROM general.policy WHERE renewal_of_id = @p", ("p", accepting));
        (await RenewalOf(accepting)).GetProperty("answer").ShouldSatisfyAllConditions(a => a.GetProperty("acceptedChannel").GetString().ShouldBe("PORTAL"),
            a => a.GetProperty("renewalPolicyId").GetGuid().ShouldBe(renewedPolicy));
        (await RenewalOf(lapsing)).GetProperty("status").GetString().ShouldBe("Lapsed");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM general.policy WHERE renewal_of_id = @p", ("p", lapsing))).ShouldBe(0);
        (await _db.ScalarAsOwner<string>("SELECT status || ':' || expiry_reason FROM general.policy WHERE id = @p", ("p", lapsing))).ShouldBe("EXPIRED:LAPSED");
    }

    /// <summary>
    /// Wave 4 §4.3 (F025, OQ-132): an insurer's non-renewal. A reason needing approval waits for a second person; approved,
    /// the policyholder gets the notice with the reason's wording, and the policy expires at its period end, not renewed by
    /// the insurer. A decision too late for its notice extends cover, pro rata, until the notice runs out, and the extra days
    /// are billed.
    /// </summary>
    [Fact]
    public async Task AnInsurersNonRenewal_IsApprovedAndNoticed_AndALateOneExtendsCoverProRata_F025_OQ132()
    {
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "NRN",
            (Insys.Communications.Contracts.NotificationTypes.NonRenewalNotice, "Not renewing {policy_number}",
                "{policy_number} will not be renewed: {reason} Cover ends on {cover_ends_on}."));
        await Seed("general.cfg_non_renewal_reasons", "EFFECTIVE", "NR-CLAIMS", ("reason_code", "CLAIMS"), ("notice_days", 31),
            ("customer_wording", "Its claims experience is outside our acceptance."), ("requires_approval", true));
        await Seed("general.cfg_non_renewal_reasons", "EFFECTIVE", "NR-EXIT", ("reason_code", "PRODUCT_EXIT"), ("notice_days", 31),
            ("customer_wording", "We no longer offer this cover."), ("requires_approval", false));
        var (approved, _) = await IssuedPolicy();
        var (late, _) = await IssuedPolicy();
        var holder = (await Policy(approved)).GetProperty("policyholderPartyId").GetGuid();
        await _db.AddContact(Tenant, holder, "EMAIL", "notice@example.com");
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @e WHERE id = @p", ("e", Today.AddDays(60)), ("p", approved));
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @e WHERE id = @p", ("e", Today.AddDays(10)), ("p", late));

        // Needing approval: proposed by one person, approved by another; then the notice goes.
        var proposed = await (await _maker.Send(HttpMethod.Post, $"/api/v1/general/policies/{approved}/non-renewal", new { reasonCode = "CLAIMS" }, idempotent: true)).Json();
        proposed.GetProperty("nonRenewal").GetProperty("status").GetString().ShouldBe("REQUESTED");
        var renewalId = proposed.GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.NonRenewalBusinessRefType, renewalId, approve: true);
        await _factory.DrainOutbox();
        var decided = await (await _maker.GetAsync($"/api/v1/general/renewals/{renewalId}")).Json();
        (decided.GetProperty("status").GetString(), decided.GetProperty("nonRenewal").GetProperty("status").GetString(),
            decided.GetProperty("nonRenewal").GetProperty("coverExtendedTo").ValueKind).ShouldBe(("NotRenewedByInsurer", "DECIDED", JsonValueKind.Null));
        await _factory.Services.GetRequiredService<Insys.Communications.Infrastructure.NotificationSender>().RunForTenant(Tenant, CancellationToken.None);
        var number = (await Policy(approved)).GetProperty("policyNumber").GetString();
        (await _db.ScalarAsOwner<string>("SELECT status || ':' || body FROM communication.notification WHERE recipient_party_id = @h AND type_code = 'NON_RENEWAL_NOTICE'",
            ("h", holder))).ShouldBe($"SENT:{number} will not be renewed: Its claims experience is outside our acceptance. Cover ends on {Today.AddDays(60):yyyy-MM-dd}.");

        // Too late for its 31 days' notice (10 days left): decided at once, cover runs to the notice's end, and the extra 21 days are billed.
        var annual = (await Policy(late)).GetProperty("annualPremium").GetDecimal();
        var extended = await (await _maker.Send(HttpMethod.Post, $"/api/v1/general/policies/{late}/non-renewal", new { reasonCode = "PRODUCT_EXIT" }, idempotent: true)).Json();
        extended.GetProperty("nonRenewal").GetProperty("coverExtendedTo").GetString().ShouldBe(Today.AddDays(31).ToString("yyyy-MM-dd"));
        await _factory.DrainOutbox();
        (await Policy(late)).GetProperty("periodEnd").GetString().ShouldBe(Today.AddDays(31).ToString("yyyy-MM-dd"));
        (await _db.ScalarAsOwner<decimal>("""
            SELECT i.amount FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.id = i.schedule_id WHERE s.policy_ref = @p AND i.due_date = @d
            """, ("p", late), ("d", Today.AddDays(11)))).ShouldBe(Math.Round(annual * 21 / 365m, 2, MidpointRounding.AwayFromZero));

        // At the period's end the approved one expires, not renewed by the insurer.
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @e WHERE id = @p", ("e", Today.AddDays(-1)), ("p", approved));
        await _factory.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("SELECT status || ':' || expiry_reason FROM general.policy WHERE id = @p", ("p", approved))).ShouldBe("EXPIRED:NOT_RENEWED_BY_INSURER");
    }

    /// <summary>Wave 4 §4.4 (F022, OQ-130): an ON_PAYMENT renewal renews at the period end, and its first premium paid is the customer's acceptance.</summary>
    [Fact]
    public async Task AnOnPaymentRenewal_RenewsAndIsAcceptedByItsFirstPremium_F022()
    {
        await Seed("general.cfg_renewal_rules", "EFFECTIVE", "RR-GMOT", ("product_code", Product), ("notice_days", 31), ("auto_renew", true), ("re_rating", false),
            ("invitation_template", "TPL-RENEWAL_NOTICE"), ("acceptance", "ON_PAYMENT"));
        var (policy, _) = await IssuedPolicy();
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_start = @s, period_end = @e WHERE id = @p", ("s", Today.AddYears(-1).AddDays(21)),
            ("e", Today.AddDays(20)), ("p", policy));
        var jobs = _factory.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @e WHERE id = @p", ("e", Today.AddDays(-1)), ("p", policy));
        await _db.ExecuteAsOwner("UPDATE general.renewal SET period_end = @e WHERE policy_id = @p", ("e", Today.AddDays(-1)), ("p", policy));
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var renewed = await _db.ScalarAsOwner<Guid>("SELECT id FROM general.policy WHERE renewal_of_id = @p", ("p", policy));
        var renewal = await (await _maker.GetAsync($"/api/v1/general/renewals?policyId={policy}")).Json();
        var answer = renewal.GetProperty("items")[0].GetProperty("answer");
        (answer.GetProperty("acceptance").GetString(), answer.GetProperty("acceptedChannel").ValueKind).ShouldBe(("ON_PAYMENT", JsonValueKind.Null));

        foreach (var payment in await _db.ScalarAsOwner<Guid[]>("""
                     SELECT array_agg(p.id) FROM payment.payment p JOIN billing.invoice i ON i.id = p.business_ref_id WHERE i.policy_ref = @p
                     """, ("p", renewed)) ?? [])
        {
            (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _factory.DrainOutbox();
        renewal = await (await _maker.GetAsync($"/api/v1/general/renewals?policyId={policy}")).Json();
        renewal.GetProperty("items")[0].GetProperty("answer").GetProperty("acceptedChannel").GetString().ShouldBe("PAYMENT");
    }

    /// <summary>
    /// External-user access (addenda/external-user-access.md): brokers and members act only through their portal, on the
    /// records of their relationship. A broker cannot see another broker's client, nor quote for one; a member cannot open
    /// another member's policy, even by guessing its ID. Staff endpoints, the staff client and a token without MFA are refused.
    /// A portal registration matching an existing party gets the same answer as a new one, and a staff review task.
    /// </summary>
    [Fact]
    public async Task ExternalUsers_SeeAndActOnlyWithinTheirRelationship_ThroughTheirPortal_ExternalUserAccess()
    {
        // Staff set up two policies, one sold by each broker, and a claim on B's, before the tenant has channels.
        var (policyA, vehicleA) = await IssuedPolicy();
        var (policyB, vehicleB) = await IssuedPolicy();
        var holderA = (await Policy(policyA)).GetProperty("policyholderPartyId").GetGuid();
        var holderB = (await Policy(policyB)).GetProperty("policyholderPartyId").GetGuid();
        await _db.ExecuteAsOwner("UPDATE general.policy SET intermediary_code = CASE WHEN id = @a THEN 'BRK-A' ELSE 'BRK-B' END WHERE id IN (@a, @b)",
            ("a", policyA), ("b", policyB));
        var claimB = await Created("/api/v1/general/claims", new
        {
            policyId = policyB, riskObjectId = vehicleB, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC",
        });
        var known = await _maker.RegisterPerson("Known");
        var owned = new Dictionary<Guid, Guid>();
        foreach (var owner in new[] { holderA, holderB })
        {
            owned[owner] = await Vehicle();
            await _db.ExecuteAsOwner("UPDATE general.risk_object SET owner_party_id = @o WHERE id = @r", ("o", owner), ("r", owned[owner]));
        }

        foreach (var code in new[] { "BRK-A", "BRK-B" })
        {
            await Seed("commission.mst_intermediary_register", "ACTIVE", code, ("intermediary_code", code), ("type", "BROKER"), ("legal_name", code),
                ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", "ACTIVE"), ("hierarchy_node", $"NODE-{code}"));
            await Seed("commission.cfg_intermediary_product_permissions", "EFFECTIVE", $"PERM-{code}", ("intermediary", code), ("product", Product),
                ("transactions_allowed", Array.Empty<string>()), ("plan_code", "PLAN-10"));
        }

        string[] brokerPermissions = [GeneralPermissions.PolicyRead, GeneralPermissions.QuoteRead, GeneralPermissions.QuoteCreate, GeneralPermissions.ClaimRead,
            PartyPermissions.PartyCreate, BillingPermissions.InvoiceRead, DocumentPermissions.DocumentRead];
        var brokerRole = await PortalUser("broker-a", "INTERMEDIARY", null, "BRK-A", brokerPermissions);
        await PortalUser("broker-b", "INTERMEDIARY", null, "BRK-B", brokerPermissions);
        var memberRole = await PortalUser("member-a", "PARTY", holderA, null, GeneralPermissions.PolicyRead, GeneralPermissions.ClaimRead, GeneralPermissions.ClaimCreate,
            BillingPermissions.InvoiceRead, DocumentPermissions.DocumentRead);

        // The portals' channels (F005), with the commands their roles may make; and the back office's.
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-BRK", ("channel_code", "BRK"), ("audience", "BROKER"), ("name", "Broker portal"),
            ("client_ids", new[] { "broker-portal" }));
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-MEM", ("channel_code", "MEM"), ("audience", "CUSTOMER"), ("name", "Member portal"),
            ("client_ids", new[] { "member-portal" }));
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-STF", ("channel_code", "STF"), ("audience", "STAFF"), ("name", "Back office"),
            ("client_ids", new[] { "insys-web" }));
        await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-BRK-Q", ("channel_code", "BRK"), ("role_code", brokerRole),
            ("transaction_type", GeneralPermissions.QuoteCreate), ("allowed", true));
        await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-MEM-C", ("channel_code", "MEM"), ("role_code", memberRole),
            ("transaction_type", GeneralPermissions.ClaimCreate), ("allowed", true));
        await _db.AdoptFunctionCatalogue();

        var brokerA = _factory.ClientFor(Tenant, "broker-a", "broker-portal", multiFactor: true);
        var brokerB = _factory.ClientFor(Tenant, "broker-b", "broker-portal", multiFactor: true);
        var member = _factory.ClientFor(Tenant, "member-a", "member-portal");

        // A broker sees its own client's policy, not the other broker's, by list or by ID; nor a claim on it.
        var brokerList = await brokerA.GetAsync("/api/v1/portal/general/policies");
        brokerList.StatusCode.ShouldBe(HttpStatusCode.OK, await brokerList.Content.ReadAsStringAsync());
        (await brokerList.Json()).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid())
            .ShouldBe([policyA]);
        (await brokerA.GetAsync($"/api/v1/portal/general/policies/{policyB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await brokerA.GetAsync($"/api/v1/portal/general/claims/{claimB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await brokerB.GetAsync($"/api/v1/portal/general/claims/{claimB}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The portal view leaves out internal fields.
        var view = await (await brokerA.GetAsync($"/api/v1/portal/general/policies/{policyA}")).Json();
        view.TryGetProperty("billingAccountRef", out _).ShouldBeFalse();
        view.TryGetProperty("cancellations", out _).ShouldBeFalse();

        // A broker quotes only for a client it services, under its own code whatever it sends.
        // BR-092: a broker quotes only on its client's own items, so each proposer here owns the vehicle quoted on.
        async Task<object> Quote(Guid proposer)
        {
            if (!owned.TryGetValue(proposer, out var vehicle))
            {
                // Staff are off the channels here, so the client's vehicle goes straight in.
                vehicle = owned[proposer] = Guid.CreateVersion7();
                await _db.ExecuteAsOwner("""
                    INSERT INTO general.risk_object (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, object_type, attributes,
                                                     attribute_schema_version, status, owner_party_id)
                    VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', 'VEHICLE', '{"registration":"CA1","vehicle_group":"G3","year":2024}', 1, 'ACTIVE', @o)
                    """, ("t", Tenant), ("id", vehicle), ("o", proposer));
            }

            return new
            {
                productCode = Product, proposerPartyId = proposer, currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1),
                risks = new[] { new { riskObjectId = vehicle, sumInsured = 100_000m, coverages = new object[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m, excessScheduleCode = "MOT-EXC" } } } },
            };
        }
        var otherClient = await brokerA.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", await Quote(holderB), idempotent: true);
        otherClient.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await otherClient.Code()).ShouldBe("OUT_OF_SCOPE");
        var quoted = await brokerA.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", await Quote(holderA), idempotent: true);
        quoted.StatusCode.ShouldBe(HttpStatusCode.Created, await quoted.Content.ReadAsStringAsync());
        (await quoted.Json()).GetProperty("intermediaryCode").GetString().ShouldBe("BRK-A");

        // A member opens their own policy, not another's, even by guessing its ID, and notifies a claim only on their own.
        (await member.GetAsync($"/api/v1/portal/general/policies/{policyA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await member.GetAsync($"/api/v1/portal/general/policies/{policyB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await member.GetAsync($"/api/v1/portal/general/policies/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        object Loss(Guid policy, Guid vehicle) => new { policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC" };
        (await member.Send(HttpMethod.Post, "/api/v1/portal/general/claims", Loss(policyB, vehicleB), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var notified = await member.Send(HttpMethod.Post, "/api/v1/portal/general/claims", Loss(policyA, vehicleA), idempotent: true);
        notified.StatusCode.ShouldBe(HttpStatusCode.Created, await notified.Content.ReadAsStringAsync());

        // Invoices: the member's are those of the accounts they pay; a broker's, those of its policies.
        async Task<Guid[]> InvoicePolicies(HttpClient client)
        {
            var listed = await client.GetAsync("/api/v1/portal/billing/invoices");
            listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.Content.ReadAsStringAsync());
            return (await listed.Json()).EnumerateArray().Select(i => i.GetProperty("policyRef").GetGuid()).Distinct().ToArray();
        }

        await _factory.DrainOutbox();
        (await InvoicePolicies(member)).ShouldBe([policyA]);
        (await InvoicePolicies(brokerA)).ShouldBe([policyA]);
        (await InvoicePolicies(brokerB)).ShouldBe([policyB]);
        var invoiceB = await _db.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p LIMIT 1", ("p", policyB));
        (await member.GetAsync($"/api/v1/portal/billing/invoices/{invoiceB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var invoiceView = await (await brokerB.GetAsync($"/api/v1/portal/billing/invoices/{invoiceB}")).Json();
        invoiceView.TryGetProperty("allocatedAmount", out _).ShouldBeFalse();

        // Documents: only categories that name a portal role (the policy schedule's does not), and only the relationship's.
        string Documents(Guid policy) => $"/api/v1/portal/documents?businessRefType=GENERAL_POLICY&businessRefId={policy}";
        await Seed("document.ref_document_categories", "ACTIVE", "PORTAL_LETTER", ("category_code", "PORTAL_LETTER"), ("label", "Letter"), ("classification", "PERSONAL"),
            ("domain", "ALL"), ("allowed_roles", new[] { memberRole, brokerRole }));
        foreach (var category in new[] { "PORTAL_LETTER", "POLICY_SCHEDULE" })
        {
            var stored = await _maker.Send(HttpMethod.Post, "/api/v1/documents/documents", new
            {
                categoryCode = category, businessRefType = "GENERAL_POLICY", businessRefId = policyA, mimeType = "text/plain",
                contentBase64 = Convert.ToBase64String("Dear policyholder"u8.ToArray()),
            }, idempotent: true);
            stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        }

        var schedule = (await (await member.GetAsync(Documents(policyA))).Json()).EnumerateArray().Single();
        schedule.GetProperty("categoryCode").GetString().ShouldBe("PORTAL_LETTER");
        (await member.GetAsync(Documents(policyB))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var scheduleId = schedule.GetProperty("id").GetGuid();
        (await member.GetAsync($"/api/v1/portal/documents/{scheduleId}/content")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _db.ScalarAsOwner<string>("SELECT purpose FROM document.document_access_log WHERE document_id = @d", ("d", scheduleId))).ShouldBe("PORTAL");
        (await brokerA.GetAsync($"/api/v1/portal/documents/{scheduleId}/content")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await brokerB.GetAsync($"/api/v1/portal/documents/{scheduleId}/content")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // One audit entry per read; the other broker's refused read is DENIED (DOC-003).
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(concat_ws('|', outcome, coalesce(details->>'refused', '-')), ',' ORDER BY occurred_at)
            FROM audit.audit_event WHERE tenant_id = @t AND operation = 'ReadDocument' AND target_id = @d
            """, ("t", Tenant), ("d", scheduleId.ToString()))).ShouldBe("SUCCESS|-,SUCCESS|-,DENIED|NOT_FOUND");

        // Outside the portal: a staff endpoint, the staff client, and a broker token without MFA are refused.
        (await (await brokerA.GetAsync($"/api/v1/general/policies/{policyA}")).Code()).ShouldBe("EXTERNAL_USER_NOT_ALLOWED");
        (await (await _factory.ClientFor(Tenant, "broker-a", "insys-web", multiFactor: true).GetAsync("/api/v1/portal/general/policies")).Code()).ShouldBe("CHANNEL_NOT_ENABLED");
        (await (await _factory.ClientFor(Tenant, "broker-a", "broker-portal").GetAsync("/api/v1/portal/general/policies")).Code()).ShouldBe("PORTAL_MFA_REQUIRED");

        // A portal registration that matches an existing party gets the same answer as a new one, and staff review it.
        async Task<JsonElement> Register(string surname)
        {
            var registered = await brokerA.Send(HttpMethod.Post, "/api/v1/portal/party/parties",
                new { individual = new { firstNames = "Test", surname, birthDate = "1985-06-15", nationality = "ZA" } }, idempotent: true);
            registered.StatusCode.ShouldBe(HttpStatusCode.Accepted, await registered.Content.ReadAsStringAsync());
            return await registered.Json();
        }

        var match = await Register("Known");
        var fresh = await Register("Newcomer");
        (match.GetProperty("status").GetString(), fresh.GetProperty("status").GetString()).ShouldBe(("Received", "Received"));
        match.EnumerateObject().Select(p => p.Name).ShouldBe(fresh.EnumerateObject().Select(p => p.Name));
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM workflow.process_instance WHERE process_code = 'PARTY_MATCH_REVIEW' AND business_ref_id = @p",
            ("p", match.GetProperty("id").GetGuid()))).ShouldBe(1L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM workflow.process_instance WHERE process_code = 'PARTY_MATCH_REVIEW' AND business_ref_id = @p",
            ("p", fresh.GetProperty("id").GetGuid()))).ShouldBe(0L);
        known.ShouldNotBe(match.GetProperty("id").GetGuid());

        // The broker may now quote for the client it registered.
        (await brokerA.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", await Quote(fresh.GetProperty("id").GetGuid()), idempotent: true)).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        async Task<JsonElement> Get(string url)
        {
            var response = await _maker.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await response.Json();
        }

        // Staff resolve the match (§6): a LINK waits for a second person; approved, the registration is merged into the
        // existing party and the review task closes; the broker's quote for its registration is then made for that party.
        var registration = match.GetProperty("id").GetGuid();
        var reviewed = await Get($"/api/v1/party/parties/{registration}");
        (reviewed.GetProperty("review").GetProperty("status").GetString(), reviewed.GetProperty("review").GetProperty("matchedPartyId").GetGuid())
            .ShouldBe(("PENDING_REVIEW", known));
        var etag = $"\"{reviewed.GetProperty("rowVersion").GetInt64()}\"";
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/party/parties/{registration}/match-resolution", new { resolution = "LINK", existingPartyId = fresh.GetProperty("id").GetGuid() },
            ifMatch: etag)).Code()).ShouldBe("VALIDATION_FAILED", "a link is to the party it matched");
        (await _maker.Send(HttpMethod.Post, $"/api/v1/party/parties/{registration}/match-resolution", new { resolution = "LINK" }, ifMatch: etag)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await Get($"/api/v1/party/parties/{registration}")).GetProperty("review").GetProperty("status").GetString().ShouldBe("PROPOSED");
        var approval = (await Get($"/api/v1/workflow/approvals?status=Requested&businessRefType={PartyApprovals.MatchBusinessRefType}&businessRefId={registration}"))
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var linked = await Get($"/api/v1/party/parties/{registration}");
        (linked.GetProperty("status").GetString(), linked.GetProperty("mergedIntoId").GetGuid(), linked.GetProperty("review").GetProperty("status").GetString())
            .ShouldBe(("Merged", known, "LINKED"));
        (await _db.ScalarAsOwner<string>("SELECT state FROM workflow.process_instance WHERE process_code = 'PARTY_MATCH_REVIEW' AND business_ref_id = @p AND ended_at IS NOT NULL",
            ("p", registration))).ShouldBe("COMPLETED:REVIEWED");
        var forKnown = await brokerA.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", await Quote(registration), idempotent: true);
        forKnown.StatusCode.ShouldBe(HttpStatusCode.Created, await forKnown.Content.ReadAsStringAsync());
        (await forKnown.Json()).GetProperty("proposerPartyId").GetGuid().ShouldBe(known);
    }

    /// <summary>
    /// BR-095 (decided by Ade, 28 October 2026): a broker's portal quote is priced as it is made, and so is every later
    /// version of it. General asks Rating; the quote reads Rated with Rating's premium. A quote whose risk object lacks a
    /// rating input stays Draft with the reason; one over a zone limit is referred by the rules and shows the broker no
    /// price until an underwriter approves it. The broker has no way to set the price, and staff quotes are rated as before.
    /// </summary>
    [Fact]
    public async Task ABrokersPortalQuote_IsPricedAsItIsMade_OrSaysWhyNot_BR095()
    {
        var broker = await Broker("BRK-A");
        var client = await _maker.RegisterPerson("Client");
        await IssuedPolicy(proposer: client, intermediaryCode: "BRK-A");

        async Task<HttpResponseMessage> Quote(Guid riskObject, string? frequency)
        {
            // The item is the client's (BR-092).
            await _db.ExecuteAsOwner("UPDATE general.risk_object SET owner_party_id = @o WHERE id = @r", ("o", client), ("r", riskObject));
            return await broker.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", new
            {
                productCode = Product, proposerPartyId = client, currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1), frequency,
                risks = new[] { new { riskObjectId = riskObject, sumInsured = 100_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m } } } },
            }, idempotent: true);
        }

        async Task<JsonElement> PortalQuote(Guid riskObject, string? frequency = null)
        {
            var quoted = await Quote(riskObject, frequency);
            quoted.StatusCode.ShouldBe(HttpStatusCode.Created, await quoted.Content.ReadAsStringAsync());
            var made = await quoted.Json();
            (made.GetProperty("status").GetString(), made.GetProperty("premium").ValueKind).ShouldBe(("Draft", JsonValueKind.Null), "the price arrives after the quote");
            made.GetProperty("pricedOnCreation").GetBoolean().ShouldBeTrue("BR-097: the broker's screen shows it is being priced");
            await _factory.DrainOutbox();
            return await BrokersQuote(made.GetProperty("id").GetGuid());
        }

        async Task<JsonElement> BrokersQuote(Guid quote)
        {
            var read = await broker.GetAsync($"/api/v1/portal/general/quotes/{quote}");
            read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
            return await read.Json();
        }

        // Priced: Rating's premium for the vehicle's group and the sum insured (100 000 × 0.03 + 50), recorded as a calculation.
        var priced = await PortalQuote(await Vehicle());
        var quoteId = priced.GetProperty("id").GetGuid();
        (priced.GetProperty("status").GetString(), priced.GetProperty("premium").GetDecimal(), priced.GetProperty("ratingIssue").ValueKind)
            .ShouldBe(("Rated", 3_050m, JsonValueKind.Null));
        var staffView = await (await _maker.GetAsync($"/api/v1/general/quotes/{quoteId}")).Json();
        staffView.GetProperty("pricedOnCreation").GetBoolean().ShouldBeTrue();
        var calculation = await (await _maker.GetAsync($"/api/v1/rating/calculations/{staffView.GetProperty("ratingRequestId").GetGuid()}")).Json();
        (calculation.GetProperty("subjectId").GetGuid(), calculation.GetProperty("requestRef").GetString(), calculation.GetProperty("premium").GetDecimal())
            .ShouldBe((quoteId, $"{priced.GetProperty("quoteNumber").GetString()}-V1", 3_050m));
        calculation.GetProperty("inputs").GetProperty("values").GetProperty("vehicle_group").GetString().ShouldBe("G3");

        // Priced in the frequency the broker chose when the product allows it: monthly here, shown beside the annual premium,
        // as Billing will charge it (3 050 / 12). A frequency the product does not allow is shown as annual; an unknown one is refused.
        await Seed("billing.ref_billing_frequencies", "ACTIVE", "QUARTERLY", ("label", "Quarterly"), ("periods_per_year", 4));
        await Seed("general.cfg_product_billing_frequencies", "EFFECTIVE", $"{Product}-MONTHLY", ("product_code", Product), ("frequency", "MONTHLY"));
        var monthly = await PortalQuote(await Vehicle(), "MONTHLY");
        (monthly.GetProperty("premium").GetDecimal(), monthly.GetProperty("premiumFrequency").GetString(), monthly.GetProperty("instalmentPremium").GetDecimal())
            .ShouldBe((3_050m, "MONTHLY", 254.17m));
        var quarterly = await PortalQuote(await Vehicle(), "QUARTERLY");
        (quarterly.GetProperty("premium").GetDecimal(), quarterly.GetProperty("premiumFrequency").GetString(), quarterly.GetProperty("instalmentPremium").GetDecimal())
            .ShouldBe((3_050m, "ANNUAL", 3_050m));
        (priced.GetProperty("premiumFrequency").GetString(), priced.GetProperty("instalmentPremium").GetDecimal()).ShouldBe(("ANNUAL", 3_050m), "none chosen");

        // An instalment loading for a frequency the product allows: quarterly at 4%, 3 050 / 4 × 1.04.
        await Seed("general.cfg_product_billing_frequencies", "EFFECTIVE", $"{Product}-QUARTERLY", ("product_code", Product), ("frequency", "QUARTERLY"),
            ("instalment_loading_pct", 4m));
        var loaded = await PortalQuote(await Vehicle(), "QUARTERLY");
        (loaded.GetProperty("premium").GetDecimal(), loaded.GetProperty("premiumFrequency").GetString(), loaded.GetProperty("instalmentPremium").GetDecimal())
            .ShouldBe((3_050m, "QUARTERLY", 793.00m));
        (await (await Quote(await Vehicle(), "WEEKLY")).Code()).ShouldBe("GENERAL_CODE_NOT_IN_FORCE");

        // The broker has no way to price a quote: Rating's calculation and the staff quote operations are not open to it.
        (await broker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = "BRK-PRICE", sourceDomain = GeneralRatingSubjects.SourceDomain, subjectType = GeneralRatingSubjects.Quote, subjectId = quoteId, productCode = Product,
            productVersion = 1, currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = 1m, vehicle_group = "G3" },
        }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await broker.PostAsync($"/api/v1/general/quotes/{quoteId}/issue", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BrokersQuote(quoteId)).GetProperty("premium").GetDecimal().ShouldBe(3_050m);

        // A later version, made by staff, is priced as it is made too, with nobody asking.
        var revised = await Created("/api/v1/general/quotes", new
        {
            productCode = Product, proposerPartyId = client, currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1), fromQuoteId = quoteId,
            intermediaryCode = "BRK-A",
            risks = new[] { new { riskObjectId = priced.GetProperty("risks")[0].GetProperty("riskObjectId").GetGuid(), sumInsured = 200_000m,
                coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 200_000m } } } },
        });
        await _factory.DrainOutbox();
        var second = await BrokersQuote(revised);
        (second.GetProperty("quoteVersion").GetInt32(), second.GetProperty("status").GetString(), second.GetProperty("premium").GetDecimal()).ShouldBe((2, "Rated", 6_050m));

        // A risk object without the vehicle group the rating reads: the quote stays Draft, saying why.
        await _db.ExecuteAsOwner("""
            INSERT INTO general.cfg_risk_object_types_and_attribute_schemas (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                                             object_type, attributes, validation)
            VALUES (@t, 'TRAILER', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'TRAILER', '["registration:String(10):Y","year:Integer:Y"]', '[]')
            """, ("t", Tenant), ("from", Today.AddDays(-30)));
        var trailer = await Created("/api/v1/general/risk-objects", new { objectType = "TRAILER", attributes = new { registration = "CA77", year = Today.Year - 1 } });
        var unpriced = await PortalQuote(trailer);
        (unpriced.GetProperty("status").GetString(), unpriced.GetProperty("premium").ValueKind).ShouldBe(("Draft", JsonValueKind.Null));
        unpriced.GetProperty("ratingIssue").GetProperty("code").GetString().ShouldBe("RATING_INPUT_MISSING");
        unpriced.GetProperty("ratingIssue").GetProperty("reason").GetString()!.ShouldContain("vehicle_group");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM rating.rating_request WHERE subject_id = @q", ("q", unpriced.GetProperty("id").GetGuid()))).ShouldBe(0L);

        // Over a zone and peril limit: the rules refer it as soon as it is rated, and the broker sees no price while it is referred.
        await Seed("general.ref_risk_zones", "ACTIVE", "ZHAIL", ("zone_code", "ZHAIL"), ("peril", "HAIL"), ("geography", JsonSerializer.SerializeToElement(new { region = "Hail belt" })));
        await Seed("general.cfg_aggregation_zones_and_limits", "EFFECTIVE", "ZL-HAIL", ("zone", "ZHAIL"), ("peril", "HAIL"), ("max_exposure", 50_000m), ("currency", "ZAR"));
        await Seed("general.ref_underwriting_decision_codes", "ACTIVE", "ACCEPT", ("label", "Accepted"), ("requires_terms", false));
        var exposed = await Created("/api/v1/general/risk-objects", new
        {
            objectType = "VEHICLE", attributes = new { registration = "CA8080", vehicle_group = "G3", year = Today.Year - 2 }, riskZone = "ZHAIL",
        });
        var referred = await PortalQuote(exposed);
        (referred.GetProperty("status").GetString(), referred.GetProperty("premium").ValueKind).ShouldBe(("Referred", JsonValueKind.Null));
        var referredId = referred.GetProperty("id").GetGuid();
        var referral = (await (await _maker.GetAsync($"/api/v1/general/quotes/{referredId}")).Json()).GetProperty("referrals").EnumerateArray().Single();
        (referral.GetProperty("reasonCode").GetString(), referral.GetProperty("referredBy").ValueKind).ShouldBe(("AGG_LIMIT", JsonValueKind.Null));

        // It is decided under the tenant's underwriting authority, as a staff quote's referral is: the underwriter's covers 50 000,
        // not this 100 000; the senior underwriter's covers the amount but not a decision code outside its types. Within both, approved, the broker sees the price.
        await Seed("general.ref_underwriting_decision_codes", "ACTIVE", "ACCEPT_LOADED", ("label", "Accepted with a loading"), ("requires_terms", false));
        object Decision(string code) => new { approved = true, decisionCode = code, specialConditions = Array.Empty<string>() };
        (await (await _maker.PostAsJsonAsync($"/api/v1/general/quotes/{referredId}/referral-decision", Decision("ACCEPT"))).Code())
            .ShouldBe("GENERAL_UW_AUTHORITY_EXCEEDED", "over the underwriter's amount");
        (await (await _checker.PostAsJsonAsync($"/api/v1/general/quotes/{referredId}/referral-decision", Decision("ACCEPT_LOADED"))).Code())
            .ShouldBe("GENERAL_UW_AUTHORITY_EXCEEDED", "a decision type outside the senior underwriter's");
        (await BrokersQuote(referredId)).GetProperty("status").GetString().ShouldBe("Referred");

        // Its referral task is in the queue of the role whose authority covers it: the senior underwriter's (the underwriter's
        // 50 000 does not cover 100 000). The decision closes it.
        string ReferralTask(string column) => $"""
            SELECT t.{column} FROM workflow.task t JOIN workflow.process_instance p ON p.tenant_id = t.tenant_id AND p.id = t.process_instance_id
            WHERE p.process_code = 'GENERAL_REFERRAL' AND p.business_ref_type = 'GENERAL_QUOTE' AND p.business_ref_id = @q
            """;
        var seniorRole = await _db.ScalarAsOwner<string>("""
            SELECT a.role_code FROM iam.user_role_assignment a JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id
            WHERE u.external_subject = 'senior-underwriter' AND a.role_code IN (SELECT role_code FROM rules.cfg_authority_matrices WHERE code = 'AM-SENIOR')
            """);
        (await _db.ScalarAsOwner<string>(ReferralTask("queue_code"), ("q", referredId))).ShouldBe(seniorRole);
        (await _checker.PostAsJsonAsync($"/api/v1/general/quotes/{referredId}/referral-decision", Decision("ACCEPT"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>(ReferralTask("status"), ("q", referredId))).ShouldBe("COMPLETED");
        var approved = await BrokersQuote(referredId);
        (approved.GetProperty("status").GetString(), approved.GetProperty("premium").GetDecimal()).ShouldBe(("Rated", 3_050m));

        // A staff quote is rated when staff ask, as before.
        var staffQuote = await Created("/api/v1/general/quotes", QuoteBody(await Vehicle(), null));
        await _factory.DrainOutbox();
        var draft = await (await _maker.GetAsync($"/api/v1/general/quotes/{staffQuote}")).Json();
        (draft.GetProperty("status").GetString(), draft.GetProperty("pricedOnCreation").GetBoolean()).ShouldBe(("Draft", false));

        // BR-097: a staff quote under the broker's code shows the broker it waits for staff (pricedOnCreation false).
        await _db.ExecuteAsOwner("UPDATE general.quote SET intermediary_code = 'BRK-A', proposer_party_id = @c WHERE id = @q", ("c", client), ("q", staffQuote));
        var waiting = await BrokersQuote(staffQuote);
        (waiting.GetProperty("status").GetString(), waiting.GetProperty("pricedOnCreation").GetBoolean()).ShouldBe(("Draft", false));
        DocumentedSchema.ResponseProblems(waiting, "core", "PortalGeneralGetQuote").ShouldBeEmpty();
    }

    /// <summary>
    /// BR-092 (decided by Ade, 28 October 2026): a risk object belongs to a client. A broker registers one for a client it
    /// services and quotes on it; it lists and reads the items of clients it services now, and no other broker's. A staff
    /// change of owner is audited. When the client's business moves to another broker, the first loses the items and the
    /// second gains them.
    /// </summary>
    [Fact]
    public async Task ABroker_RegistersAndListsItsClientsItems_AndLosesThemWhenTheClientMoves_BR092()
    {
        var brokerA = await Broker("BRK-A", GeneralPermissions.RiskCreate, GeneralPermissions.RiskRead);
        var brokerB = await Broker("BRK-B", GeneralPermissions.RiskCreate, GeneralPermissions.RiskRead);
        var client = await _maker.RegisterPerson("Client");
        var (policyA, insured) = await IssuedPolicy(proposer: client, intermediaryCode: "BRK-A");

        async Task<HttpResponseMessage> Register(HttpClient broker, Guid owner) => await broker.Send(HttpMethod.Post, "/api/v1/portal/general/risk-objects", new
        {
            clientPartyId = owner, objectType = "VEHICLE", attributes = new { registration = "CA4242", vehicle_group = "G3", year = Today.Year - 1 },
            valuation = 150_000m, valuationBasis = "MARKET", valuationCurrency = "ZAR",
        }, idempotent: true);

        async Task<Guid[]> Items(HttpClient broker, HttpStatusCode expected = HttpStatusCode.OK)
        {
            var listed = await broker.GetAsync($"/api/v1/portal/general/risk-objects?clientPartyId={client}&limit=1");
            listed.StatusCode.ShouldBe(expected, await listed.Content.ReadAsStringAsync());
            if (expected != HttpStatusCode.OK)
            {
                return [];
            }

            // Paged one at a time, so every item comes back once.
            var items = new List<Guid>();
            var page = await listed.Json();
            while (true)
            {
                items.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
                if (page.GetProperty("nextCursor").GetString() is not { } next)
                {
                    return [.. items];
                }

                page = await (await broker.GetAsync($"/api/v1/portal/general/risk-objects?clientPartyId={client}&limit=1&cursor={Uri.EscapeDataString(next)}")).Json();
            }
        }

        object QuoteOn(Guid item) => new
        {
            productCode = Product, proposerPartyId = client, currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1),
            risks = new[] { new { riskObjectId = item, sumInsured = 100_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m } } } },
        };

        await Seed("general.ref_valuation_bases", "ACTIVE", "MARKET", ("label", "Market value"));

        // The broker registers the client's car: it belongs to the client, and the broker quotes on it.
        var registered = await Register(brokerA, client);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var car = await registered.Json();
        var carId = car.GetProperty("id").GetGuid();
        (car.GetProperty("ownerPartyId").GetGuid(), car.GetProperty("valuationCurrency").GetString()).ShouldBe((client, "ZAR"));
        car.TryGetProperty("rowVersion", out _).ShouldBeFalse();
        (await (await _maker.GetAsync($"/api/v1/general/risk-objects/{carId}")).Json()).GetProperty("ownerPartyId").GetGuid().ShouldBe(client);
        (await brokerA.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", QuoteOn(carId), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The insured vehicle staff registered has no owner yet: the broker neither sees it nor quotes on it.
        (await Items(brokerA)).ShouldBe([carId]);
        (await brokerA.GetAsync($"/api/v1/portal/general/risk-objects/{insured}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await brokerA.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", QuoteOn(insured), idempotent: true)).Code()).ShouldBe("OUT_OF_SCOPE");

        // Another broker sees none of it, by list or by ID, and cannot register for the client.
        await Items(brokerB, HttpStatusCode.NotFound);
        (await brokerB.GetAsync($"/api/v1/portal/general/risk-objects/{carId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await Register(brokerB, client)).Code()).ShouldBe("OUT_OF_SCOPE");

        // Staff give the insured vehicle its owner, audited with the owner before and after; the same owner again is refused.
        var etag = $"\"{(await (await _maker.GetAsync($"/api/v1/general/risk-objects/{insured}")).Json()).GetProperty("rowVersion").GetInt64()}\"";
        var changed = await _maker.Send(HttpMethod.Post, $"/api/v1/general/risk-objects/{insured}/owner", new { ownerPartyId = client }, ifMatch: etag);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("SELECT details::text FROM audit.audit_event WHERE operation = 'ChangeRiskObjectOwner' AND target_id = @r",
            ("r", insured.ToString())))!.ShouldContain($"\"owner_after\": \"{client}\"");
        var again = (await (await _maker.GetAsync($"/api/v1/general/risk-objects/{insured}")).Json()).GetProperty("rowVersion").GetInt64();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/general/risk-objects/{insured}/owner", new { ownerPartyId = client }, ifMatch: $"\"{again}\"")).Code())
            .ShouldBe("INVALID_STATE_TRANSITION");
        (await Items(brokerA)).Order().ShouldBe(new[] { carId, insured }.Order());
        ((await (await _maker.GetAsync($"/api/v1/general/risk-objects?ownerPartyId={client}")).Json()).GetProperty("items").GetArrayLength()).ShouldBe(2);

        // The client moves its business: its policy with broker A is cancelled and broker B's is issued. A loses the items, B gains them.
        (await _maker.PostAsJsonAsync($"/api/v1/general/policies/{policyA}/cancel", new { reasonCode = "INS_REQUEST", effectiveDate = Today })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        await IssuedPolicy(proposer: client, intermediaryCode: "BRK-B");
        await Items(brokerA, HttpStatusCode.NotFound);
        (await brokerA.GetAsync($"/api/v1/portal/general/risk-objects/{carId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await Register(brokerA, client)).Code()).ShouldBe("OUT_OF_SCOPE");
        (await (await brokerA.Send(HttpMethod.Post, "/api/v1/portal/general/quotes", QuoteOn(carId), idempotent: true)).Code()).ShouldBe("OUT_OF_SCOPE");
        (await Items(brokerB)).Order().ShouldBe(new[] { carId, insured }.Order());
        (await brokerB.GetAsync($"/api/v1/portal/general/risk-objects/{carId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// BR-093: a member reads the perils and loss causes in force for their policy's product line, with labels, to choose
    /// from when notifying a loss. Another line's codes and a retired cause are not listed; another member's policy is 404.
    /// A loss notified by staff or on the portal takes only those codes, a cause with its own peril (Ade, 28 October 2026).
    /// </summary>
    [Fact]
    public async Task AMember_ReadsTheLossCodesOfTheirPolicysLine_AndNotificationsTakeOnlyThem_BR093()
    {
        var holder = await _maker.RegisterPerson("Holder");
        var (policy, vehicle) = await IssuedPolicy(proposer: holder);
        var (others, _) = await IssuedPolicy();
        await Seed("general.ref_peril_codes", "ACTIVE", "THEFT_P", ("peril_code", "THEFT_P"), ("label", "Theft"), ("line", "MOTOR"), ("catastrophe", false));
        await Seed("general.ref_loss_cause_codes", "ACTIVE", "HIJACK", ("cause_code", "HIJACK"), ("peril", "THEFT_P"), ("label", "Hijacking"));
        await Seed("general.ref_loss_cause_codes", "ACTIVE", "BREAK_IN", ("cause_code", "BREAK_IN"), ("peril", "THEFT_P"), ("label", "Break-in"));
        await Seed("general.ref_peril_codes", "ACTIVE", "FLOOD", ("peril_code", "FLOOD"), ("label", "Flood"), ("line", "PROPERTY"), ("catastrophe", true));
        await Seed("general.ref_loss_cause_codes", "ACTIVE", "RIVER", ("cause_code", "RIVER"), ("peril", "FLOOD"), ("label", "River flooding"));
        await _db.ExecuteAsOwner("UPDATE general.ref_loss_cause_codes SET record_status = 'RETIRED' WHERE tenant_id = @t AND code = 'BREAK_IN'", ("t", Tenant));

        // Staff: another line's peril, or a cause of another peril, is refused; the line's own pair is taken.
        object Loss(string cause, string peril) => new { policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = cause, perilCode = peril };
        await Created("/api/v1/general/claims", Loss("RIVER", "FLOOD"), expectFailureCode: "GENERAL_LOSS_CODE_NOT_FOR_LINE");
        await Created("/api/v1/general/claims", Loss("HIJACK", "ACC"), expectFailureCode: "GENERAL_LOSS_CODE_NOT_FOR_LINE");
        await Created("/api/v1/general/claims", Loss("HIJACK", "THEFT_P"));

        var memberRole = await PortalUser("member-h", "PARTY", holder, null, GeneralPermissions.PolicyRead, GeneralPermissions.ClaimRead, GeneralPermissions.ClaimCreate);
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-MEM", ("channel_code", "MEM"), ("audience", "CUSTOMER"), ("name", "Member portal"),
            ("client_ids", new[] { "member-portal" }));
        await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-MEM-C", ("channel_code", "MEM"), ("role_code", memberRole),
            ("transaction_type", GeneralPermissions.ClaimCreate), ("allowed", true));
        await _db.AdoptFunctionCatalogue();
        var member = _factory.ClientFor(Tenant, "member-h", "member-portal");

        var read = await member.GetAsync($"/api/v1/portal/general/policies/{policy}/loss-codes");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        var codes = await read.Json();
        codes.GetProperty("perils").EnumerateArray().Select(p => (p.GetProperty("code").GetString(), p.GetProperty("label").GetString()))
            .ShouldBe([("ACC", "Accident"), ("THEFT_P", "Theft")]);
        codes.GetProperty("causes").EnumerateArray().Select(c => (c.GetProperty("code").GetString(), c.GetProperty("perilCode").GetString(), c.GetProperty("label").GetString()))
            .ShouldBe([("COLLISION", "ACC", "Collision"), ("HIJACK", "THEFT_P", "Hijacking")]);

        // The portal's notification takes only the listed codes too.
        (await (await member.Send(HttpMethod.Post, "/api/v1/portal/general/claims", Loss("RIVER", "FLOOD"), idempotent: true)).Code()).ShouldBe("GENERAL_LOSS_CODE_NOT_FOR_LINE");
        var notified = await member.Send(HttpMethod.Post, "/api/v1/portal/general/claims", Loss("COLLISION", "ACC"), idempotent: true);
        notified.StatusCode.ShouldBe(HttpStatusCode.Created, await notified.Content.ReadAsStringAsync());

        (await member.GetAsync($"/api/v1/portal/general/policies/{others}/loss-codes")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await member.GetAsync($"/api/v1/portal/general/policies/{Guid.CreateVersion7()}/loss-codes")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The instalment loading carries through (Ade, 28 October 2026): a quote priced monthly at a 5% loading shows each
    /// instalment and the total payable; the policy keeps the frequency and loading (another frequency is refused); and
    /// Billing's schedule charges the instalments as quoted, the last taking the rounding, so the year totals exactly the
    /// loaded annual premium: 3 050.00 × 1.05 = 3 202.50, eleven of 266.88 and one of 266.82.
    /// </summary>
    [Fact]
    public async Task ALoadedMonthlyQuote_IsBilledAsQuoted_TheYearTotallingTheLoadedPremium()
    {
        await Seed("general.cfg_product_billing_frequencies", "EFFECTIVE", $"{Product}-MONTHLY", ("product_code", Product), ("frequency", "MONTHLY"),
            ("instalment_loading_pct", 5m));
        await Seed("billing.ref_billing_frequencies", "ACTIVE", "QUARTERLY", ("label", "Quarterly"), ("periods_per_year", 4));
        var vehicle = await Vehicle();
        var quote = await Created("/api/v1/general/quotes", new
        {
            productCode = Product, proposerPartyId = Guid.CreateVersion7(), currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1), frequency = "MONTHLY",
            risks = new[] { new { riskObjectId = vehicle, sumInsured = 100_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m } } } },
        });
        await Rate(GeneralRatingSubjects.Quote, quote, 100_000m);
        var quoted = await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json();
        (quoted.GetProperty("premium").GetDecimal(), quoted.GetProperty("premiumFrequency").GetString(), quoted.GetProperty("instalmentPremium").GetDecimal(),
            quoted.GetProperty("totalPayable").GetDecimal()).ShouldBe((3_050m, "MONTHLY", 266.88m, 3_202.50m));

        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposal = await Created("/api/v1/general/proposals", new { quoteId = quote, declarations = new { claims_last_3_years = 0 } });
        (await _maker.PostAsync($"/api/v1/general/proposals/{proposal}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Created("/api/v1/general/policies", new { quoteId = quote, billingFrequency = "QUARTERLY", collectionMethod = "DEBIT_ORDER" },
            expectFailureCode: "GENERAL_FREQUENCY_NOT_QUOTED");
        var policy = await Created("/api/v1/general/policies", new { quoteId = quote, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER" });
        var issued = await Policy(policy);
        (issued.GetProperty("annualPremium").GetDecimal(), issued.GetProperty("billingFrequency").GetString(), issued.GetProperty("instalmentLoadingPct").GetDecimal())
            .ShouldBe((3_050m, "MONTHLY", 5m));

        await _factory.DrainOutbox();
        var amounts = await _db.ScalarAsOwner<decimal[]>("""
            SELECT array_agg(i.amount ORDER BY i.due_date) FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.tenant_id = i.tenant_id AND s.id = i.schedule_id
            WHERE s.policy_ref = @p AND i.status <> 'CANCELLED'
            """, ("p", policy));
        amounts!.Length.ShouldBe(12);
        amounts.Take(11).ShouldAllBe(a => a == 266.88m);
        amounts[^1].ShouldBe(266.82m);
        amounts.Sum().ShouldBe(3_202.50m);
        (await _db.ScalarAsOwner<decimal>("SELECT total_amount FROM billing.invoice WHERE policy_ref = @p ORDER BY issue_date LIMIT 1", ("p", policy))).ShouldBe(266.88m);
    }

    /// <summary>A portal user linked to a party (a member) or an intermediary code (a broker), holding the permissions; gives its role.</summary>
    private async Task<string> PortalUser(string subject, string kind, Guid? party, string? broker, params string[] permissions)
    {
        var user = await _db.UserWithPermissions(Tenant, subject, permissions);
        await _db.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @k, @p, @i, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", Tenant), ("u", user), ("k", kind), ("p", (object?)party ?? DBNull.Value), ("i", (object?)broker ?? DBNull.Value));
        return (await _db.ScalarAsOwner<string>("SELECT role_code FROM iam.user_role_assignment WHERE user_id = @u", ("u", user)))!;
    }

    /// <summary>
    /// A broker on the broker portal: the intermediary, allowed to sell the product, and a portal user linked to its code,
    /// on the tenant's broker and back-office channels, where its role may create the transactions given.
    /// </summary>
    private async Task<HttpClient> Broker(string code, params string[] transactions)
    {
        await Seed("commission.mst_intermediary_register", "ACTIVE", code, ("intermediary_code", code), ("type", "BROKER"), ("legal_name", code),
            ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", "ACTIVE"), ("hierarchy_node", $"NODE-{code}"));
        await Seed("commission.cfg_intermediary_product_permissions", "EFFECTIVE", $"PERM-{code}", ("intermediary", code), ("product", Product),
            ("transactions_allowed", Array.Empty<string>()), ("plan_code", "PLAN-10"));
        var subject = $"broker-{code.ToLowerInvariant()}";
        var role = await PortalUser(subject, "INTERMEDIARY", null, code, [.. BrokerPermissions, .. transactions]);
        if (await _db.ScalarAsOwner<long>("SELECT count(*) FROM channel.cfg_channel_definitions WHERE channel_code = 'BRK'") == 0)
        {
            await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-BRK", ("channel_code", "BRK"), ("audience", "BROKER"), ("name", "Broker portal"),
                ("client_ids", new[] { "broker-portal" }));
            await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-STF", ("channel_code", "STF"), ("audience", "STAFF"), ("name", "Back office"),
                ("client_ids", new[] { "insys-web" }));
        }

        foreach (var (transaction, t) in new[] { GeneralPermissions.QuoteCreate }.Concat(transactions).Select((x, i) => (x, i)))
        {
            await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", $"CP-{code}-{t}", ("channel_code", "BRK"), ("role_code", role),
                ("transaction_type", transaction), ("allowed", true));
        }

        // With channels in force, staff work through the back office's application, which lets their roles make what these tests make.
        var staffRoles = (await _db.ScalarAsOwner<string[]>("""
            SELECT array_agg(DISTINCT a.role_code) FROM iam.user_role_assignment a JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id
            WHERE u.external_subject IN ('underwriter', 'senior-underwriter')
            """))!;
        string[] staffTransactions =
        [
            GeneralPermissions.RiskCreate, GeneralPermissions.RiskUpdate, GeneralPermissions.QuoteCreate, GeneralPermissions.QuoteUpdate, GeneralPermissions.QuoteUnderwrite,
            GeneralPermissions.PolicyCreate, GeneralPermissions.PolicyUpdate, GeneralPermissions.PolicyCancel, RatingPermissions.CalculationCreate, PartyPermissions.PartyCreate,
        ];
        for (var r = 0; r < staffRoles.Length; r++)
        {
            for (var t = 0; t < staffTransactions.Length; t++)
            {
                var (staffRole, transaction, permissionCode) = (staffRoles[r], staffTransactions[t], $"CP-STF-{r}-{t}");
                if (await _db.ScalarAsOwner<long>("SELECT count(*) FROM channel.cfg_channel_transaction_permissions WHERE code = @c", ("c", permissionCode)) == 0)
                {
                    await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", permissionCode, ("channel_code", "STF"), ("role_code", staffRole),
                        ("transaction_type", transaction), ("allowed", true));
                }
            }
        }

        await _db.AdoptFunctionCatalogue();
        _maker = _factory.ClientFor(Tenant, "underwriter", "insys-web");
        _checker = _factory.ClientFor(Tenant, "senior-underwriter", "insys-web");
        return _factory.ClientFor(Tenant, subject, "broker-portal", multiFactor: true);
    }

    private static readonly string[] BrokerPermissions =
    [
        GeneralPermissions.PolicyRead, GeneralPermissions.QuoteRead, GeneralPermissions.QuoteCreate, GeneralPermissions.ClaimRead, PartyPermissions.PartyCreate,
        BillingPermissions.InvoiceRead, DocumentPermissions.DocumentRead,
    ];

    private async Task<Guid> Vehicle() => await Created("/api/v1/general/risk-objects", new
    {
        objectType = "VEHICLE", attributes = new { registration = "CA" + Random.Shared.Next(100_000, 999_999), vehicle_group = "G3", year = Today.Year - 2 },
    });

    private static object QuoteBody(Guid vehicle, string? orgNodeCode) => new
    {
        productCode = Product, proposerPartyId = Guid.CreateVersion7(), currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1), orgNodeCode,
        risks = new[] { new { riskObjectId = vehicle, sumInsured = 100_000m, coverages = new object[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m, excessScheduleCode = "MOT-EXC" } } } },
    };

    private async Task<Guid> QuoteAt(string orgNodeCode) => await Created("/api/v1/general/quotes", QuoteBody(await Vehicle(), orgNodeCode));

    /// <summary>
    /// GAP-044, ADR-025: at 23:30 UTC it is 01:30 the next day in Johannesburg, and business dates follow the tenant's time
    /// zone. A policy whose cover starts on that (local) day is in force when issued, its first debit order falls due and
    /// is invoiced, and a claim for an event on the first day of cover is accepted. On UTC dates all three would wait a day.
    /// </summary>
    [Fact]
    public async Task AtOneThirtyInJohannesburg_CoverStartingThatDayIsInForce_ItsDebitOrderIsDue_AndAFirstDayClaimIsAccepted_GAP044()
    {
        var utcDay = DateOnly.FromDateTime(WindowClock.Start.UtcDateTime);
        var localDay = utcDay.AddDays(1);
        await using var boundary = new InsysApiFactory(_db, new Dictionary<string, string?> { ["Identity:PermissionCache:AllowMultipleInstances"] = "true" },
            time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(utcDay.ToDateTime(new TimeOnly(23, 30)), TimeSpan.Zero)));
        var (factory, maker, checker) = (_factory, _maker, _checker);
        (_factory, _maker, _checker) = (boundary, boundary.ClientFor(Tenant, "underwriter"), boundary.ClientFor(Tenant, "senior-underwriter"));
        try
        {
            var (policy, vehicle) = await IssuedPolicy(start: localDay);
            await _factory.DrainOutbox();

            // In force on issue: its start date is today in the tenant's time zone.
            (await Policy(policy)).GetProperty("status").GetString().ShouldBe("InForce");

            // The first monthly debit order falls due on the start date, today locally, and is invoiced now.
            (await _db.ScalarAsOwner<DateOnly>("SELECT min(due_date) FROM billing.invoice WHERE policy_ref = @p AND document_type = 'INVOICE'", ("p", policy)))
                .ShouldBe(localDay);

            // A claim for an event on the first day of cover is not "in the future".
            var claim = await Created("/api/v1/general/claims", new
            {
                policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = localDay, causeCode = "COLLISION", perilCode = "ACC",
                description = "Reversed into a pole on the first morning",
            });
            var registered = await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null);
            registered.StatusCode.ShouldBe(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());
        }
        finally
        {
            (_factory, _maker, _checker) = (factory, maker, checker);
        }
    }

    [Fact]
    public async Task GeneralQuoteToCash_WithItsCompensations()
    {
        var (policy, _) = await IssuedPolicy();
        await _factory.DrainOutbox();

        // GenerateDocument: the certificate has no template assigned, so the delivery is parked for the missing template (ADR-022),
        // not retried into a dead letter. Compensation: the policy is not rolled back, and the documents are generated once it is fixed.
        (await Policy(policy)).GetProperty("status").GetString().ShouldBe("InForce");
        (await Documents(policy, "POLICY_SCHEDULE")).ShouldBe(0);
        (await Documents(policy, "CERTIFICATE")).ShouldBe(0);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message WHERE source = 'outbox:general'")).ShouldBe(0L);
        var parked = await (await _maker.GetAsync($"/api/v1/integration/parked-deliveries?status=Parked&code={PrerequisiteCodes.DocumentTemplateNotInForce}")).Json();
        ShouldMatchDocumented(parked, "ListParkedDeliveries");
        parked.GetProperty("items").EnumerateArray().Single().GetProperty("prerequisiteItem").GetString().ShouldEndWith(":CERTIFICATE");
        await AssignTemplate("CERTIFICATE");
        var retry = await _maker.PostAsJsonAsync("/api/v1/integration/parked-deliveries/retry", new { code = PrerequisiteCodes.DocumentTemplateNotInForce });
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted, await retry.Content.ReadAsStringAsync());
        await _factory.Services.GetRequiredService<Insys.Integration.Infrastructure.Parking.ParkingJob>().RunForTenant(Tenant, CancellationToken.None);
        await _factory.DrainOutbox();
        (await Documents(policy, "CERTIFICATE")).ShouldBe(1);
        (await Documents(policy, "POLICY_SCHEDULE")).ShouldBe(1);

        // OpenAccount, CreateSchedule and IssueInvoice: the first monthly instalment of 3 050.00, posted (PREMIUM_DUE).
        var invoice = await _db.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p AND document_type = 'INVOICE'", ("p", policy));
        var issued = await (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json();
        issued.GetProperty("totalAmount").GetDecimal().ShouldBe(254.17m);
        (await Journals("INVOICE", invoice)).Single().GetProperty("lines").EnumerateArray().First().GetProperty("postingRuleCode").GetString().ShouldBe("PR-PREM");

        // InitiatePayment (collection), the provider confirms, PaymentReceived and PaymentAllocated, MarkSettled, posted.
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
        (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await InvoiceStatus(invoice)).ShouldBe("Paid");
        (await Journals("PAYMENT", payment)).Count.ShouldBe(1);

        // Compensation: the payment is reversed; the invoice reopens.
        (await _maker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/reverse", new { reason = "Debit order returned" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await InvoiceStatus(invoice)).ShouldBe("Issued");
        (await Journals("PAYMENT", payment)).Count.ShouldBe(2);
    }

    /// <summary>
    /// GAP-031: General's quote, proposal, policy and risk object responses match the schemas the core document gives
    /// them. Before module-specific schema IDs, General's proposal and policy were documented with Life's shapes.
    /// </summary>
    [Fact]
    public async Task GeneralResponses_MatchTheirDocumentedSchemas_GAP031()
    {
        var (policy, vehicle) = await IssuedPolicy();
        var policyView = await Policy(policy);
        var quote = await _db.ScalarAsOwner<Guid>("SELECT quote_id FROM general.policy WHERE id = @p", ("p", policy));
        var proposal = await _db.ScalarAsOwner<Guid>("SELECT id FROM general.proposal WHERE quote_id = @q", ("q", quote));

        ShouldMatchDocumented(policyView, "GeneralGetPolicy");
        ShouldMatchDocumented(await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json(), "GeneralGetQuote");
        ShouldMatchDocumented(await (await _maker.GetAsync($"/api/v1/general/proposals/{proposal}")).Json(), "GeneralGetProposal");
        ShouldMatchDocumented(await (await _maker.GetAsync($"/api/v1/general/risk-objects/{vehicle}")).Json(), "GeneralGetRiskObject");

        // GAP-032: the lists find them by party and by number.
        var policyNumber = policyView.GetProperty("policyNumber").GetString();
        var holder = policyView.GetProperty("policyholderPartyId").GetGuid();
        var byNumber = await (await _maker.GetAsync($"/api/v1/general/policies?policyNumber={policyNumber}")).Json();
        ShouldMatchDocumented(byNumber, "GeneralListPolicies");
        byNumber.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(policy);
        (await (await _maker.GetAsync($"/api/v1/general/policies?policyholderPartyId={holder}")).Json()).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).ShouldContain(policy);
        var proposer = (await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json()).GetProperty("proposerPartyId").GetGuid();
        var quotes = await (await _maker.GetAsync($"/api/v1/general/quotes?proposerPartyId={proposer}")).Json();
        ShouldMatchDocumented(quotes, "GeneralListQuotes");
        quotes.GetProperty("items").EnumerateArray().Select(q => q.GetProperty("id").GetGuid()).ShouldContain(quote);
    }

    [Fact]
    public async Task GeneralClaimToSettlement_WithItsCompensations()
    {
        var (policy, vehicle) = await IssuedPolicy();
        await AssignTemplate("CERTIFICATE");
        await _factory.DrainOutbox();

        // BR-038: the policy read lists what its issue set off in other modules, each as it arrived.
        var effects = (await Policy(policy)).GetProperty("effects");
        async Task<List<Guid>> Ids(string sql) =>
            [.. (await _db.ScalarAsOwner<Guid[]>($"SELECT coalesce(array_agg(id ORDER BY id), '{{}}') FROM {sql}", ("p", policy)))!];
        List<Guid> Listed(string property) => [.. effects.GetProperty(property).EnumerateArray().Select(e => e.GetGuid()).Order()];
        Listed("documentIds").ShouldBe(await Ids("document.document WHERE business_ref_type = 'GENERAL_POLICY' AND business_ref_id = @p"));
        Listed("documentIds").ShouldNotBeEmpty("the schedule and certificate");
        Listed("invoiceIds").ShouldBe(await Ids("billing.invoice WHERE policy_ref = @p"));
        Listed("cessionIds").ShouldBe(await Ids("reinsurance.cession WHERE source_id = @p"));

        // NotifyLoss, then RegisterClaim: the cover is validated and the Claims Framework allocates the number.
        var uncovered = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "THEFT", lossDate = Today.AddDays(-400), causeCode = "COLLISION", perilCode = "ACC",
        });
        (await (await _maker.PostAsync($"/api/v1/general/claims/{uncovered}/register", null)).Code()).ShouldBe("LOSS_NOT_COVERED");
        // F009: the fraud indicators in force are evaluated when the claim is registered. This one fires (a loss on the day
        // of inception) below its referral threshold, so it is recorded without referring the claim.
        await Seed("claims.cfg_fraud_indicator_definitions", "EFFECTIVE", "E2E-EARLY-GEN", ("indicator_code", "EARLY_CLAIM"), ("logic", "days_since_inception < 30"),
            ("score", 10), ("referral_threshold", 100), ("referral_queue", "FRAUD"));
        // This one needs a fact General does not give, so it is skipped: recorded with why, and the configuration owner is told.
        await Seed("claims.cfg_fraud_indicator_definitions", "EFFECTIVE", "E2E-SUM-GEN", ("indicator_code", "HIGH_SUM_ASSURED"), ("logic", "sum_assured > 1000000"),
            ("score", 50), ("referral_threshold", 100), ("referral_queue", "FRAUD"));
        var claim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC",
            description = "Rear-ended at a robot",
        });
        var registered = await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json();
        var number = registered.GetProperty("claimNumber").GetString()!;
        number.ShouldStartWith("GEN-");
        registered.GetProperty("status").GetString().ShouldBe("Registered");
        ShouldMatchDocumented(await Claim(claim), "GeneralGetClaim");
        (await _db.ScalarAsOwner<string>("SELECT indicator_code || ':' || referred FROM claims.fraud_indicator_hit WHERE claim_number = @n", ("n", number)))
            .ShouldBe("EARLY_CLAIM:false", customMessage: "F009: fraud indicators are evaluated at registration");
        (await _db.ScalarAsOwner<string>("""
            SELECT s ->> 'indicatorCode' || ':' || (s ->> 'reason') || ':' || (s -> 'missingFacts' ->> 0)
            FROM claims.fraud_evaluation e CROSS JOIN jsonb_array_elements(e.skipped) s WHERE e.claim_number = @n
            """, ("n", number))).ShouldBe("HIGH_SUM_ASSURED:MISSING_FACTS:sum_assured", customMessage: "F009: a skipped indicator is recorded with why");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("""
            SELECT t.queue_code FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
            WHERE p.process_code = 'CONFIG_REVIEW' AND p.business_ref_type = 'FRAUD_INDICATOR'
            """)).ShouldBe("TENANT_ADMIN", customMessage: "F009: the configuration owner gets a review task, not only a log line");
        var claims = await (await _maker.GetAsync($"/api/v1/general/claims?claimNumber={number}")).Json();
        ShouldMatchDocumented(claims, "GeneralListClaims");
        claims.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(claim);
        (await (await _maker.GetAsync($"/api/v1/general/claims?policyId={policy}")).Json()).GetProperty("items").EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid()).ShouldContain(claim);
        (await _db.ScalarAsOwner<Guid>("SELECT domain_claim_id FROM claims.claim_register WHERE claim_number = @n", ("n", number))).ShouldBe(claim);
        (await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json()).GetProperty("claimNumber").GetString().ShouldBe(number);

        // SetReserve (Claims Framework), posted.
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 50_000m, currency = "ZAR", reason = "Assessed" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // AppointAssessor; the loss assessment is reported and accepted. The excess is the greater of 5 000 and 5 %.
        (await (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Guid.CreateVersion7() })).Code())
            .ShouldBe("ASSESSOR_NOT_ON_PANEL");
        // BR-031: two people working one claim. The version one read before the other's change is refused (412), never applied unseen.
        var read = await _maker.GetAsync($"/api/v1/general/claims/{claim}");
        var stale = read.Headers.ETag!.Tag;
        (await _maker.Send(HttpMethod.Post, $"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Assessor }, ifMatch: stale)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        var lost = await _maker.Send(HttpMethod.Post, $"/api/v1/general/claims/{claim}/parties", new { partyId = Guid.CreateVersion7(), role = "WITNESS" }, ifMatch: stale);
        lost.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await lost.Content.ReadAsStringAsync());
        (await _maker.Send(HttpMethod.Post, $"/api/v1/general/claims/{claim}/parties", new { partyId = Guid.CreateVersion7(), role = "WITNESS" }, raw: true)).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionRequired);
        // OQ-122: a claim party's role is one in force in the tenant's claim party roles.
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/general/claims/{claim}/parties", new { partyId = Guid.CreateVersion7(), role = "BYSTANDER" },
            ifMatch: (await _maker.GetAsync($"/api/v1/general/claims/{claim}")).ETag())).Code()).ShouldBe("GENERAL_CODE_NOT_IN_FORCE");
        var witnessed = await _maker.Send(HttpMethod.Post, $"/api/v1/general/claims/{claim}/parties", new { partyId = Guid.CreateVersion7(), role = "WITNESS" },
            ifMatch: (await _maker.GetAsync($"/api/v1/general/claims/{claim}")).ETag());
        witnessed.StatusCode.ShouldBe(HttpStatusCode.OK, await witnessed.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        var assessment = (await (await _maker.GetAsync($"/api/v1/general/claims/{claim}/assessments")).Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{assessment}/estimates", new { repairerPartyId = Repairer, amount = 41_000m }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var reported = await (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{assessment}/report", new { assessedAmount = 40_000m })).Json();
        reported.GetProperty("excessApplied").GetDecimal().ShouldBe(5_000m);
        reported.GetProperty("currency").GetString().ShouldBe("ZAR", "BR-033: the claim's policy's currency");
        reported.GetProperty("estimates").EnumerateArray().ShouldAllBe(e => e.GetProperty("currency").GetString() == "ZAR");
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // RequestApproval for the settlement. Compensation: rejected, the claim stays under assessment.
        // The payee and their verified bank account (party.bank_account): the settlement names the account by id only.
        var payee = await _maker.RegisterPerson("Repairs");
        var firstAccount = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "62 0001 2345");
        var settlement = new { payeePartyId = payee, bankAccountId = firstAccount, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Repair estimate within the assessed amount" };

        // F062 §2: the coverage's aggregate claims limit for the policy period is read at settlement; 35 000 is within 40 000 (GEN-008).
        await Seed("general.cfg_aggregate_limit_definitions", "EFFECTIVE", "AGG-OD", ("coverage", "OWN_DAMAGE"), ("period", "POLICY_PERIOD"), ("aggregate_amount", 40_000m),
            ("reinstatement", "NONE"));
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/settlement", settlement)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.SettlementBusinessRefType, claim, approve: false);
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("UnderAssessment");

        // Again: maker and checker differ; GeneralClaimApproved; DraftInstruction and ReleaseInstruction for 35 000.00.
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/settlement", settlement)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.SettlementBusinessRefType, claim, approve: true);
        var approvedClaim = await Claim(claim);
        approvedClaim.GetProperty("status").GetString().ShouldBe("Approved");
        // BR-035: the claim's loss description, who decided it and when, and the settlement's requester, account and currency.
        approvedClaim.GetProperty("lossDescription").GetString().ShouldBe("Rear-ended at a robot");
        approvedClaim.GetProperty("decidedBy").ValueKind.ShouldBe(JsonValueKind.String);
        approvedClaim.GetProperty("decidedAt").ValueKind.ShouldBe(JsonValueKind.String);
        var settled = approvedClaim.GetProperty("settlement");
        settled.GetProperty("requestedBy").ValueKind.ShouldBe(JsonValueKind.String);
        settled.GetProperty("bankAccountId").GetGuid().ShouldBe(firstAccount);
        settled.GetProperty("currency").GetString().ShouldBe("ZAR");
        var instruction = await _db.ScalarAsOwner<Guid>("SELECT id FROM claims.claim_payment_instruction WHERE claim_number = @n", ("n", number));
        var instructed = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json();
        instructed.GetProperty("status").GetString().ShouldBe("Released");
        instructed.GetProperty("amount").GetDecimal().ShouldBe(35_000m);
        // BR-035: the payee by name and the account by its last four, masked; never the number.
        instructed.GetProperty("payeeName").GetString()!.ShouldContain("Repairs");
        instructed.GetProperty("maskedAccount").GetString().ShouldBe("•••• 2345");
        instructed.ToString().ShouldNotContain("62 0001");

        // F062 §2: 35 000 of the coverage's 40 000 aggregate is used. A second loss on it settling 10 000 is refused.
        var secondClaim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC", description = "Hail damage",
        });
        (await _maker.PostAsync($"/api/v1/general/claims/{secondClaim}/register", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{secondClaim}/assessor", new { assessorPartyId = Assessor })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var secondAssessment = (await (await _maker.GetAsync($"/api/v1/general/claims/{secondClaim}/assessments")).Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/general/assessments/{secondAssessment}/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{secondAssessment}/report", new { assessedAmount = 15_000m })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/assessments/{secondAssessment}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var overAggregate = await _maker.PostAsJsonAsync($"/api/v1/general/claims/{secondClaim}/settlement", settlement);
        overAggregate.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await overAggregate.Code()).ShouldBe("AGGREGATE_LIMIT_EXCEEDED");

        // The settlement approval covered this payee and account, so it stands in for the instruction's approval
        // (ADR-009). The account number appears in no event, approval or claim, only its keyed hash and id.
        instructed.GetProperty("bankAccountId").GetGuid().ShouldBe(firstAccount);
        (await _db.ScalarAsOwner<long>("""
            SELECT (SELECT count(*) FROM general.outbox_event WHERE payload::text LIKE '%00012345%')
                 + (SELECT count(*) FROM claims.outbox_event WHERE payload::text LIKE '%00012345%')
                 + (SELECT count(*) FROM workflow.approval WHERE reason LIKE '%00012345%')
            """)).ShouldBe(0L);

        // Payout batch, approved and released. Compensation: the provider rejects the payment; the instruction returns to
        // Failed and is re-released to the same approved account at once, as a new payment.
        var first = await PayOut(instruction, "BOUNCED");
        (await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json()).GetProperty("status").GetString().ShouldBe("Failed");
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Approved");
        (await (await _maker.PostAsJsonAsync($"/api/v1/claims/payment-instructions/{instruction}/retry", new { payeePartyId = payee, bankAccountId = firstAccount })).Json())
            .GetProperty("status").GetString().ShouldBe("Released");
        await _factory.DrainOutbox();

        // The payee changes bank account before the batch is released: the account the payment was approved for is
        // superseded, so the payment fails without reaching the provider, and the new account needs a fresh approval.
        var second = await PayOut(instruction, null, beforeRelease: async () => await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "1234567890"));
        (await _db.ScalarAsOwner<string>("SELECT status FROM payment.payment WHERE id = @p", ("p", second))).ShouldBe("FAILED");
        (await _db.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", second))).ShouldBeNull();
        (await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json()).GetProperty("status").GetString().ShouldBe("Failed");
        var newAccount = (await (await _maker.GetAsync($"/api/v1/party/parties/{payee}/bank-accounts")).Json()).EnumerateArray()
            .Single(a => a.GetProperty("status").GetString() == "ACTIVE").GetProperty("id").GetGuid();
        (await (await _maker.PostAsJsonAsync($"/api/v1/claims/payment-instructions/{instruction}/retry", new { payeePartyId = payee, bankAccountId = firstAccount }))
            .Code()).ShouldBe("PAYEE_BANK_ACCOUNT_CHANGED");
        var retried = await (await _maker.PostAsJsonAsync($"/api/v1/claims/payment-instructions/{instruction}/retry", new { payeePartyId = payee, bankAccountId = newAccount }))
            .Json();
        retried.GetProperty("status").GetString().ShouldBe("Failed");
        retried.GetProperty("pendingBankAccountId").GetGuid().ShouldBe(newAccount);
        await _factory.DrainOutbox();
        await Decide(ClaimsApprovals.InstructionBusinessRefType, instruction, approve: true);
        var released = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json();
        released.GetProperty("status").GetString().ShouldBe("Released");
        released.GetProperty("bankAccountId").GetGuid().ShouldBe(newAccount);
        var third = await PayOut(instruction, "PAID");
        new[] { first, second, third }.Distinct().Count().ShouldBe(3);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'ReadBankAccountForPayment'")).ShouldBeGreaterThanOrEqualTo(2L);

        // ClaimPaymentCompleted → GeneralClaimSettled; the payment draws down the reserve and is posted.
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Settled");
        var financials = await (await _maker.GetAsync($"/api/v1/claims/claims/{number}/financials")).Json();
        financials.GetProperty("outstandingReserves").GetProperty("INDEMNITY").GetDecimal().ShouldBe(15_000m);
        var paid = financials.GetProperty("movements").EnumerateArray().Single(m => m.GetProperty("movementType").GetString() == "Payment").GetProperty("id").GetGuid();
        (await Journals("CLAIM_MOVEMENT", paid)).Single().GetProperty("lines").EnumerateArray().First().GetProperty("postingRuleCode").GetString().ShouldBe("PR-CLM-PAID");

        // A third-party recovery, recorded as a claim financial movement (CLM-004), then the claim is closed.
        var recovery = await Created($"/api/v1/general/claims/{claim}/recoveries", new { recoveryType = "SUBROGATION", counterpartyPartyId = Guid.CreateVersion7(), expectedAmount = 20_000m });
        (await _maker.PostAsync($"/api/v1/general/recoveries/{recovery}/demand", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.Send(HttpMethod.Post, $"/api/v1/general/recoveries/{recovery}/recovered", new { amount = 12_000m }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        financials = await (await _maker.GetAsync($"/api/v1/claims/claims/{number}/financials")).Json();
        financials.GetProperty("movements").EnumerateArray().Count(m => m.GetProperty("movementType").GetString() == "Recovery").ShouldBe(1);
        financials.GetProperty("currency").GetString().ShouldBe("ZAR", "BR-033: of every movement and outstanding reserve");
        (await (await _maker.GetAsync($"/api/v1/general/recoveries/{recovery}")).Json()).GetProperty("currency").GetString().ShouldBe("ZAR");
        (await _maker.PostAsync($"/api/v1/general/claims/{claim}/close", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Closed");
    }

    /// <summary>
    /// §5 (s26B): with its gates on, the tenant does not pay a General claim to a payee with a screening hit waiting for
    /// review. The settlement's instruction is held, not refused: nothing is paid, and it says why. When the hit is
    /// reviewed as a false positive, the instruction is released at once, and the payment follows.
    /// </summary>
    [Fact]
    public async Task AClaimPayment_ToAPayeeWithAnUnreviewedHit_IsHeld_AndReleasedWhenTheHitIsCleared_DueDiligence5()
    {
        await Seed("party.cfg_due_diligence_settings", "EFFECTIVE", "GATES-ON", ("gates_enabled", true), ("adverse_media_enabled", false));
        await Seed("integration.mst_partner_register", "ACTIVE", "SCR-1", ("partner_code", "SCR-1"), ("partner_type", "SCREENING"), ("legal_name", "Screening (stub)"),
            ("status", "ACTIVE"));
        var (policy, vehicle) = await IssuedPolicy();
        var claim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC", description = "Gate test",
        });
        var number = (await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json()).GetProperty("claimNumber").GetString()!;
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 50_000m, currency = "ZAR", reason = "Assessed" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Assessor })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var assessment = (await (await _maker.GetAsync($"/api/v1/general/claims/{claim}/assessments")).Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{assessment}/report", new { assessedAmount = 20_000m })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The payee's name matches the stub's sanctions list: screened when registered, the hit waits for review.
        var payee = await _maker.RegisterPerson("Sanctionlist Repairs");
        var account = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "62 0009 8765");
        await _factory.DrainOutbox();
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/settlement",
            new { payeePartyId = payee, bankAccountId = account, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Within the assessed amount" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.SettlementBusinessRefType, claim, approve: true);

        var instruction = await _db.ScalarAsOwner<Guid>("SELECT id FROM claims.claim_payment_instruction WHERE claim_number = @n", ("n", number));
        var held = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json();
        (held.GetProperty("status").GetString(), held.GetProperty("heldReason").GetString()).ShouldBe(("Approved", "DUE_DILIGENCE_NOT_CLEARED"));
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM payment.payment WHERE business_ref_id = @i", ("i", instruction))).ShouldBe(0L, "nothing is paid");
        var released = await _maker.Send(HttpMethod.Post, $"/api/v1/claims/payment-instructions/{instruction}/release",
            ifMatch: (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).ETag());
        (await released.Code()).ShouldBe("DUE_DILIGENCE_NOT_CLEARED", "a person cannot release it either");

        // The reviewer clears the hit: the payee is screened with no hit waiting, so the instruction is released and the payment created.
        var review = await (await _checker.GetAsync($"/api/v1/party/parties/{payee}/due-diligence")).Json();
        var hit = review.GetProperty("hits").EnumerateArray().Single().GetProperty("id").GetGuid();
        var etag = (await _checker.GetAsync($"/api/v1/party/parties/{payee}/due-diligence")).ETag();
        (await _checker.Send(HttpMethod.Post, $"/api/v1/party/parties/{payee}/due-diligence/hits/{hit}/review", new { outcome = "FALSE_POSITIVE", reason = "A different business" },
            ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var after = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json();
        (after.GetProperty("status").GetString(), after.GetProperty("heldReason").ValueKind).ShouldBe(("Released", JsonValueKind.Null));
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM payment.payment WHERE business_ref_id = @i", ("i", instruction))).ShouldBe(1L);
    }

    [Fact]
    public async Task Endorsement_IsAppliedAfterApproval_AndCancellationCreditsTheRefund_GEN003_GEN004()
    {
        var (policy, vehicle) = await IssuedPolicy();
        await AssignTemplate("CERTIFICATE");
        await _factory.DrainOutbox();

        var endorsement = await Created($"/api/v1/general/policies/{policy}/endorsements", new
        {
            endorsementType = "CHANGE_SI", effectiveDate = Today,
            changes = new[] { new { action = "ChangeRisk", riskObjectId = vehicle, sumInsured = 150_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 150_000m } } } },
        });
        await Rate(GeneralRatingSubjects.Endorsement, endorsement, 150_000m);
        var rated = await (await _maker.GetAsync($"/api/v1/general/endorsements/{endorsement}")).Json();
        rated.GetProperty("status").GetString().ShouldBe("Rated");
        rated.GetProperty("newAnnualPremium").GetDecimal().ShouldBe(4_550m);
        rated.GetProperty("currency").GetString().ShouldBe("ZAR", "BR-033: the policy's currency");

        // A risk object's valuation names its own currency (BR-033): required with a valuation, and only with one.
        await Seed("general.ref_valuation_bases", "ACTIVE", "MARKET", ("label", "Market value"));
        var uncurrencied = await _maker.Send(HttpMethod.Post, $"/api/v1/general/risk-objects/{vehicle}/revalue",
            new { valuation = 160_000m, valuationBasis = "MARKET" }, ifMatch: (await _maker.GetAsync($"/api/v1/general/risk-objects/{vehicle}")).ETag());
        uncurrencied.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await uncurrencied.Code()).ShouldBe("VALUATION_CURRENCY_REQUIRED");
        var revalued = await _maker.Send(HttpMethod.Post, $"/api/v1/general/risk-objects/{vehicle}/revalue",
            new { valuation = 160_000m, valuationBasis = "MARKET", valuationCurrency = "ZAR" }, ifMatch: (await _maker.GetAsync($"/api/v1/general/risk-objects/{vehicle}")).ETag());
        revalued.StatusCode.ShouldBe(HttpStatusCode.OK, await revalued.Content.ReadAsStringAsync());
        (await revalued.Json()).GetProperty("valuationCurrency").GetString().ShouldBe("ZAR");

        (await _maker.PostAsync($"/api/v1/general/endorsements/{endorsement}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Policy(policy)).GetProperty("annualPremium").GetDecimal().ShouldBe(3_050m);
        await Decide(GeneralApprovals.EndorsementBusinessRefType, endorsement, approve: true);

        // Applied: the policy's premium and coverages, the billing schedule from the effective date, and the endorsement document.
        var endorsed = await Policy(policy);
        endorsed.GetProperty("annualPremium").GetDecimal().ShouldBe(4_550m);
        var applied = await (await _maker.GetAsync($"/api/v1/general/endorsements/{endorsement}")).Json();
        applied.GetProperty("status").GetString().ShouldBe("Applied");
        applied.GetProperty("documentId").ValueKind.ShouldBe(JsonValueKind.String);
        (await _db.ScalarAsOwner<decimal>("SELECT instalment_amount FROM billing.premium_schedule WHERE policy_ref = @p", ("p", policy))).ShouldBe(379.17m);

        // CancelPolicy: pro rata refund, the schedule stops, and the refund is credited against the invoice.
        var cancelled = await (await _maker.PostAsJsonAsync($"/api/v1/general/policies/{policy}/cancel", new { reasonCode = "INS_REQUEST", effectiveDate = Today })).Json();
        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>(
            "SELECT count(*) FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.id = i.schedule_id WHERE s.policy_ref = @p AND i.status = 'SCHEDULED'",
            ("p", policy))).ShouldBe(0L);
        (await _db.ScalarAsOwner<decimal>("SELECT total_amount FROM billing.invoice WHERE policy_ref = @p AND document_type = 'CREDIT_NOTE'", ("p", policy))).ShouldBe(254.17m);

        (await _maker.PostAsync($"/api/v1/general/policies/{policy}/reinstate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Policy(policy)).GetProperty("status").GetString().ShouldBe("InForce");
    }

    [Fact]
    public async Task ReferredQuote_DeclinedWithAReason_IsFinal()
    {
        await Seed("general.ref_referral_reason_codes", "ACTIVE", "HIGH_VALUE", ("label", "High value"), ("target_queue", "UW"));
        await Seed("general.ref_underwriting_decision_codes", "ACTIVE", "DECLINE", ("label", "Declined"), ("requires_terms", false));
        var vehicle = await Created("/api/v1/general/risk-objects", new { objectType = "VEHICLE", attributes = new { registration = "CA9", vehicle_group = "G3", year = Today.Year } });
        var quote = await Created("/api/v1/general/quotes", new
        {
            productCode = Product, proposerPartyId = Guid.CreateVersion7(), currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1),
            risks = new[] { new { riskObjectId = vehicle, sumInsured = 400_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 400_000m } } } },
        });
        await Rate(GeneralRatingSubjects.Quote, quote, 400_000m);
        (await _maker.PostAsJsonAsync($"/api/v1/general/quotes/{quote}/refer", new { reasonCode = "HIGH_VALUE" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var declined = await (await _checker.PostAsJsonAsync($"/api/v1/general/quotes/{quote}/referral-decision",
            new { approved = false, decisionCode = "DECLINE", declineReason = "Outside the vehicle value appetite" })).Json();

        declined.GetProperty("status").GetString().ShouldBe("Declined");
        declined.GetProperty("referrals")[0].GetProperty("declineReason").GetString().ShouldBe("Outside the vehicle value appetite");
        (await (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM general.outbox_event WHERE event_type = 'general.GeneralQuoteDeclined'")).ShouldBe(1L);
    }

    [Fact]
    public async Task CoverStartingOnFirstPayment_StartsWhenTheFirstInvoiceIsSettled()
    {
        const string firstPayment = "E2E-GFP";
        var from = Today.AddDays(-30);
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'GENERAL', 'Motor', @coverages, 'MOTOR', 'FIRST_PAYMENT')
            """, ("t", Tenant), ("p", firstPayment), ("from", from), ("coverages", new[] { "OWN_DAMAGE", "THEFT" }));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GFP-P1", ("parameter_code", "GMOT-BASE"), ("product_version", $"{firstPayment} v1"), ("value", 1m), ("unit", "FACTOR"));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GFP-P2", ("parameter_code", "POLICY_FEE"), ("product_version", $"{firstPayment} v1"), ("value", 50m), ("unit", "AMOUNT"));
        await Seed("general.cfg_quote_validity_periods", "EFFECTIVE", "QV-GFP", ("product_code", firstPayment), ("days_valid", 30), ("re_rate_on_expiry", false));
        foreach (var type in new[] { "POLICY_SCHEDULE", "CERTIFICATE" })
        {
            await Seed("product.cfg_product_template_assignments", "EFFECTIVE", $"TAF-{type}", ("product_code", firstPayment), ("document_type", type),
                ("template", $"TPL-{type}"), ("language", "en"));
        }

        var (policy, vehicle) = await IssuedPolicy(firstPayment);
        await _factory.DrainOutbox();
        var issued = await Policy(policy);
        issued.GetProperty("status").GetString().ShouldBe("Issued");
        issued.GetProperty("coverStartsOn").GetString().ShouldBe("FIRST_PAYMENT");
        var loss = await Created("/api/v1/general/claims", new { policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC" });
        (await (await _maker.PostAsync($"/api/v1/general/claims/{loss}/register", null)).Code()).ShouldBe("LOSS_NOT_COVERED");

        // InvoiceSettled → cover starts.
        var invoice = await _db.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p", ("p", policy));
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
        (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var inForce = await Policy(policy);
        inForce.GetProperty("status").GetString().ShouldBe("InForce");
        inForce.GetProperty("firstPaidOn").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));
        (await (await _maker.PostAsync($"/api/v1/general/claims/{loss}/register", null)).Json()).GetProperty("status").GetString().ShouldBe("Registered");
    }

    /// <summary>A vehicle, a quote rated through Rating, issued and accepted (GeneralQuoteAccepted), a proposal, and the policy issued from it.</summary>
    private async Task<(Guid Policy, Guid Vehicle)> IssuedPolicy(string product = Product, DateOnly? start = null, string? riskZone = null, string? orgNodeCode = null,
        Guid? proposer = null, string? intermediaryCode = null)
    {
        var periodStart = start ?? Today;
        var vehicle = await Created("/api/v1/general/risk-objects", new
        {
            objectType = "VEHICLE", attributes = new { registration = "CA123456", vehicle_group = "G3", year = Today.Year - 2 }, valuation = 180_000m, valuationBasis = (string?)null,
        }, expectFailureCode: "VALUATION_BASIS_REQUIRED");
        vehicle = await Created("/api/v1/general/risk-objects", new
        {
            objectType = "VEHICLE", attributes = new { registration = "CA123456", vehicle_group = "G3", year = Today.Year - 2 }, riskZone,
        });
        var quote = await Created("/api/v1/general/quotes", new
        {
            productCode = product, proposerPartyId = proposer ?? Guid.CreateVersion7(), currency = "ZAR", periodStart, periodEnd = periodStart.AddYears(1).AddDays(-1), orgNodeCode,
            intermediaryCode,
            risks = new[]
            {
                new
                {
                    riskObjectId = vehicle, sumInsured = 100_000m,
                    coverages = new object[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m, excessScheduleCode = "MOT-EXC" }, new { coverageCode = "THEFT", sumInsured = 50_000m } },
                },
            },
        });
        await Rate(GeneralRatingSubjects.Quote, quote, 100_000m, product);
        (await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json()).GetProperty("premium").GetDecimal().ShouldBe(3_050m);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposal = await Created("/api/v1/general/proposals", new { quoteId = quote, declarations = new { claims_last_3_years = 0 } });
        (await _maker.PostAsync($"/api/v1/general/proposals/{proposal}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var policy = await Created("/api/v1/general/policies", new { quoteId = quote, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER" });

        // BR-035: the forward and back links from quote to proposal to policy, and the quote's node.
        var quoted = await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json();
        quoted.GetProperty("proposalId").GetGuid().ShouldBe(proposal);
        (quoted.GetProperty("orgNodeCode").ValueKind == JsonValueKind.Null ? null : quoted.GetProperty("orgNodeCode").GetString()).ShouldBe(orgNodeCode);
        (await (await _maker.GetAsync($"/api/v1/general/proposals/{proposal}")).Json()).GetProperty("policyId").GetGuid().ShouldBe(policy);
        var linked = await Policy(policy);
        (linked.GetProperty("quoteId").GetGuid(), linked.GetProperty("proposalId").GetGuid()).ShouldBe((quote, proposal));
        if (product != Product)
        {
            return (policy, vehicle);
        }

        (await Policy(policy)).GetProperty("risks")[0].GetProperty("coverages").EnumerateArray().Sum(c => c.GetProperty("premium").GetDecimal()).ShouldBe(3_050m);
        return (policy, vehicle);
    }

    private async Task Rate(string subjectType, Guid subjectId, decimal sumInsured, string product = Product)
    {
        var calculated = await _maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"E2E-{subjectId:N}"[..20], sourceDomain = GeneralRatingSubjects.SourceDomain, subjectType, subjectId, productCode = product, productVersion = 1,
            currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = sumInsured, vehicle_group = "G3" },
        }, idempotent: true);
        calculated.StatusCode.ShouldBe(HttpStatusCode.Created, await calculated.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    /// <summary>Batches the payout, approves and releases it, and answers it with the provider's status (none when the payment never reaches the provider).</summary>
    private async Task<Guid> PayOut(Guid instruction, string? providerStatus, Func<Task>? beforeRelease = null)
    {
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.ClaimPayout, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();

        // The payout batch keeps its own release approval, whatever approved the payments in it (SRS-002).
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Decide(PaymentApprovals.PayoutBusinessRefType, batch, approve: true);
        if (beforeRelease is not null)
        {
            await beforeRelease();
        }

        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i AND payout_batch_id = @b", ("i", instruction), ("b", batch));
        if (providerStatus is not null)
        {
            (await Callback(payment, providerStatus)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _factory.DrainOutbox();
        return payment;
    }

    /// <summary>The maker cannot decide their own request (SRS-002); the checker does.</summary>
    private async Task Decide(string businessRefType, Guid businessRefId, bool approve)
    {
        var page = await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={businessRefId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var decided = approve
            ? await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")
            : await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "Check the estimate" }, ifMatch: "\"1\"");
        decided.StatusCode.ShouldBe(HttpStatusCode.NoContent, await decided.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    private Task<HttpResponseMessage> Callback(Guid payment, string status) => _maker.SimulatorCallback(_factory, _db, payment, status);

    private async Task<Guid> Created(string url, object body, string? expectFailureCode = null)
    {
        var response = await _maker.Send(HttpMethod.Post, url, body, idempotent: true);
        if (expectFailureCode is not null)
        {
            (await response.Code()).ShouldBe(expectFailureCode);
            return Guid.Empty;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("id").GetGuid();
    }

    private static void ShouldMatchDocumented(JsonElement response, string operationId) =>
        DocumentedSchema.ResponseProblems(response, "core", operationId).ShouldBeEmpty(operationId);

    private async Task<JsonElement> Policy(Guid id) => await (await _maker.GetAsync($"/api/v1/general/policies/{id}")).Json();

    private async Task<JsonElement> Claim(Guid id) => await (await _maker.GetAsync($"/api/v1/general/claims/{id}")).Json();

    private async Task<string?> InvoiceStatus(Guid id) => (await (await _maker.GetAsync($"/api/v1/billing/invoices/{id}")).Json()).GetProperty("status").GetString();

    private async Task<List<JsonElement>> Journals(string businessRefType, Guid businessRefId) =>
        (await (await _maker.GetAsync($"/api/v1/finance/journals?businessRefType={businessRefType}&businessRefId={businessRefId}")).Json()).GetProperty("items").EnumerateArray().ToList();

    private Task<long> Documents(Guid policy, string category) =>
        _db.ScalarAsOwner<long>("SELECT count(*) FROM document.document WHERE business_ref_id = @p AND category_code = @c", ("p", policy), ("c", category));

    private Task AssignTemplate(string documentType) =>
        Seed("product.cfg_product_template_assignments", "EFFECTIVE", $"TA-{documentType}", ("product_code", Product), ("document_type", documentType),
            ("template", $"TPL-{documentType}"), ("language", "en"));

    private Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
        _db.SeedStatic(Tenant, table, status, code, Today.AddDays(-30), fields);
}
