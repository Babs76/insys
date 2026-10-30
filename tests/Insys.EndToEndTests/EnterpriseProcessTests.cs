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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Insys.Analytics.Contracts;
using Insys.Billing.Contracts;
using Insys.ClaimsFramework.Contracts;
using Insys.Compliance.Contracts;
using Insys.Distribution.Contracts;
using Insys.Documents.Contracts;
using Insys.Finance.Contracts;
using Insys.General.Contracts;
using Insys.General.Infrastructure;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Webhooks;
using Insys.Payments.Contracts;
using Insys.Rating.Contracts;
using Insys.Reinsurance.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.EndToEndTests;

/// <summary>
/// Phase 6 through the API, on the General motor product: the commission and reinsurance steps of "General quote to
/// cash", "General claim to settlement" and a cancellation (OQ-062, OQ-063 defaults, engineering cases pending business
/// sign-off); projections rebuilt from events; partner webhooks with retry and dead letters; renewals and the exposure
/// snapshot; and a regulatory return from a closed period.
/// </summary>
public sealed class EnterpriseProcessTests(PostgresServer server) : IAsyncLifetime
{
    private const string Product = "E2E-EMOT";
    private const string Broker = "BRK-1";
    private static readonly Guid Tenant = TestTenants.A;
    private static readonly DateOnly Today = WindowClock.Today;
    private static readonly string Month = Today.ToString("yyyy-MM");
    private static readonly Guid Assessor = Guid.CreateVersion7();

    private static readonly string[] Permissions =
    [
        GeneralPermissions.RiskRead, GeneralPermissions.RiskCreate, GeneralPermissions.QuoteRead, GeneralPermissions.QuoteCreate, GeneralPermissions.QuoteUpdate, GeneralPermissions.QuoteUnderwrite,
        GeneralPermissions.PolicyRead, GeneralPermissions.PolicyCreate, GeneralPermissions.PolicyUpdate, GeneralPermissions.PolicyCancel, GeneralPermissions.ClaimRead,
        GeneralPermissions.ClaimCreate, GeneralPermissions.ClaimUpdate, GeneralPermissions.ClaimDecide, GeneralPermissions.AssessmentUpdate,
        RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead, BillingPermissions.AccountRead, BillingPermissions.InvoiceRead, BillingPermissions.InvoiceCreate,
        PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackEndpointUpdate,
        PaymentPermissions.CommissionPayoutRead,
        ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead, ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead,
        FinancePermissions.JournalRead, FinancePermissions.JournalReverse, FinancePermissions.PeriodRead, FinancePermissions.PeriodUpdate,
        FinancePermissions.PostingExceptionRead, FinancePermissions.GlExportRead, FinancePermissions.GlExportCreate,
        FinancePermissions.GlExportUpdate, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
        IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate, IntegrationPermissions.WebhookRead, IntegrationPermissions.WebhookUpdate, DocumentPermissions.DocumentRead,
        Insys.Party.Contracts.PartyPermissions.PartyCreate, Insys.Party.Contracts.PartyPermissions.BankAccountCreate, Insys.Party.Contracts.PartyPermissions.BankAccountRead,
        Insys.Party.Contracts.PartyPermissions.BankAccountVerify, DistributionPermissions.IntermediaryRead, DistributionPermissions.CommissionRead,
        DistributionPermissions.CommissionUpdate, DistributionPermissions.StatementRead, DistributionPermissions.StatementCreate, ReinsurancePermissions.CessionRead,
        ReinsurancePermissions.RecoveryRead, ReinsurancePermissions.RecoveryUpdate, ReinsurancePermissions.BordereauRead, ReinsurancePermissions.BordereauCreate,
        ReinsurancePermissions.BordereauUpdate, AnalyticsPermissions.ProjectionRead, AnalyticsPermissions.ProjectionUpdate, CompliancePermissions.ReturnRead,
        CompliancePermissions.ReturnCreate, CompliancePermissions.ReturnUpdate, CompliancePermissions.ReturnApprove, CompliancePermissions.ReportRead,
        CompliancePermissions.ReportCreate, GeneralPermissions.EndorsementRead, GeneralPermissions.EndorsementCreate, DocumentPermissions.DocumentCreate,
    ];

    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;
    private string _reportRole = "";

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
        _watchdog = StallWatchdog.Start(_db, TimeSpan.FromSeconds(90), $"EnterpriseProcessTests.{TestContext.Current.TestMethod?.MethodName}");
        await _db.SeedTenant(Tenant, "TENA");
        var maker = await _db.UserWithPermissions(Tenant, "underwriter", Permissions);
        var checker = await _db.UserWithPermissions(Tenant, "senior-underwriter", Permissions);
        var approverRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, checker, approverRole);
        _reportRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, maker, _reportRole);
        await _db.AllowBankAccountVerification(Tenant, approverRole, Today.AddYears(-1));
        var from = Today.AddDays(-30);

        foreach (var type in new[]
                 {
                     GeneralApprovals.SettlementApprovalType, PaymentApprovals.PayoutApprovalType, ClaimsApprovals.InstructionApprovalType,
                     DistributionApprovals.StatementApprovalType, GeneralApprovals.EndorsementApprovalType,
                 })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", ("role_code", approverRole), ("transaction_type", type),
                ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        // A published General motor product, its schedule template, and its rating: sum insured × 0.03 + 50.
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'GENERAL', 'Motor', @coverages, 'GEN-MOTOR', 'MOTOR', 'ISSUE')
            """, ("t", Tenant), ("p", Product), ("from", from), ("coverages", new[] { "OWN_DAMAGE" }));
        await Seed("product.cfg_limit_and_deductible_schedules", "EFFECTIVE", "MOT-EXC", ("schedule_code", "MOT-EXC"), ("coverage", "OWN_DAMAGE"), ("limit_type", "EXCESS"),
            ("amount", 5_000m), ("percentage", 5m), ("rule", "GREATER_OF"), ("currency", "ZAR"));
        foreach (var type in new[] { "POLICY_SCHEDULE", "CERTIFICATE", "RENEWAL_NOTICE" })
        {
            await Seed("document.ref_document_categories", "ACTIVE", type, ("category_code", type), ("label", type), ("classification", "PERSONAL"), ("domain", "ALL"),
                ("allowed_roles", Array.Empty<string>()));
            await Seed("document.cfg_document_templates", "EFFECTIVE", $"TPL-{type}", ("template_code", $"TPL-{type}"), ("language", "en"), ("document_type", type),
                ("body_ref", $"doc://templates/{type}/v1"), ("merge_fields", new[] { "policy_number" }));
            if (type != "RENEWAL_NOTICE")
            {
                await Seed("product.cfg_product_template_assignments", "EFFECTIVE", $"TA-{type}", ("product_code", Product), ("document_type", type), ("template", $"TPL-{type}"),
                    ("language", "en"));
            }
        }

        await Seed("rating.cfg_rating_factor_definitions", "EFFECTIVE", "VEHICLE_GROUP", ("factor_code", "VEHICLE_GROUP"), ("data_type", "CODE"), ("source_attribute", "vehicle_group"));
        await _db.ExecuteAsOwner("""
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'EMOT-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'EMOT-BASE', ARRAY['VEHICLE_GROUP'], '[{"key":["G3"],"value":0.03}]', 'NONE')
            """, ("t", Tenant), ("from", from));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "EMOT-P1", ("parameter_code", "EMOT-BASE"), ("product_version", $"{Product} v1"), ("value", 1m), ("unit", "FACTOR"));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "EMOT-P2", ("parameter_code", "POLICY_FEE"), ("product_version", $"{Product} v1"), ("value", 50m), ("unit", "AMOUNT"));
        await Seed("rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", ("rule_code", "ZAR-2DP"), ("currency", "ZAR"), ("step", 0.01m),
            ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));

        // General: vehicles, numbering, cancellation, renewal (invite 400 days ahead; renew by itself, without re-rating), codes and an assessor panel.
        await _db.ExecuteAsOwner("""
            INSERT INTO general.cfg_risk_object_types_and_attribute_schemas (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                                             object_type, attributes, validation)
            VALUES (@t, 'VEHICLE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'VEHICLE', '["registration:String(10):Y","vehicle_group:String(5):Y"]', '[]')
            """, ("t", Tenant), ("from", from));
        await Seed("general.cfg_quote_validity_periods", "EFFECTIVE", "QV-EMOT", ("product_code", Product), ("days_valid", 30), ("re_rate_on_expiry", false));
        await Seed("general.cfg_policy_numbering_schemes", "EFFECTIVE", "GP", ("product_line", "GENERAL"), ("prefix", "GP"), ("sequence_length", 9));
        await Seed("general.cfg_cancellation_reasons_and_refund_rules", "EFFECTIVE", "INS_REQUEST", ("reason_code", "INS_REQUEST"), ("initiator", "INSURED"),
            ("refund_basis", "PRO_RATA"), ("fee", 0m));
        await Seed("general.cfg_renewal_rules", "EFFECTIVE", "RR-EMOT", ("product_code", Product), ("notice_days", 400), ("auto_renew", true), ("re_rating", false),
            ("invitation_template", "TPL-RENEWAL_NOTICE"), ("reminder_days", new[] { 30 }));
        await Seed("general.ref_risk_zones", "ACTIVE", "CPT1", ("zone_code", "CPT1"), ("geography", JsonSerializer.SerializeToElement(new { city = "Cape Town" })),
            ("peril", "ACC"));
        await Seed("general.ref_loss_cause_codes", "ACTIVE", "COLLISION", ("cause_code", "COLLISION"), ("peril", "ACC"), ("label", "Collision"));
        await Seed("general.ref_peril_codes", "ACTIVE", "ACC", ("peril_code", "ACC"), ("label", "Accident"), ("line", "MOTOR"), ("catastrophe", false));
        await Seed("general.mst_repairer_and_assessor_panels", "ACTIVE", "ASSESSOR-1", ("party", Assessor.ToString()), ("panel_type", "ASSESSOR"), ("location", "CPT"),
            ("approved_lines", new[] { "MOTOR" }), ("status", "ACTIVE"));

        // Distribution (OQ-062): an appointed broker with a 10 % premium plan, a suspended one, and one without a permission.
        await Seed("commission.cfg_commission_plans", "EFFECTIVE", "PLAN-10", ("plan_code", "PLAN-10"), ("basis", "PREMIUM"), ("rate", 10m), ("clawback_rule", "UNEARNED"));
        await Seed("commission.cfg_intermediary_product_permissions", "EFFECTIVE", "PERM-BRK1", ("intermediary", Broker), ("product", Product),
            ("transactions_allowed", Array.Empty<string>()), ("plan_code", "PLAN-10"));
        await Seed("commission.cfg_intermediary_product_permissions", "EFFECTIVE", "PERM-BRK2", ("intermediary", "BRK-2"), ("product", Product),
            ("transactions_allowed", Array.Empty<string>()), ("plan_code", "PLAN-10"));

        // Reinsurance (OQ-063): a 30 % retention quota share on the product, and its monthly bordereau layout.
        await Seed("reinsurance.cfg_treaty_definitions", "EFFECTIVE", "QS-1", ("treaty_code", "QS-1"), ("type", "QUOTA_SHARE"), ("retention", "30"), ("capacity", 5_000_000m),
            ("period_from", Today.AddYears(-1)), ("period_to", Today.AddYears(1)), ("participants", JsonSerializer.SerializeToElement(new[] { new { reinsurer = "RE-1", share = 100 } })));
        await Seed("reinsurance.cfg_cession_rules", "EFFECTIVE", "CR-1", ("rule_code", "CR-1"), ("treaty", "QS-1"), ("products", new[] { Product }),
            ("allocation_basis", "SUM_INSURED"));
        await Seed("reinsurance.cfg_bordereaux_layouts", "EFFECTIVE", "BX-QS1", ("layout_code", "BX-QS1"), ("treaty", "QS-1"),
            ("fields", new[] { "policy_ref", "ceded_sum_insured", "ceded_premium" }), ("frequency", "MONTHLY"));

        // Integration (OQ-066): partner webhooks for issued policies, one to a partner that answers 503 twice and is dead-lettered.
        await Seed("integration.cfg_retry_and_dead_letter_policies", "EFFECTIVE", "RP-2", ("policy_code", "RP-2"), ("max_attempts", 2), ("backoff", "FIXED"),
            ("initial_delay", TimeSpan.Zero), ("dead_letter_target", "OPERATOR"));
        await Seed("integration.cfg_webhook_subscriptions", "EFFECTIVE", "WH-1", ("subscription_id", "WH-1"), ("partner_code", "BROKER-SYS"),
            ("event_types", new[] { "general.GeneralPolicyIssued" }), ("endpoint_url", "https://partner.example/hooks"), ("signing_key_ref", "vault-wh1"));
        await Seed("integration.cfg_webhook_subscriptions", "EFFECTIVE", "WH-2", ("subscription_id", "WH-2"), ("partner_code", "DOWN-SYS"),
            ("event_types", new[] { "general.GeneralPolicyIssued" }), ("endpoint_url", "https://fail.example/hooks"), ("signing_key_ref", "vault-wh1"), ("retry_policy", "RP-2"));

        // Analytics and Compliance (OQ-064, OQ-065): a written premium metric, a monthly return from it and the receivables account, and a report.
        await Seed("analytics.cfg_metric_definitions", "EFFECTIVE", "GWP", ("metric_code", "GWP"), ("formula", "sum(written_premium)"),
            ("source", ProjectionSources.WrittenPremium), ("owner", "Finance"));
        await Seed("reporting.cfg_regulatory_return_definitions", "EFFECTIVE", "RET-M1", ("return_code", "RET-M1"), ("regulator", "PA"),
            ("field_mapping", JsonSerializer.SerializeToElement(new { gross_premium = "metric:GWP", receivables = "account:1100" })), ("frequency", "MONTHLY"));

        // Billing, Payments, Claims and Finance, with posting rules for commission and reinsurance.
        await Seed("billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", ("label", "Monthly"), ("periods_per_year", 12));
        await Seed("billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("payment_method", "DEBIT_ORDER"), ("lead_time", TimeSpan.FromDays(2)));
        await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "INS_REQUEST", ("label", "Cancelled at the insured's request"),
            ("requires_approval", false), ("accounting_impact", "CREDIT"));
        await Seed("payment.ref_payment_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("direction", "COLLECT"), ("currencies", new[] { "ZAR" }),
            ("countries", new[] { "ZA" }));
        await Seed("payment.ref_payment_methods", "ACTIVE", "EFT", ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await Seed("payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"),
            ("supported_methods", new[] { "DEBIT_ORDER", "EFT" }), ("credential_ref", "vault:simulator"));
        await Seed("payment.ref_payment_status_mappings", "ACTIVE", "SIM-PAID", ("provider_code", "SIMULATOR"), ("provider_status", "PAID"), ("platform_status", "SUCCEEDED"),
            ("final", true));
        await Seed("claims.ref_reserve_types", "ACTIVE", "INDEMNITY", ("category", "INDEMNITY"), ("posting_rule", "PR-CLM-UP"));
        await Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "GENERAL", ("domain", "GENERAL"), ("prefix", "GEN"), ("sequence_length", 8));
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"),
            ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        foreach (var account in new[] { "1100", "1200", "2100", "2200", "4000", "5000", "6000" })
        {
            await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", ("account_code", account), ("name", account), ("type", "ASSET"),
                ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        foreach (var (rule, type, debit, credit) in new[]
                 {
                     ("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"), ("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"),
                     ("PR-CREDIT", TransactionTypes.PremiumCredited, "4000", "1100"), ("PR-CLM-UP", TransactionTypes.ClaimReserveIncrease, "5000", "2100"),
                     ("PR-CLM-PAID", TransactionTypes.ClaimPaid, "2100", "1200"), ("PR-COM-ACC", TransactionTypes.CommissionAccrued, "6000", "2200"),
                     ("PR-COM-CLAW", TransactionTypes.CommissionClawedBack, "2200", "6000"), ("PR-COM-PAID", TransactionTypes.CommissionPaid, "2200", "1200"),
                     ("PR-CEDED", TransactionTypes.PremiumCeded, "4000", "2100"), ("PR-RI-REC", TransactionTypes.ReinsuranceRecoverable, "1100", "5000"),
                     ("PR-RI-RCVD", TransactionTypes.ReinsuranceRecovered, "1200", "1100"),
                 })
        {
            await Seed("finance.cfg_posting_rules", "EFFECTIVE", rule, ("rule_code", rule), ("transaction_type", type), ("debit_account", debit), ("credit_account", credit));
        }

        var month = new DateOnly(Today.Year, Today.Month, 1);
        await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{Month}", ("period_code", Month), ("start_date", month),
            ("end_date", month.AddMonths(1).AddDays(-1)));

        _factory = new InsysApiFactory(_db, new Dictionary<string, string?>
        {
            ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoff"] = "00:00:00", ["Integration:Webhooks:Transport"] = "stub", ["Integration:Webhooks:JobEnabled"] = "false",
            ["Integration:Secrets:vault-wh1"] = "whsec-test", ["General:Jobs:Enabled"] = "false",
        }, time: WindowClock.Time);
        _maker = _factory.ClientFor(Tenant, "underwriter");
        _checker = _factory.ClientFor(Tenant, "senior-underwriter");
        (await _maker.PostAsync($"/api/v1/finance/periods/{Month}/open", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The brokers in the register (BRK-2 suspended), BRK-1 paid to its party's verified account.
        var brokerParty = await _maker.RegisterPerson("Broker");
        await _maker.AddVerifiedBankAccount(_checker, _factory, brokerParty, "8800112233");
        foreach (var (code, status, party) in new[] { (Broker, "ACTIVE", (Guid?)brokerParty), ("BRK-2", "SUSPENDED", null), ("BRK-3", "ACTIVE", null) })
        {
            await Seed("commission.mst_intermediary_register", "ACTIVE", code, ("intermediary_code", code), ("type", "BROKER"), ("legal_name", code),
                ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", status), ("hierarchy_node", "NODE-1"),
                ("party_id", (object?)party ?? DBNull.Value));
        }
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

    [Fact]
    public async Task QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002()
    {
        // DST-001: only an appointed intermediary with a permission for the product starts new business.
        var vehicle = await Vehicle();
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/general/quotes", QuoteBody(vehicle, "BRK-3"), idempotent: true)).Code()).ShouldBe("INTERMEDIARY_NOT_PERMITTED");
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/general/quotes", QuoteBody(vehicle, "BRK-2"), idempotent: true)).Code()).ShouldBe("INTERMEDIARY_SUSPENDED");
        (await (await _maker.GetAsync($"/api/v1/distribution/intermediaries/{Broker}/permissions/{Product}")).Json()).GetProperty("permitted").GetBoolean().ShouldBeTrue();

        var policy = await IssuedPolicy(vehicle);

        // CalculateCession (REI-001, REI-002): 70 % of the 100 000 sum insured and of the 3 050 premium, with the versions it came from.
        var cession = (await (await _maker.GetAsync($"/api/v1/reinsurance/cessions?sourceDomain=GENERAL&policyRef={policy}")).Json()).GetProperty("items").EnumerateArray().Single();
        cession.GetProperty("sourceType").GetString().ShouldBe("GENERAL_POLICY");

        // BR-048: the cession is found by treaty and by the month it was calculated in.
        (await Get($"/api/v1/reinsurance/cessions?treatyCode=QS-1&limit=200")).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())
            .ShouldContain(cession.GetProperty("id").GetGuid());
        (await Get($"/api/v1/reinsurance/cessions?periodCode={Month}&limit=200")).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())
            .ShouldContain(cession.GetProperty("id").GetGuid());
        (await (await _maker.GetAsync($"/api/v1/reinsurance/cessions?sourceDomain=GENERAL")).Code()).ShouldBe("VALIDATION_FAILED");
        (cession.GetProperty("cededSumInsured").GetDecimal(), cession.GetProperty("cededPremium").GetDecimal(), cession.GetProperty("ruleCode").GetString(),
            cession.GetProperty("treatyVersion").GetInt32()).ShouldBe((70_000m, 2_135m, "CR-1", 1));
        (await Journals("CESSION", cession.GetProperty("id").GetGuid())).Count.ShouldBe(1);

        // AccrueCommission at the plan version on the first invoice (DST-002, DST-003): 10 % of 254.17.
        var invoice = await _db.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p AND document_type = 'INVOICE'", ("p", policy));
        var entitlement = (await Entitlements(policy)).Single();
        (entitlement.GetProperty("amount").GetDecimal(), entitlement.GetProperty("planCode").GetString(), entitlement.GetProperty("sourceId").GetGuid(),
            entitlement.GetProperty("status").GetString()).ShouldBe((25.42m, "PLAN-10", invoice, "Accrued"));
        var entitlementId = entitlement.GetProperty("id").GetGuid();
        (await Journals("COMMISSION", entitlementId)).Count.ShouldBe(1);

        // Nothing is payable before the premium is paid; once the invoice is paid in full the commission is payable.
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/distribution/statements", new { intermediaryCode = Broker, periodCode = Month, currency = "ZAR" }, idempotent: true))
            .Code()).ShouldBe("STATEMENT_EMPTY");
        var collection = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
        (await Callback(collection, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Entitlements(policy)).Single().GetProperty("status").GetString().ShouldBe("Payable");

        // PrepareStatement; a second person approves it in Workflow (DST-004); it is paid to the broker's verified account.
        var statement = await Created("/api/v1/distribution/statements", new { intermediaryCode = Broker, periodCode = Month, currency = "ZAR" });
        (await Get($"/api/v1/distribution/statements/{statement}")).GetProperty("netAmount").GetDecimal().ShouldBe(25.42m);
        await _factory.DrainOutbox();
        await Decide(DistributionApprovals.StatementBusinessRefType, statement);
        (await Get($"/api/v1/distribution/statements/{statement}")).GetProperty("status").GetString().ShouldBe("Approved");

        // BR-058: the statement is listed by intermediary, period and status, with the approval that approved it.
        var statementApproval = (await Get($"/api/v1/workflow/approvals?businessRefType={DistributionApprovals.StatementBusinessRefType}&businessRefId={statement}"))
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        var listedStatement = (await Get($"/api/v1/distribution/statements?intermediaryCode={Broker}&periodCode={Month}&status=Approved")).GetProperty("items")
            .EnumerateArray().Single();
        (listedStatement.GetProperty("id").GetGuid(), listedStatement.GetProperty("approvalId").GetGuid()).ShouldBe((statement, statementApproval));
        (await Get($"/api/v1/distribution/statements?intermediaryCode={Broker}&status=Draft")).GetProperty("items").GetArrayLength().ShouldBe(0);

        // BR-059: the statement's entitlements, each saying its rate is a percentage of the premium.
        var onStatement = (await Get($"/api/v1/distribution/entitlements?statementId={statement}")).EnumerateArray().Single();
        (onStatement.GetProperty("rateBasis").GetString(), onStatement.GetProperty("rateUnit").GetString()).ShouldBe(("PREMIUM", "PERCENT"));
        (await Get($"/api/v1/distribution/entitlements?statementId={Guid.CreateVersion7()}")).GetArrayLength().ShouldBe(0);
        await PayOut(PaymentPurposes.Commission, statement);
        (await Get($"/api/v1/distribution/statements/{statement}")).GetProperty("status").GetString().ShouldBe("Paid");

        // A commission payout is outside branch scope, but read only with commission_payout.read.
        var commission = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @s", ("s", statement));
        var commissionBatch = await _db.ScalarAsOwner<Guid>("SELECT payout_batch_id FROM payment.payment WHERE id = @p", ("p", commission));
        await _db.UserWithPermissions(Tenant, "payments-clerk", PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead);
        var clerk = _factory.ClientFor(Tenant, "payments-clerk");
        (await clerk.GetAsync($"/api/v1/payments/payments/{commission}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.GetAsync($"/api/v1/payments/payout-batches/{commissionBatch}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _maker.GetAsync($"/api/v1/payments/payments/{commission}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Entitlements(policy)).Single().GetProperty("status").GetString().ShouldBe("Paid");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM finance.journal j JOIN finance.subledger_entry e ON e.journal_id = j.id WHERE e.posting_rule_code = 'PR-COM-PAID'"))
            .ShouldBeGreaterThan(0L);

        // Projections from events, rebuilt from Analytics' own log to the same figures.
        var projected = (await (await _maker.GetAsync($"/api/v1/analytics/written-premium?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).Json()).EnumerateArray().Single();
        (projected.GetProperty("channelCode").GetString(), projected.GetProperty("writtenPremium").GetDecimal(), projected.GetProperty("policyCount").GetInt32())
            .ShouldBe(("INTERMEDIARY", 3_050m, 1));
        (await (await _maker.PostAsync("/api/v1/analytics/projections/rebuild", null)).Json()).GetProperty("replayed").GetInt32().ShouldBeGreaterThan(0);
        (await (await _maker.GetAsync($"/api/v1/analytics/written-premium?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).Json()).EnumerateArray().Single()
            .GetProperty("writtenPremium").GetDecimal().ShouldBe(3_050m);

        // GAP-046: the dashboard figures, per currency, from the same projections.
        var summary = await (await _maker.GetAsync($"/api/v1/analytics/summary?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).Json();
        JsonElement Figure(JsonElement s, string code) => s.GetProperty("figures").EnumerateArray().Single(f => f.GetProperty("code").GetString() == code);
        var premium = Figure(summary, "WRITTEN_PREMIUM").GetProperty("amounts").EnumerateArray().Single();
        (premium.GetProperty("currency").GetString(), premium.GetProperty("amount").GetDecimal()).ShouldBe(("ZAR", 3_050m));
        Figure(summary, "POLICIES_WRITTEN").GetProperty("count").GetInt64().ShouldBe(1L);
        summary.GetProperty("notIncluded").EnumerateArray().Select(n => n.GetString()).ShouldBe(["HEALTH"]);
        // This tenant has no organisation structure, so the policy has no node: its branch is ALL, which no branch scope covers.
        var branchUser = await _db.UserWithPermissions(Tenant, "figures-branch", AnalyticsPermissions.ProjectionRead);
        await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_branches = ARRAY['ORG-X'] WHERE user_id = @u", ("u", branchUser));
        var scoped = await (await _factory.ClientFor(Tenant, "figures-branch").GetAsync($"/api/v1/analytics/summary?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).Json();
        (Figure(scoped, "POLICIES_WRITTEN").GetProperty("count").GetInt64(), Figure(scoped, "WRITTEN_PREMIUM").GetProperty("amounts").GetArrayLength()).ShouldBe((0L, 0));

        // Partner webhooks: signed with the subscription's key; a partner that answers 503 is retried by its policy, then dead-lettered (INT-006).
        var jobs = _factory.Services.GetRequiredService<WebhookJobs>();
        var stub = _factory.Services.GetRequiredService<StubWebhookTransport>();
        await jobs.Run(CancellationToken.None);
        await jobs.Run(CancellationToken.None);
        var delivered = stub.Received.First(r => r.Url == "https://partner.example/hooks");
        delivered.Headers["X-Insys-Signature"].ShouldBe("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("whsec-test"u8.ToArray(), Encoding.UTF8.GetBytes(delivered.Body))));
        JsonDocument.Parse(delivered.Body).RootElement.GetProperty("eventType").GetString().ShouldBe("general.GeneralPolicyIssued");
        var deliveries = (await (await _maker.GetAsync("/api/v1/integration/webhook-deliveries")).Json()).EnumerateArray().ToList();
        deliveries.Single(d => d.GetProperty("subscriptionId").GetString() == "WH-1").GetProperty("status").GetString().ShouldBe("Delivered");
        var failed = deliveries.Single(d => d.GetProperty("subscriptionId").GetString() == "WH-2");
        (failed.GetProperty("status").GetString(), failed.GetProperty("attempt").GetInt32(), failed.GetProperty("httpStatus").GetInt32()).ShouldBe(("Failed", 2, 503));
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message WHERE source = 'webhook:WH-2'")).ShouldBe(1L);

        // A failed webhook is not an event envelope, so it cannot be replayed to the event consumers; it is discarded (OQ-066).
        var webhookDeadLetter = await _db.ScalarAsOwner<Guid>("SELECT id FROM integration.dead_letter_message WHERE source = 'webhook:WH-2'");
        var replay = await _maker.Send(HttpMethod.Post, $"/api/v1/integration/dead-letters/{webhookDeadLetter}/replay", ifMatch: "\"1\"");
        (await replay.Code()).ShouldBe("INVALID_STATE_TRANSITION");
        var discard = await _maker.Send(HttpMethod.Post, $"/api/v1/integration/dead-letters/{webhookDeadLetter}/discard", ifMatch: "\"1\"");
        discard.StatusCode.ShouldBe(HttpStatusCode.NoContent, await discard.Content.ReadAsStringAsync());

        // PrepareBordereau for the month (one per treaty and period), sent through the reinsurer adapter, then agreed.
        var bordereau = await Created("/api/v1/reinsurance/bordereaux", new { layoutCode = "BX-QS1", periodCode = Month });
        var prepared = await Get($"/api/v1/reinsurance/bordereaux/{bordereau}");
        (prepared.GetProperty("rowCount").GetInt32(), prepared.GetProperty("content").GetString()).ShouldBe((1, $"policy_ref,ceded_sum_insured,ceded_premium\n{policy},70000.00,2135.00\n"));
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/reinsurance/bordereaux", new { layoutCode = "BX-QS1", periodCode = Month }, idempotent: true)).Code())
            .ShouldBe("RI_BORDEREAU_EXISTS");

        // BR-047: listed by treaty, period and status, and downloaded as a named CSV file.
        var fileName = $"bordereau-qs-1-{Month}.csv";
        var listedBordereau = (await Get($"/api/v1/reinsurance/bordereaux?treatyCode=QS-1&periodCode={Month}&status=Prepared")).GetProperty("items").EnumerateArray().Single();
        (listedBordereau.GetProperty("id").GetGuid(), listedBordereau.GetProperty("fileName").GetString()).ShouldBe((bordereau, fileName));
        var file = await _maker.GetAsync($"/api/v1/reinsurance/bordereaux/{bordereau}/content");
        (file.Content.Headers.ContentType!.MediaType, file.Content.Headers.ContentDisposition!.FileName?.Trim('"')).ShouldBe(("text/csv", fileName));
        (await file.Content.ReadAsStringAsync()).ShouldBe(prepared.GetProperty("content").GetString());
        (await (await _maker.PostAsync($"/api/v1/reinsurance/bordereaux/{bordereau}/send", null)).Json()).GetProperty("status").GetString().ShouldBe("Sent");
        (await (await _maker.PostAsync($"/api/v1/reinsurance/bordereaux/{bordereau}/agree", null)).Json()).GetProperty("status").GetString().ShouldBe("Agreed");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.external_call_log WHERE operation = 'reinsurance.SendBordereau' AND partner_code = 'RE-1'"))
            .ShouldBe(1L);

        // The broker portal (external-user access §2), last as its channel then applies to staff too (F005): the broker sees
        // its paid statement without the approval or bank account.
        var brokerUser = await _db.UserWithPermissions(Tenant, "broker-1", DistributionPermissions.StatementRead);
        await _db.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, intermediary_code, status,
                                               requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'INTERMEDIARY', @b, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", Tenant), ("u", brokerUser), ("b", Broker));
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-BRK", ("channel_code", "BRK"), ("audience", "BROKER"), ("name", "Broker portal"),
            ("client_ids", new[] { "broker-portal" }));
        var brokerPortal = _factory.ClientFor(Tenant, "broker-1", "broker-portal", multiFactor: true);
        var statements = await brokerPortal.GetAsync("/api/v1/portal/distribution/statements");
        statements.StatusCode.ShouldBe(HttpStatusCode.OK, await statements.Content.ReadAsStringAsync());
        (await statements.Json()).EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ShouldContain(statement);
        var portalStatement = await (await brokerPortal.GetAsync($"/api/v1/portal/distribution/statements/{statement}")).Json();
        (portalStatement.GetProperty("status").GetString(), portalStatement.TryGetProperty("bankAccountId", out _)).ShouldBe(("Paid", false));
    }

    /// <summary>BR-117: with channels in force, a broker of record change takes its appointment letter and is submitted: the letter is part of drafting, under its function.</summary>
    [Fact]
    public async Task ABrokerOfRecordChange_TakesItsLetterAndIsSubmitted_InAChannelEnabledTenant_BR117()
    {
        await Seed("general.cfg_endorsement_types", "EFFECTIVE", "INTERMEDIARY", ("premium_method", "NONE"), ("documents", Array.Empty<string>()),
            ("requires_underwriting", false));
        await Seed("document.ref_document_categories", "ACTIVE", "APPOINTMENT", ("category_code", "APPOINTMENT"), ("label", "Broker appointment"),
            ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await Seed("commission.mst_intermediary_register", "ACTIVE", "BRK-4", ("intermediary_code", "BRK-4"), ("type", "BROKER"), ("legal_name", "BRK-4"),
            ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-4" })), ("status", "ACTIVE"), ("hierarchy_node", "NODE-BRK-4"));
        await Seed("commission.cfg_intermediary_product_permissions", "EFFECTIVE", "PERM-BRK4", ("intermediary", "BRK-4"), ("product", Product),
            ("transactions_allowed", Array.Empty<string>()), ("plan_code", "PLAN-10"));
        var policy = await IssuedPolicy(await Vehicle());
        var draft = await _maker.Send(HttpMethod.Post, $"/api/v1/general/policies/{policy}/endorsements",
            new { endorsementType = "INTERMEDIARY", effectiveDate = Today.AddDays(10), changes = Array.Empty<object>(), intermediaryCode = "BRK-4" }, idempotent: true);
        draft.StatusCode.ShouldBe(HttpStatusCode.Created, await draft.Content.ReadAsStringAsync());
        var endorsement = (await draft.Json()).GetProperty("id").GetGuid();
        var letter = await _maker.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "APPOINTMENT", businessRefType = GeneralDocuments.EndorsementRefType, businessRefId = endorsement, mimeType = "text/plain",
            contentBase64 = Convert.ToBase64String("I appoint BRK-4"u8.ToArray()),
        }, idempotent: true);
        letter.StatusCode.ShouldBe(HttpStatusCode.Created, await letter.Content.ReadAsStringAsync());

        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-STF", ("channel_code", "STF"), ("audience", "STAFF"), ("name", "Back office"), ("client_ids", new[] { "insys-web" }));
        var roles = (await _db.ScalarAsOwner<string[]>("""
            SELECT array_agg(DISTINCT a.role_code) FROM iam.user_role_assignment a JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id WHERE u.external_subject = 'underwriter'
            """))!;
        foreach (var (role, i) in roles.Select((r, i) => (r, i)))
        {
            await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", $"CP-STF-D{i}", ("channel_code", "STF"), ("role_code", role),
                ("transaction_type", GeneralPermissions.EndorsementCreate), ("allowed", true));
        }

        await _db.AdoptFunctionCatalogue();
        var staff = _factory.ClientFor(Tenant, "underwriter", "insys-web");
        var url = $"/api/v1/general/endorsements/{endorsement}";
        var recorded = await staff.Send(HttpMethod.Post, $"{url}/appointment-letter", new { documentId = (await letter.Json()).GetProperty("id").GetGuid() }, ifMatch: (await staff.GetAsync(url)).ETag());
        recorded.StatusCode.ShouldBe(HttpStatusCode.OK, await recorded.Content.ReadAsStringAsync());
        var submitted = await staff.Send(HttpMethod.Post, $"{url}/submit", ifMatch: (await staff.GetAsync(url)).ETag());
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cancellation_ClawsBackUnearnedCommission_AndASettledClaimRecoversTheCededShare_REI003()
    {
        // ClawBack (process "Missed premium to lapse" and GeneralPolicyCancelled): the commission on the invoice due from the cancellation date.
        var cancelled = await IssuedPolicy(await Vehicle());
        var entitlement = (await Entitlements(cancelled)).Single();
        (await _maker.PostAsJsonAsync($"/api/v1/general/policies/{cancelled}/cancel", new { reasonCode = "INS_REQUEST", effectiveDate = Today })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var clawedBack = (await Entitlements(cancelled)).Single();
        (clawedBack.GetProperty("status").GetString(), clawedBack.GetProperty("clawback").GetProperty("amount").GetDecimal(),
            clawedBack.GetProperty("clawback").GetProperty("reason").GetString()).ShouldBe(("ClawedBack", 25.42m, "POLICY_CANCELLED"));
        (await Journals("COMMISSION", entitlement.GetProperty("id").GetGuid())).Count.ShouldBe(2);

        // A claim on a ceded policy is settled; IdentifyRecovery and BillRecovery: 70 % of the 35 000 paid (REI-003).
        var vehicle = await Vehicle();
        var policy = await IssuedPolicy(vehicle);
        var claim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC",
        });
        var number = (await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json()).GetProperty("claimNumber").GetString()!;
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 50_000m, currency = "ZAR", reason = "Assessed" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Assessor })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var assessment = (await (await _maker.GetAsync($"/api/v1/general/claims/{claim}/assessments")).Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{assessment}/report", new { assessedAmount = 40_000m })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payee = await _maker.RegisterPerson("Insured");
        var account = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "62 0009 8765");
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/settlement", new
        {
            payeePartyId = payee, bankAccountId = account, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Assessed and accepted",
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.SettlementBusinessRefType, claim);
        var instruction = await _db.ScalarAsOwner<Guid>("SELECT id FROM claims.claim_payment_instruction WHERE claim_number = @n", ("n", number));
        await PayOut(PaymentPurposes.ClaimPayout, instruction);
        (await Get($"/api/v1/general/claims/{claim}")).GetProperty("status").GetString().ShouldBe("Settled");

        var recovery = (await (await _maker.GetAsync($"/api/v1/reinsurance/recoveries?claimNumber={number}")).Json()).GetProperty("items").EnumerateArray().Single();
        recovery.GetProperty("billedBy").ValueKind.ShouldBe(JsonValueKind.Null, "billed automatically when identified");
        (recovery.GetProperty("treatyCode").GetString(), recovery.GetProperty("paidAmount").GetDecimal(), recovery.GetProperty("recoverable").GetDecimal(),
            recovery.GetProperty("status").GetString(), recovery.GetProperty("policyRef").GetGuid()).ShouldBe(("QS-1", 35_000m, 24_500m, "Billed", policy));

        // SettleRecovery: the reinsurer pays short; the difference stays for reconciliation; both are posted.
        var recoveryId = recovery.GetProperty("id").GetGuid();
        var settled = await (await _maker.PostAsJsonAsync($"/api/v1/reinsurance/recoveries/{recoveryId}/settle", new { amount = 24_000m, settledOn = Today })).Json();
        (settled.GetProperty("status").GetString(), settled.GetProperty("difference").GetDecimal()).ShouldBe(("Settled", -500m));
        // BR-049: who recorded the settlement.
        settled.GetProperty("settledBy").GetGuid()
            .ShouldBe(await _db.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'underwriter'", ("t", Tenant)));
        await _factory.DrainOutbox();
        (await Journals("RI_RECOVERY", recoveryId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task RenewalJob_InvitesThenRenewsByItself_AndTheExposureSnapshotSumsByZoneAndPeril_AndTheRenewalIsBilled_GEN004_F023()
    {
        var vehicle = await Vehicle(riskZone: "CPT1");
        var policy = await IssuedPolicy(vehicle);
        var jobs = _factory.Services.GetRequiredService<GeneralJobs>();

        // The invitation goes out the rule's notice days before the period ends, once, from its template.
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM document.document WHERE business_ref_id = @p AND category_code = 'RENEWAL_NOTICE'", ("p", policy)))
            .ShouldBe(1L);

        // The exposure snapshot: the policy's 100 000 in zone CPT1 for its peril.
        await jobs.RefreshExposure(CancellationToken.None);
        (await _db.ScalarAsOwner<decimal>("SELECT sum_insured FROM general.exposure_snapshot WHERE as_at = @d AND zone = 'CPT1' AND peril_code = 'ACC'", ("d", Today)))
            .ShouldBe(100_000m);

        // At the period end, the rule renews it by itself (no re-rating), and the policy is Renewed.
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @d WHERE id = @p", ("d", Today), ("p", policy));
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        (await Get($"/api/v1/general/policies/{policy}")).GetProperty("status").GetString().ShouldBe("Renewed");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM general.policy WHERE renewal_of_id = @p", ("p", policy))).ShouldBe(1L);

        // F023: the renewal is billed. It is issued as a new policy, so Billing schedules its premium on the policy's billing
        // account, and invoices each instalment when it is due: the first on the renewal's first day.
        var renewal = await _db.ScalarAsOwner<Guid>("SELECT id FROM general.policy WHERE renewal_of_id = @p", ("p", policy));
        var account = await _db.ScalarAsOwner<Guid>("SELECT billing_account_id FROM billing.invoice WHERE policy_ref = @p LIMIT 1", ("p", policy));
        (await _db.ScalarAsOwner<Guid>("SELECT billing_account_id FROM billing.premium_schedule WHERE policy_ref = @r", ("r", renewal))).ShouldBe(account);
        var renewalStart = await _db.ScalarAsOwner<DateOnly>("SELECT period_start FROM general.policy WHERE id = @r", ("r", renewal));
        var invoiced = await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/invoices", new { asOf = renewalStart }, idempotent: true);
        invoiced.StatusCode.ShouldBe(HttpStatusCode.OK, await invoiced.Content.ReadAsStringAsync());
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM billing.invoice WHERE policy_ref = @r AND document_type = 'INVOICE'", ("r", renewal)))
            .ShouldBe(1L);
    }

    /// <summary>
    /// F021 and F100 (addenda/communications.md §6.2, OQ-089): the renewal invitation reaches the policyholder by email, and
    /// on the rule's reminder day a reminder follows, once, however often the job runs that day.
    /// </summary>
    [Fact]
    public async Task RenewalInvitation_AndItsReminder_ReachThePolicyholder_OnceEach_F021_F100()
    {
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "EPT",
            (Insys.Communications.Contracts.NotificationTypes.RenewalInvitation, "Renew {policy_number}", "{policy_number} ends on {period_end}."),
            (Insys.Communications.Contracts.NotificationTypes.RenewalReminder, "Reminder {policy_number}", "{policy_number} ends in {days_to_renewal} days."));
        var policy = await IssuedPolicy(await Vehicle());
        var holder = await _maker.RegisterPerson("Renewing");
        await _db.AddContact(Tenant, holder, "EMAIL", "renewal@example.com");
        var periodEnd = Today.AddDays(30);
        await _db.ExecuteAsOwner("UPDATE general.policy SET period_end = @d, policyholder_party_id = @h WHERE id = @p", ("d", periodEnd), ("h", holder),
            ("p", policy));
        var jobs = _factory.Services.GetRequiredService<GeneralJobs>();
        var sender = _factory.Services.GetRequiredService<Insys.Communications.Infrastructure.NotificationSender>();
        for (var run = 0; run < 3; run++)
        {
            await jobs.Run(CancellationToken.None);
            await _factory.DrainOutbox();
        }

        await sender.RunForTenant(Tenant, CancellationToken.None);
        var number = (await Get($"/api/v1/general/policies/{policy}")).GetProperty("policyNumber").GetString();
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(type_code || ':' || status || ':' || body, '|' ORDER BY type_code) FROM communication.notification WHERE recipient_party_id = @h
            """, ("h", holder))).ShouldBe(
            $"RENEWAL_INVITATION:SENT:{number} ends on {periodEnd:yyyy-MM-dd}.|RENEWAL_REMINDER:SENT:{number} ends in 30 days.");
    }

    /// <summary>
    /// F055 (§15.1): the posted entries of a range go to the general ledger summarised by date, account and currency, through
    /// the GL adapter; the batch is balanced, cannot be exported twice, and the GL's answer is recorded.
    /// </summary>
    [Fact]
    public async Task PostedEntries_AreExportedToTheGeneralLedger_Summarised_Balanced_AndOnlyOnce_F055()
    {
        await IssuedPolicy(await Vehicle());
        await _factory.DrainOutbox();
        await Seed("finance.cfg_gl_export_layouts", "EFFECTIVE", "GL-EXP-01", ("layout_code", "GL-EXP-01"), ("target_system", "Insurer ERP"), ("format", "CSV"),
            ("schedule", "DAILY"));

        var exported = await _maker.Send(HttpMethod.Post, "/api/v1/finance/gl-exports",
            new { layoutCode = "GL-EXP-01", periodCode = Month, fromDate = Today, toDate = Today }, idempotent: true);
        exported.StatusCode.ShouldBe(HttpStatusCode.Created, await exported.Content.ReadAsStringAsync());
        var batch = await exported.Json();
        batch.GetProperty("status").GetString().ShouldBe("Sent");
        var id = batch.GetProperty("id").GetGuid();
        var lines = batch.GetProperty("lineCount").GetInt32();
        lines.ShouldBeGreaterThan(0);

        // The content: a header and one line per date, account and currency; debits equal credits.
        var csv = (await (await _maker.GetAsync($"/api/v1/finance/gl-exports/{id}/content")).Content.ReadAsStringAsync()).TrimEnd('\n').Split('\n');
        csv[0].ShouldBe("accounting_date,account_code,currency,debit,credit,entries");
        csv.Length.ShouldBe(lines + 1);
        var rows = csv.Skip(1).Select(l => l.Split(',')).ToList();
        rows.Sum(r => decimal.Parse(r[3], System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(rows.Sum(r => decimal.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture)));
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.external_call_log WHERE operation = 'finance.SendGlExport'")).ShouldBe(1L);

        // Never twice; the GL's acknowledgement is recorded.
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/finance/gl-exports",
            new { layoutCode = "GL-EXP-01", periodCode = Month, fromDate = Today, toDate = Today }, idempotent: true)).Code()).ShouldBe("GL_EXPORT_OVERLAPS");
        (await _maker.Send(HttpMethod.Post, $"/api/v1/finance/gl-exports/{id}/acknowledgement", new { accepted = true }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Get($"/api/v1/finance/gl-exports/{id}")).GetProperty("status").GetString().ShouldBe("Acknowledged");
    }

    /// <summary>
    /// BR-041, BR-043 to BR-046: journals are found by period, number and accounting dates without a business record, and
    /// name the journal that reverses them and their base currency; a GL export says when and by whom it was sent, and the
    /// contract declares its ETag; a period reads alone, the calendar's periods not yet opened read as Future, and a reopen
    /// gives when it was asked for and its approval; a posting exception reads alone with its event's amount, date and record.
    /// </summary>
    [Fact]
    public async Task TheLedger_IsFoundAndExplained_BR041_BR043_BR044_BR045_BR046()
    {
        await IssuedPolicy(await Vehicle());
        await _factory.DrainOutbox();

        // BR-043: by period, a page at a time; then by number and by accounting dates.
        var inPeriod = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await Get($"/api/v1/finance/journals?periodCode={Month}&limit=1{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}");
            inPeriod.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        inPeriod.ShouldNotBeEmpty();
        inPeriod.Select(j => j.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(inPeriod.Count);
        var journal = inPeriod[0];
        var number = journal.GetProperty("journalNumber").GetString();
        (await Get($"/api/v1/finance/journals?journalNumber={number}")).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid()
            .ShouldBe(journal.GetProperty("id").GetGuid());
        (await Get($"/api/v1/finance/journals?accountingFrom={Today:yyyy-MM-dd}&accountingTo={Today:yyyy-MM-dd}&limit=200")).GetProperty("items").GetArrayLength()
            .ShouldBe(inPeriod.Count(j => j.GetProperty("accountingDate").GetString() == $"{Today:yyyy-MM-dd}"));
        (journal.GetProperty("baseCurrency").GetString(), journal.GetProperty("reversedByJournalId").ValueKind).ShouldBe(("ZAR", JsonValueKind.Null));

        // Reversed, the journal names its reversal.
        var reversed = await _maker.Send(HttpMethod.Post, $"/api/v1/finance/journals/{journal.GetProperty("id").GetGuid()}/reverse",
            new { accountingDate = Today, reason = "Posted in error" }, idempotent: true);
        reversed.StatusCode.ShouldBe(HttpStatusCode.Created, await reversed.Content.ReadAsStringAsync());
        (await Get($"/api/v1/finance/journals/{journal.GetProperty("id").GetGuid()}")).GetProperty("reversedByJournalId").GetGuid()
            .ShouldBe((await reversed.Json()).GetProperty("id").GetGuid());

        // V0089: the ledger is partitioned on accounting_date. A period opened 20 months ahead, past the partitions maintenance
        // creates, gets its partitions when it opens, so a reversal dated in it posts.
        var far = new DateOnly(Today.Year, Today.Month, 1).AddMonths(20);
        await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{far:yyyy-MM}", ("period_code", $"{far:yyyy-MM}"), ("start_date", far),
            ("end_date", far.AddMonths(1).AddDays(-1)));
        (await _db.ScalarAsOwner<bool>($"SELECT to_regclass('finance.journal_{far:yyyy_MM}') IS NULL")).ShouldBeTrue("not yet created");
        (await _maker.PostAsync($"/api/v1/finance/periods/{far:yyyy-MM}/open", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _db.ScalarAsOwner<bool>($"SELECT to_regclass('finance.subledger_entry_{far:yyyy_MM}') IS NOT NULL")).ShouldBeTrue();
        var farReversal = await _maker.Send(HttpMethod.Post, $"/api/v1/finance/journals/{inPeriod.First(j => j.GetProperty("id").GetGuid() != journal.GetProperty("id").GetGuid()).GetProperty("id").GetGuid()}/reverse",
            new { accountingDate = far.AddDays(14), reason = "Corrected in a later period" }, idempotent: true);
        farReversal.StatusCode.ShouldBe(HttpStatusCode.Created, await farReversal.Content.ReadAsStringAsync());
        (await farReversal.Json()).GetProperty("accountingDate").GetString().ShouldBe($"{far.AddDays(14):yyyy-MM-dd}");

        // BR-046, BR-041: who sent the GL export and when; the contract declares the ETag the read gives.
        await Seed("finance.cfg_gl_export_layouts", "EFFECTIVE", "GL-EXP-02", ("layout_code", "GL-EXP-02"), ("target_system", "Insurer ERP"), ("format", "CSV"),
            ("schedule", "DAILY"));
        var exported = await _maker.Send(HttpMethod.Post, "/api/v1/finance/gl-exports",
            new { layoutCode = "GL-EXP-02", periodCode = Month, fromDate = Today, toDate = Today }, idempotent: true);
        exported.StatusCode.ShouldBe(HttpStatusCode.Created, await exported.Content.ReadAsStringAsync());
        var underwriter = await _db.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'underwriter'", ("t", Tenant));
        var export = await Get($"/api/v1/finance/gl-exports/{(await exported.Json()).GetProperty("id").GetGuid()}");
        (export.GetProperty("sentBy").GetGuid(), export.GetProperty("sentAt").ValueKind).ShouldBe((underwriter, JsonValueKind.String));
        using (var contract = JsonDocument.Parse(await (await _maker.GetAsync("/openapi/core.json")).Content.ReadAsStringAsync()))
        {
            contract.RootElement.GetProperty("paths").GetProperty("/api/v1/finance/gl-exports/{id}").GetProperty("get").GetProperty("responses").GetProperty("200")
                .GetProperty("headers").TryGetProperty("ETag", out _).ShouldBeTrue();
        }

        // BR-045: the period reads alone; next month, only in the calendar, is Future; a reopen says when it was asked for and its approval.
        var next = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);
        await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{next:yyyy-MM}", ("period_code", $"{next:yyyy-MM}"), ("start_date", next),
            ("end_date", next.AddMonths(1).AddDays(-1)));
        (await Get($"/api/v1/finance/periods/{Month}")).GetProperty("status").GetString().ShouldBe("Open");
        var future = await Get($"/api/v1/finance/periods/{next:yyyy-MM}");
        (future.GetProperty("status").GetString(), future.GetProperty("id").ValueKind).ShouldBe(("Future", JsonValueKind.Null));
        (await Get("/api/v1/finance/periods")).EnumerateArray().Select(p => (p.GetProperty("periodCode").GetString(), p.GetProperty("status").GetString()))
            .ShouldContain(($"{next:yyyy-MM}", "Future"));
        (await _maker.GetAsync("/api/v1/finance/periods/1999-01")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // BR-044: a posting exception reads alone, with its event's amount, accounting date and business record; the list pages.
        var failed = Guid.CreateVersion7();
        var record = Guid.CreateVersion7();
        await _db.ExecuteAsOwner("""
            INSERT INTO finance.posting_exception (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, source_event_id, event_type, error, status,
                                                   business_event)
            VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', gen_random_uuid(), 'billing.InvoiceIssued', 'POSTING_RULE_NOT_FOUND: none', 'OPEN', @event::jsonb)
            """, ("t", Tenant), ("id", failed), ("event", JsonSerializer.Serialize(new
        {
            sourceEventId = Guid.CreateVersion7(), sourceDomain = "BILLING", transactionType = "PREMIUM_DUE", accountingDate = Today, transactionDate = Today,
            amount = 254.17m, currency = "ZAR", businessRefType = "INVOICE", businessRefId = record, variables = new { },
        })));
        var exception = await Get($"/api/v1/finance/posting-exceptions/{failed}");
        (exception.GetProperty("amount").GetDecimal(), exception.GetProperty("currency").GetString(), exception.GetProperty("accountingDate").GetString(),
            exception.GetProperty("businessRefType").GetString(), exception.GetProperty("businessRefId").GetGuid()).ShouldBe((254.17m, "ZAR", $"{Today:yyyy-MM-dd}", "INVOICE", record));
        (await Get("/api/v1/finance/posting-exceptions?status=OPEN&limit=1")).GetProperty("items").GetArrayLength().ShouldBe(1);

        // The reopen, last: soft-closing the period stops its postings.
        (await _maker.PostAsync($"/api/v1/finance/periods/{Month}/soft-close", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/finance/periods/{Month}/reopen", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var reopening = await Get($"/api/v1/finance/periods/{Month}");
        var waiting = (await Get($"/api/v1/workflow/approvals?status=Requested&businessRefType={FinanceApprovals.PeriodBusinessRefType}&businessRefId={reopening.GetProperty("id").GetGuid()}"))
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (reopening.GetProperty("reopenRequestedAt").ValueKind, reopening.GetProperty("reopenApprovalId").GetGuid()).ShouldBe((JsonValueKind.String, waiting));
    }

    [Fact]
    public async Task ClosedPeriod_PreparesItsReturn_ApprovedByAnotherOfficerBeforeSubmission_AndReportsFollowTheCatalogue()
    {
        await IssuedPolicy(await Vehicle());
        await Seed("reporting.cfg_report_catalogue_and_access_rules", "EFFECTIVE", "RPT-GWP", ("report_code", "RPT-GWP"), ("name", "Written premium"),
            ("roles", new[] { _reportRole }), ("export_allowed", false));

        // Reports: run by the catalogue's roles, not exported where the catalogue says so.
        var run = await Created("/api/v1/compliance/report-runs", new { reportCode = "RPT-GWP", from = Today, to = Today, metrics = new[] { "GWP" }, export = false });
        (await Get($"/api/v1/compliance/report-runs/{run}")).GetProperty("result").GetProperty("GWP").GetDecimal().ShouldBe(3_050m);

        // GAP-048: a run needs an Idempotency-Key, and a retry with the same key gives the same run, not a second one.
        var runBody = new { reportCode = "RPT-GWP", from = Today, to = Today, metrics = new[] { "GWP" }, export = false };
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/compliance/report-runs", runBody)).Code()).ShouldBe("IDEMPOTENCY_KEY_REQUIRED");
        var key = Guid.CreateVersion7().ToString();
        var first = await (await _maker.SendAsync(Keyed("/api/v1/compliance/report-runs", runBody, key))).Json();
        var retried = await (await _maker.SendAsync(Keyed("/api/v1/compliance/report-runs", runBody, key))).Json();
        retried.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());

        // GAP-047: runs are listed newest first, and a run made with export gives its CSV file; one made without does not.
        await Seed("reporting.cfg_report_catalogue_and_access_rules", "EFFECTIVE", "RPT-GWPX", ("report_code", "RPT-GWPX"), ("name", "Written premium (export)"),
            ("roles", new[] { _reportRole }), ("export_allowed", true));
        var exported = await Created("/api/v1/compliance/report-runs", new { reportCode = "RPT-GWPX", from = Today, to = Today, metrics = new[] { "GWP" }, export = true });
        var runs = (await Get("/api/v1/compliance/report-runs?limit=10")).EnumerateArray().ToList();
        runs[0].GetProperty("id").GetGuid().ShouldBe(exported);
        runs.Select(r => r.GetProperty("id").GetGuid()).ShouldContain(run);
        (await Get("/api/v1/compliance/report-runs?reportCode=RPT-GWPX")).EnumerateArray().Single().GetProperty("exported").GetBoolean().ShouldBeTrue();
        var file = await _maker.GetAsync($"/api/v1/compliance/report-runs/{exported}/export");
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        file.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe($"RPT-GWPX-{exported:N}.csv");
        (await file.Content.ReadAsStringAsync()).ShouldBe($"report_code,from,to,metric,total\r\nRPT-GWPX,{Today:yyyy-MM-dd},{Today:yyyy-MM-dd},GWP,3050.00\r\n");
        (await (await _maker.GetAsync($"/api/v1/compliance/report-runs/{run}/export")).Code()).ShouldBe("REPORT_NOT_EXPORTED");
        // Each download is audited once; one the catalogue doesn't allow your roles is DENIED.
        (await (await _checker.GetAsync($"/api/v1/compliance/report-runs/{exported}/export")).Code()).ShouldBe("REPORT_NOT_ALLOWED");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(concat_ws('|', outcome, coalesce(details->>'refused', '-')), ',' ORDER BY occurred_at)
            FROM audit.audit_event WHERE tenant_id = @t AND operation = 'DownloadReportExport' AND target_id = @r
            """, ("t", Tenant), ("r", exported.ToString()))).ShouldBe("SUCCESS|-,DENIED|REPORT_NOT_ALLOWED");
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/compliance/report-runs", new { reportCode = "RPT-GWP", from = Today, to = Today, metrics = new[] { "GWP" }, export = true },
            idempotent: true)).Code()).ShouldBe("REPORT_NOT_ALLOWED");
        (await (await _checker.Send(HttpMethod.Post, "/api/v1/compliance/report-runs", new { reportCode = "RPT-GWP", from = Today, to = Today, metrics = new[] { "GWP" }, export = false },
            idempotent: true)).Code()).ShouldBe("REPORT_NOT_ALLOWED");

        // AccountingPeriodClosed (final) → PrepareReturn from the metric and the ledger.
        (await _maker.PostAsync($"/api/v1/finance/periods/{Month}/soft-close", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/finance/periods/{Month}/close", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var prepared = (await (await _maker.GetAsync($"/api/v1/compliance/returns?periodCode={Month}")).Json()).EnumerateArray().Single();

        // GAP-046: the return counts by status and as awaiting action; it has no deadline yet, so it is never overdue (OQ-083).
        var returns = await (await _maker.GetAsync($"/api/v1/compliance/returns/summary?fromPeriod={Month}&toPeriod={Month}")).Json();
        JsonElement ReturnFigure(string code) => returns.GetProperty("figures").EnumerateArray().Single(f => f.GetProperty("code").GetString() == code);
        ReturnFigure("RETURNS_BY_STATUS").GetProperty("by").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "Prepared").GetProperty("count").GetInt64()
            .ShouldBe(1L);
        ReturnFigure("RETURNS_AWAITING_ACTION").GetProperty("count").GetInt64().ShouldBe(1L);
        var overdue = ReturnFigure("RETURNS_OVERDUE");
        (overdue.GetProperty("count").GetInt64(), overdue.GetProperty("by").EnumerateArray().Single().GetProperty("key").GetString()!).ShouldBe((0L, $"NO_DEADLINE:{prepared.GetProperty("returnCode").GetString()}"));
        prepared.GetProperty("fieldValues").GetProperty("gross_premium").GetDecimal().ShouldBe(3_050m);
        prepared.GetProperty("fieldValues").GetProperty("receivables").GetDecimal().ShouldBe(254.17m);
        var id = prepared.GetProperty("id").GetGuid();

        // GAP-048: return actions need the ETag from a read (If-Match); a stale one changes nothing.
        var read = await _maker.GetAsync($"/api/v1/compliance/returns/{id}");
        var etag = read.Headers.ETag!.Tag;
        etag.ShouldBe($"\"{(await read.Json()).GetProperty("rowVersion").GetInt64()}\"");
        (await (await _maker.PostAsync($"/api/v1/compliance/returns/{id}/review", null)).Code()).ShouldBe("PRECONDITION_REQUIRED");
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/compliance/returns/{id}/review", ifMatch: "\"99\"")).Code()).ShouldBe("PRECONDITION_FAILED");

        // Reviewed, then approved by a different compliance officer, then submitted through the regulator adapter and accepted.
        (await (await ReturnAction(_maker, id, "submit")).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        var reviewed = await ReturnAction(_maker, id, "review");
        reviewed.StatusCode.ShouldBe(HttpStatusCode.OK);
        reviewed.Headers.ETag!.Tag.ShouldBe((await _maker.GetAsync($"/api/v1/compliance/returns/{id}")).Headers.ETag!.Tag, "the action answers with the new ETag");
        (await (await ReturnAction(_maker, id, "approve")).Code()).ShouldBe("RETURN_SAME_PERSON");
        (await ReturnAction(_checker, id, "approve")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var submitted = await (await ReturnAction(_maker, id, "submit")).Json();
        submitted.GetProperty("status").GetString().ShouldBe("Submitted");
        submitted.GetProperty("regulatorRef").GetString()!.ShouldStartWith("STUB-");
        (await (await ReturnAction(_maker, id, "accept")).Json()).GetProperty("status").GetString().ShouldBe("Accepted");
    }

    /// <summary>A return action with the ETag just read (GAP-048).</summary>
    private static async Task<HttpResponseMessage> ReturnAction(HttpClient client, Guid id, string action)
    {
        var etag = (await client.GetAsync($"/api/v1/compliance/returns/{id}")).Headers.ETag!.Tag;
        return await client.Send(HttpMethod.Post, $"/api/v1/compliance/returns/{id}/{action}", ifMatch: etag);
    }

    private static HttpRequestMessage Keyed(string url, object body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private object QuoteBody(Guid vehicle, string? intermediary) => new
    {
        productCode = Product, proposerPartyId = Guid.CreateVersion7(), intermediaryCode = intermediary, currency = "ZAR", periodStart = Today,
        periodEnd = Today.AddYears(1).AddDays(-1),
        risks = new[]
        {
            new { riskObjectId = vehicle, sumInsured = 100_000m, coverages = new object[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m, excessScheduleCode = "MOT-EXC" } } },
        },
    };

    private Task<Guid> Vehicle(string? riskZone = null) =>
        Created("/api/v1/general/risk-objects", new
        {
            objectType = "VEHICLE", attributes = new { registration = "CA" + Random.Shared.Next(100_000, 999_999), vehicle_group = "G3" }, riskZone,
        });

    /// <summary>A quote through BRK-1, rated, issued, accepted and proposed; the policy issued from it, its events relayed.</summary>
    /// <summary>
    /// B-001 (Wave 4 §4.8; OQ-134 to OQ-136; addition 3): a broker of record change is an INTERMEDIARY endorsement. It is
    /// refused at submission until the policyholder's appointment letter is filed on it, and a second person approves it.
    /// Future-dated, the policy stays with its broker (only it sees the policy) until the date, when the daily job moves it.
    /// The commission on the invoice whose cover runs over the date splits pro rata: the outgoing broker keeps the days
    /// before, and the incoming broker accrues the days from it on its own plan.
    /// </summary>
    [Fact]
    public async Task ABrokerOfRecordChange_NeedsTheAppointmentLetterAndASecondPerson_MovesAtItsDate_AndSplitsCommissionProRata_B001()
    {
        await Seed("general.cfg_endorsement_types", "EFFECTIVE", "INTERMEDIARY", ("premium_method", "NONE"), ("documents", Array.Empty<string>()),
            ("requires_underwriting", false));
        await Seed("document.ref_document_categories", "ACTIVE", "APPOINTMENT", ("category_code", "APPOINTMENT"), ("label", "Broker appointment"),
            ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "BRK",
            (Insys.Communications.Contracts.NotificationTypes.BrokerChangeNotice, "Broker change on {policy_number}",
                "{policy_number} ({broker_role}): {outgoing_intermediary} to {incoming_intermediary} from {effective_date}."));
        var incomingContact = await _maker.RegisterPerson("Incoming Contact");
        await _db.AddContact(Tenant, incomingContact, "EMAIL", "incoming@broker.test");
        await Seed("commission.mst_intermediary_register", "ACTIVE", "BRK-4", ("intermediary_code", "BRK-4"), ("type", "BROKER"), ("legal_name", "BRK-4"),
            ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-4" })), ("status", "ACTIVE"), ("hierarchy_node", "NODE-BRK-4"),
            ("contact_party_id", incomingContact));
        await Seed("commission.cfg_intermediary_product_permissions", "EFFECTIVE", "PERM-BRK4", ("intermediary", "BRK-4"), ("product", Product),
            ("transactions_allowed", Array.Empty<string>()), ("plan_code", "PLAN-10"));
        var policy = await IssuedPolicy(await Vehicle());
        var outgoing = (await Entitlements(policy)).Single();
        var invoice = outgoing.GetProperty("sourceId").GetGuid();
        var basis = outgoing.GetProperty("basisAmount").GetDecimal();
        // The first instalment's cover started 15 days ago, so the change today falls in the middle of it.
        var due = Today.AddDays(-15);
        await _db.ExecuteAsOwner("UPDATE commission.commission_entitlement SET due_date = @d WHERE id = @e", ("d", due), ("e", outgoing.GetProperty("id").GetGuid()));

        // Drafted, rated at once (no premium change); refused at submission without the appointment letter.
        var draft = await _maker.Send(HttpMethod.Post, $"/api/v1/general/policies/{policy}/endorsements",
            new { endorsementType = "INTERMEDIARY", effectiveDate = Today.AddDays(10), changes = Array.Empty<object>(), intermediaryCode = "BRK-4" }, idempotent: true);
        draft.StatusCode.ShouldBe(HttpStatusCode.Created, await draft.Content.ReadAsStringAsync());
        var drafted = await draft.Json();
        var endorsement = drafted.GetProperty("id").GetGuid();
        (drafted.GetProperty("status").GetString(), drafted.GetProperty("premiumDelta").GetDecimal(), drafted.GetProperty("newIntermediaryCode").GetString())
            .ShouldBe(("Rated", 0m, "BRK-4"));
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/general/endorsements/{endorsement}/submit",
            ifMatch: (await _maker.GetAsync($"/api/v1/general/endorsements/{endorsement}")).ETag())).Code()).ShouldBe("APPOINTMENT_LETTER_REQUIRED");
        var letter = await _maker.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "APPOINTMENT", businessRefType = GeneralDocuments.EndorsementRefType, businessRefId = endorsement, mimeType = "text/plain",
            contentBase64 = Convert.ToBase64String("I appoint BRK-4"u8.ToArray()),
        }, idempotent: true);
        letter.StatusCode.ShouldBe(HttpStatusCode.Created, await letter.Content.ReadAsStringAsync());
        var recorded = await _maker.Send(HttpMethod.Post, $"/api/v1/general/endorsements/{endorsement}/appointment-letter",
            new { documentId = (await letter.Json()).GetProperty("id").GetGuid() }, ifMatch: (await _maker.GetAsync($"/api/v1/general/endorsements/{endorsement}")).ETag());
        recorded.StatusCode.ShouldBe(HttpStatusCode.OK, await recorded.Content.ReadAsStringAsync());
        var version = (await _maker.GetAsync($"/api/v1/general/endorsements/{endorsement}")).ETag();
        (await _maker.Send(HttpMethod.Post, $"/api/v1/general/endorsements/{endorsement}/submit", ifMatch: version)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.EndorsementBusinessRefType, endorsement);
        await _factory.DrainOutbox();

        // OQ-136: approved for a later date, the policy stays with its broker, the change waiting on it.
        (await _db.ScalarAsOwner<string>("SELECT intermediary_code || ':' || pending_intermediary_code || ':' || pending_intermediary_from FROM general.policy WHERE id = @p",
            ("p", policy))).ShouldBe($"{Broker}:BRK-4:{Today.AddDays(10):yyyy-MM-dd}");

        // B-010: told once it is approved, not when it applies: the incoming broker's contact party, and the outgoing broker through its own party (it has no contact party).
        await _factory.Services.GetRequiredService<Insys.Communications.Infrastructure.NotificationSender>().RunForTenant(Tenant, CancellationToken.None);
        var number = await _db.ScalarAsOwner<string>("SELECT policy_number FROM general.policy WHERE id = @p", ("p", policy));
        (await _db.ScalarAsOwner<string>("SELECT status || ':' || body FROM communication.notification WHERE recipient_party_id = @c AND type_code = 'BROKER_CHANGE_NOTICE'",
            ("c", incomingContact))).ShouldBe($"SENT:{number} (INCOMING): {Broker} to BRK-4 from {Today.AddDays(10):yyyy-MM-dd}.");
        (await _db.ScalarAsOwner<string>("SELECT string_agg(merge_values->>'broker_role', ',' ORDER BY merge_values->>'broker_role') FROM communication.notification WHERE source_id = @p AND type_code = 'BROKER_CHANGE_NOTICE'",
            ("p", policy))).ShouldBe("INCOMING,OUTGOING");

        // The date comes (brought to today): the daily job moves the policy, and the commission splits at it.
        await _db.ExecuteAsOwner("UPDATE general.policy SET pending_intermediary_from = @d WHERE id = @p", ("d", Today), ("p", policy));
        await _factory.Services.GetRequiredService<Insys.General.Infrastructure.GeneralJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("SELECT intermediary_code || ':' || coalesce(pending_intermediary_code, '-') FROM general.policy WHERE id = @p", ("p", policy)))
            .ShouldBe("BRK-4:-");
        var coverEnd = due.AddMonths(1).AddDays(-1);
        var share = Math.Round((decimal)(coverEnd.DayNumber - Today.DayNumber + 1) / (coverEnd.DayNumber - due.DayNumber + 1), 6);
        var entitlements = await Entitlements(policy);
        entitlements.Single(e => e.GetProperty("intermediaryCode").GetString() == Broker).GetProperty("amount").GetDecimal()
            .ShouldBe(Math.Round(Math.Round(basis * 0.10m, 2, MidpointRounding.AwayFromZero) * (1m - share), 2, MidpointRounding.AwayFromZero), "the outgoing broker keeps the days before");
        var incoming = entitlements.Single(e => e.GetProperty("intermediaryCode").GetString() == "BRK-4");
        (incoming.GetProperty("sourceId").GetGuid(), incoming.GetProperty("amount").GetDecimal())
            .ShouldBe((invoice, Math.Round(Math.Round(basis * share, 2, MidpointRounding.AwayFromZero) * 0.10m, 2, MidpointRounding.AwayFromZero)));
    }

    private async Task<Guid> IssuedPolicy(Guid vehicle)
    {
        var quote = await Created("/api/v1/general/quotes", QuoteBody(vehicle, Broker));
        var calculated = await _maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"E2E-{quote:N}"[..20], sourceDomain = GeneralRatingSubjects.SourceDomain, subjectType = GeneralRatingSubjects.Quote, subjectId = quote,
            productCode = Product, productVersion = 1, currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = 100_000m, vehicle_group = "G3" },
        }, idempotent: true);
        calculated.StatusCode.ShouldBe(HttpStatusCode.Created, await calculated.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposal = await Created("/api/v1/general/proposals", new { quoteId = quote, declarations = new { claims_last_3_years = 0 } });
        var accepted = await _maker.PostAsync($"/api/v1/general/proposals/{proposal}/accept", null);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        var policy = await Created("/api/v1/general/policies", new { quoteId = quote, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER" });
        await _factory.DrainOutbox();
        return policy;
    }

    private async Task<List<JsonElement>> Entitlements(Guid policy) =>
        (await (await _maker.GetAsync($"/api/v1/distribution/entitlements?policyRef={policy}")).Json()).EnumerateArray().ToList();

    /// <summary>Batches the payouts of a purpose, approves and releases the batch, and the provider confirms the payment for the business reference.</summary>
    private async Task PayOut(string purpose, Guid businessRefId)
    {
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Decide(PaymentApprovals.PayoutBusinessRefType, batch);
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r AND payout_batch_id = @b", ("r", businessRefId), ("b", batch));
        (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
    }

    /// <summary>The maker cannot decide their own request (SRS-002); the checker approves it.</summary>
    private async Task Decide(string businessRefType, Guid businessRefId)
    {
        var page = await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={businessRefId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var decided = await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        decided.StatusCode.ShouldBe(HttpStatusCode.NoContent, await decided.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    private Task<HttpResponseMessage> Callback(Guid payment, string status) => _maker.SimulatorCallback(_factory, _db, payment, status);

    private async Task<Guid> Created(string url, object body)
    {
        var response = await _maker.Send(HttpMethod.Post, url, body, idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Get(string url)
    {
        var response = await _maker.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private async Task<List<JsonElement>> Journals(string businessRefType, Guid businessRefId) =>
        (await (await _maker.GetAsync($"/api/v1/finance/journals?businessRefType={businessRefType}&businessRefId={businessRefId}")).Json()).GetProperty("items").EnumerateArray().ToList();

    private Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
        _db.SeedStatic(Tenant, table, status, code, Today.AddDays(-30), fields);
}
