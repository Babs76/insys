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
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly string Month = Today.ToString("yyyy-MM");
    private static readonly Guid Assessor = Guid.CreateVersion7();

    private static readonly string[] Permissions =
    [
        GeneralPermissions.RiskRead, GeneralPermissions.RiskCreate, GeneralPermissions.QuoteRead, GeneralPermissions.QuoteCreate, GeneralPermissions.QuoteUpdate, GeneralPermissions.QuoteUnderwrite,
        GeneralPermissions.PolicyRead, GeneralPermissions.PolicyCreate, GeneralPermissions.PolicyUpdate, GeneralPermissions.PolicyCancel, GeneralPermissions.ClaimRead,
        GeneralPermissions.ClaimCreate, GeneralPermissions.ClaimUpdate, GeneralPermissions.ClaimDecide, GeneralPermissions.AssessmentUpdate,
        RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead, BillingPermissions.AccountRead, BillingPermissions.InvoiceRead,
        PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackCreate,
        ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead, ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead,
        FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
        IntegrationPermissions.DeadLetterRead, IntegrationPermissions.WebhookRead, IntegrationPermissions.WebhookUpdate, DocumentPermissions.DocumentRead,
        Insys.Party.Contracts.PartyPermissions.PartyCreate, Insys.Party.Contracts.PartyPermissions.BankAccountCreate, Insys.Party.Contracts.PartyPermissions.BankAccountRead,
        Insys.Party.Contracts.PartyPermissions.BankAccountVerify, DistributionPermissions.IntermediaryRead, DistributionPermissions.CommissionRead,
        DistributionPermissions.CommissionUpdate, DistributionPermissions.StatementRead, DistributionPermissions.StatementCreate, ReinsurancePermissions.CessionRead,
        ReinsurancePermissions.RecoveryRead, ReinsurancePermissions.RecoveryUpdate, ReinsurancePermissions.BordereauRead, ReinsurancePermissions.BordereauCreate,
        ReinsurancePermissions.BordereauUpdate, AnalyticsPermissions.ProjectionRead, AnalyticsPermissions.ProjectionUpdate, CompliancePermissions.ReturnRead,
        CompliancePermissions.ReturnCreate, CompliancePermissions.ReturnUpdate, CompliancePermissions.ReturnApprove, CompliancePermissions.ReportRead,
        CompliancePermissions.ReportCreate,
    ];

    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;
    private string _reportRole = "";

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
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
                     DistributionApprovals.StatementApprovalType,
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
            ("invitation_template", "TPL-RENEWAL_NOTICE"));
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
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }),
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
        });
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

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

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
        var cession = (await (await _maker.GetAsync($"/api/v1/reinsurance/cessions?sourceDomain=GENERAL&policyRef={policy}")).Json()).EnumerateArray().Single();
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
        await PayOut(PaymentPurposes.Commission, statement);
        (await Get($"/api/v1/distribution/statements/{statement}")).GetProperty("status").GetString().ShouldBe("Paid");
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

        // PrepareBordereau for the month (one per treaty and period), sent through the reinsurer adapter, then agreed.
        var bordereau = await Created("/api/v1/reinsurance/bordereaux", new { layoutCode = "BX-QS1", periodCode = Month });
        var prepared = await Get($"/api/v1/reinsurance/bordereaux/{bordereau}");
        (prepared.GetProperty("rowCount").GetInt32(), prepared.GetProperty("content").GetString()).ShouldBe((1, $"policy_ref,ceded_sum_insured,ceded_premium\n{policy},70000.00,2135.00\n"));
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/reinsurance/bordereaux", new { layoutCode = "BX-QS1", periodCode = Month }, idempotent: true)).Code())
            .ShouldBe("RI_BORDEREAU_EXISTS");
        (await (await _maker.PostAsync($"/api/v1/reinsurance/bordereaux/{bordereau}/send", null)).Json()).GetProperty("status").GetString().ShouldBe("Sent");
        (await (await _maker.PostAsync($"/api/v1/reinsurance/bordereaux/{bordereau}/agree", null)).Json()).GetProperty("status").GetString().ShouldBe("Agreed");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.external_call_log WHERE operation = 'reinsurance.SendBordereau' AND partner_code = 'RE-1'"))
            .ShouldBe(1L);
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
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 50_000m, currency = "ZAR" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Assessor })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var assessment = (await (await _maker.GetAsync($"/api/v1/general/claims/{claim}/assessments")).Json()).EnumerateArray().Single().GetProperty("id").GetGuid();
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

        var recovery = (await (await _maker.GetAsync($"/api/v1/reinsurance/recoveries?claimNumber={number}")).Json()).EnumerateArray().Single();
        (recovery.GetProperty("treatyCode").GetString(), recovery.GetProperty("paidAmount").GetDecimal(), recovery.GetProperty("recoverable").GetDecimal(),
            recovery.GetProperty("status").GetString(), recovery.GetProperty("policyRef").GetGuid()).ShouldBe(("QS-1", 35_000m, 24_500m, "Billed", policy));

        // SettleRecovery: the reinsurer pays short; the difference stays for reconciliation; both are posted.
        var recoveryId = recovery.GetProperty("id").GetGuid();
        var settled = await (await _maker.PostAsJsonAsync($"/api/v1/reinsurance/recoveries/{recoveryId}/settle", new { amount = 24_000m, settledOn = Today })).Json();
        (settled.GetProperty("status").GetString(), settled.GetProperty("difference").GetDecimal()).ShouldBe(("Settled", -500m));
        await _factory.DrainOutbox();
        (await Journals("RI_RECOVERY", recoveryId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task RenewalJob_InvitesThenRenewsByItself_AndTheExposureSnapshotSumsByZoneAndPeril_GEN004()
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
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/compliance/report-runs", new { reportCode = "RPT-GWP", from = Today, to = Today, metrics = new[] { "GWP" }, export = true },
            idempotent: true)).Code()).ShouldBe("REPORT_NOT_ALLOWED");
        (await (await _checker.Send(HttpMethod.Post, "/api/v1/compliance/report-runs", new { reportCode = "RPT-GWP", from = Today, to = Today, metrics = new[] { "GWP" }, export = false },
            idempotent: true)).Code()).ShouldBe("REPORT_NOT_ALLOWED");

        // AccountingPeriodClosed (final) → PrepareReturn from the metric and the ledger.
        (await _maker.PostAsync($"/api/v1/finance/periods/{Month}/soft-close", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/finance/periods/{Month}/close", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var prepared = (await (await _maker.GetAsync($"/api/v1/compliance/returns?periodCode={Month}")).Json()).EnumerateArray().Single();
        prepared.GetProperty("fieldValues").GetProperty("gross_premium").GetDecimal().ShouldBe(3_050m);
        prepared.GetProperty("fieldValues").GetProperty("receivables").GetDecimal().ShouldBe(254.17m);
        var id = prepared.GetProperty("id").GetGuid();

        // Reviewed, then approved by a different compliance officer, then submitted through the regulator adapter and accepted.
        (await (await _maker.PostAsync($"/api/v1/compliance/returns/{id}/submit", null)).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        (await _maker.PostAsync($"/api/v1/compliance/returns/{id}/review", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _maker.PostAsync($"/api/v1/compliance/returns/{id}/approve", null)).Code()).ShouldBe("RETURN_SAME_PERSON");
        (await _checker.PostAsync($"/api/v1/compliance/returns/{id}/approve", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var submitted = await (await _maker.PostAsync($"/api/v1/compliance/returns/{id}/submit", null)).Json();
        submitted.GetProperty("status").GetString().ShouldBe("Submitted");
        submitted.GetProperty("regulatorRef").GetString()!.ShouldStartWith("STUB-");
        (await (await _maker.PostAsync($"/api/v1/compliance/returns/{id}/accept", null)).Json()).GetProperty("status").GetString().ShouldBe("Accepted");
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

    private async Task<HttpResponseMessage> Callback(Guid payment, string status)
    {
        var externalRef = await _db.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", payment));
        var body = JsonSerializer.Serialize(new { externalRef, status }, JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/providers/SIMULATOR/callbacks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestTokens.SimulatorCallbackSecret), Encoding.UTF8.GetBytes(body))));
        return await _maker.SendAsync(request);
    }

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
        (await (await _maker.GetAsync($"/api/v1/finance/journals?businessRefType={businessRefType}&businessRefId={businessRefId}")).Json()).EnumerateArray().ToList();

    private Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
        _db.SeedStatic(Tenant, table, status, code, Today.AddDays(-30), fields);
}
