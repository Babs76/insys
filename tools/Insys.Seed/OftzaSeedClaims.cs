/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Seed
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
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Insys.Seed;

/// <summary>Claims at each main stage, and the money behind them, each by its normal process, with a second person where the process asks for one. Payments reach only the simulator.</summary>
public sealed partial class OftzaSeed
{
    private Persona Handler => context.Tenant(TenantId, "oftza-claims-handler");
    private Persona ClaimsManager => context.Tenant(TenantId, "oftza-claims-manager");
    private Persona Assessor => context.Tenant(TenantId, "oftza-health-claims-assessor");
    private Persona FinanceOfficer => context.Tenant(TenantId, "oftza-finance-officer");
    private Persona FinanceManager => context.Tenant(TenantId, "oftza-finance-manager");

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task ClaimConfiguration()
    {
        await Static("general/static/peril-codes", "ACC", new { perilCode = "ACC", label = "Accident", line = "MOTOR", catastrophe = false });
        await Static("general/static/peril-codes", "FIRE", new { perilCode = "FIRE", label = "Fire", line = "PROPERTY", catastrophe = false });
        await Static("general/static/loss-cause-codes", "COLLISION", new { causeCode = "COLLISION", peril = "ACC", label = "Collision" });
        await Static("general/static/loss-cause-codes", "HOUSE_FIRE", new { causeCode = "HOUSE_FIRE", peril = "FIRE", label = "House fire" });
        await Static("general/static/repairer-and-assessor-panels", "OFT-ASSESSOR", new { party = (await Party(Clerk, SampleParties.Companies[1], SampleParties.Companies[1], null, new { })).ToString(), panelType = "ASSESSOR", location = "JHB", approvedLines = new[] { "MOTOR", "PROPERTY" }, status = "ACTIVE" });
        foreach (var (code, type, wording) in new[] { ("SETTLE", "APPROVE", "Your claim is settled."), ("DEATH_PROVEN", "APPROVE", "Your claim is accepted."), ("PART_COVER", "PARTIAL", "Part of your claim is accepted (sample wording)."), ("OUT_OF_COVER", "DECLINE", "This loss is not covered (sample wording).") })
        {
            await Static("claims-framework/static/claim-decision-reason-codes", code, new { decisionType = type, customerWording = wording });
        }

        await Static("life/static/claims-authority-limits", "LCL-FUNERAL", new { role = "CLAIMS_MANAGER", claimType = "FUNERAL", limitValue = 100_000m });
        await Static("workflow/static/delegation-and-approval-authority", "AUTH-REFUND-FINANCE_MANAGER", new { roleCode = "FINANCE_MANAGER", transactionType = "REFUND", limitValue = 25_000m, delegationAllowed = false, currency = "ZAR" });
        await Static("workflow/static/delegation-and-approval-authority", "AUTH-PROVIDER_PAYMENT_RUN-FINANCE_MANAGER", new { roleCode = "FINANCE_MANAGER", transactionType = "PROVIDER_PAYMENT_RUN", limitValue = 5_000_000m, delegationAllowed = false, currency = "ZAR" });
        await Static("finance/static/chart-of-accounts-subledger", "ACC-2200", new { accountCode = "2200", name = "VAT (sample)", type = "LIABILITY", glAccount = "2200", status = "ACTIVE" });
        await Static("finance/static/tax-codes-on-financial-transactions", "VAT", new { taxCode = "VAT", rate = 15m, account = "2200", jurisdiction = "ZA" });
        await Static("finance/static/posting-rules", "PR-TAX", new { ruleCode = "PR-TAX", transactionType = "PREMIUM_TAX", debitAccount = "1100", creditAccount = "2200" });
        await Static("finance/static/posting-rules", "PR-TAX-CR", new { ruleCode = "PR-TAX-CR", transactionType = "PREMIUM_TAX_CREDITED", debitAccount = "2200", creditAccount = "1100" });
        await Static("documents/static/document-categories", "REMITTANCE_ADVICE", new { categoryCode = "REMITTANCE_ADVICE", label = "Remittance advice", classification = "CONFIDENTIAL", domain = "HEALTH", allowedRoles = Array.Empty<string>() });
        await Static("documents/static/document-templates", "OFT-TPL-RA", new { templateCode = "OFT-TPL-RA", language = "en", documentType = "REMITTANCE_ADVICE", bodyRef = "sample", mergeFields = new[] { "run_code", "provider_id", "amount" } });
        await Static("health/static/remittance-advice-formats", "RA-STD", new { formatCode = "RA-STD", channel = "PORTAL", template = "OFT-TPL-RA v1" });
        await Static("workflow/static/case-categories", "CLAIM_DISPUTE", new { categoryCode = "CLAIM_DISPUTE", family = "COMPLAINT", pprCategory = "CLAIMS", name = "Claim dispute (sample)" });
        foreach (var (status, platform) in new[] { ("BOUNCED", "FAILED"), ("TIMEOUT", "UNKNOWN") })
        {
            await Static("payments/static/payment-status-mappings", $"SIM-{status}", new { providerCode = "SIMULATOR", providerStatus = status, platformStatus = platform, final = platform != "UNKNOWN" });
        }
    }

    private async Task Claims()
    {
        // Motor and household: registered, reserved, assessed, approved, partly approved (assessed below the reserve), declined, paid.
        await GeneralClaim(0, "Registered");
        await GeneralClaim(2, "Assessed");
        await GeneralClaim(4, "Approved", 20_000m);
        await GeneralClaim(6, "Paid", 20_000m);
        await GeneralClaim(1, "Declined");
        await GeneralClaim(3, "Approved", 8_000m);
        await GeneralClaim(5, "Paid", 15_000m);

        await LifeClaim(3, "FUNERAL", "Registered", 0m);
        await LifeClaim(5, "FUNERAL", "Assessed", 30_000m);
        await LifeClaim(7, "FUNERAL", "Approved", 30_000m);
        await LifeClaim(4, "DEATH", "Paid", 400_000m);
        await LifeClaim(6, "DEATH", "Approved", 200_000m);

        await HealthClaim(5, 1_200m, "Submitted");
        await HealthClaim(6, 1_200m, "Adjudicated");
        await HealthClaim(8, 1_200m, "Paid");
        await HealthClaim(9, 900m, "Paid");
        await HealthClaim(10, 700m, "Adjudicated");
        await Refund();
        await PayOut();
        await Statement();
        await Commission();
        await Complaint();
        await Books();

        // Approved after the week's run, so it waits for the next one.
        await HealthClaim(7, 1_200m, "Approved");
    }

    /// <summary>A verified account for a payee: one person adds it, the finance manager verifies it.</summary>
    private async Task<Guid> PayeeAccount(Guid party, string number)
    {
        var url = $"/api/v1/party/parties/{party}/bank-accounts";
        var existing = await Clerk.Send(HttpMethod.Get, url);
        var accounts = (existing.ValueKind == JsonValueKind.Array ? existing : existing.GetProperty("items")).EnumerateArray().ToList();
        var id = accounts.Count > 0 ? accounts[0].GetProperty("id").GetGuid()
            : (await Clerk.Send(HttpMethod.Post, url, new { accountNumber = number, bankCode = "000000", accountHolderName = "Sample account holder", accountType = "CURRENT", currency = "ZAR" }, key: $"{TenantCode}:bank-account:{party}")).GetProperty("id").GetGuid();
        await context.Settle();
        await Approve(FinanceManager, id);
        await context.Settle();
        return id;
    }

    private async Task GeneralClaim(int holder, string stage, decimal assessed = 0m)
    {
        var party = await Person(holder);
        var policy = (await Clerk.Send(HttpMethod.Get, $"/api/v1/general/policies?policyholderPartyId={party}")).GetProperty("items")[0];
        var policyId = policy.GetProperty("id").GetGuid();
        var view = await Clerk.Send(HttpMethod.Get, $"/api/v1/general/policies/{policyId}");
        var risk = view.GetProperty("risks")[0];
        var coverage = risk.GetProperty("coverages")[0].GetProperty("coverageCode").GetString();
        var existing = (await Handler.Send(HttpMethod.Get, $"/api/v1/general/claims?policyId={policyId}")).GetProperty("items");
        var claim = existing.GetArrayLength() > 0 ? existing[0].GetProperty("id").GetGuid() : (await Handler.Send(HttpMethod.Post, "/api/v1/general/claims", new
        {
            policyId, riskObjectId = risk.GetProperty("riskObjectId").GetGuid(), coverageCode = coverage, lossDate = Today, causeCode = holder % 2 == 0 ? "COLLISION" : "HOUSE_FIRE", perilCode = holder % 2 == 0 ? "ACC" : "FIRE", description = "Sample loss",
        }, key: $"{TenantCode}:gclaim:{holder}")).GetProperty("id").GetGuid();
        var url = $"/api/v1/general/claims/{claim}";
        async Task<string> Status() => (await Handler.Send(HttpMethod.Get, url)).GetProperty("status").GetString()!;
        if ((await Handler.Send(HttpMethod.Get, url)).GetProperty("claimNumber").GetString() is null or "")
        {
            await Handler.Send(HttpMethod.Post, $"{url}/register", idempotent: false);
            await context.Settle();
        }

        // A claim an earlier run took past its stage is left as it is.
        if (stage == "Registered" || await Status() is "Approved" or "Settled" or "Declined" or "Closed")
        {
            return;
        }

        var number = (await Handler.Send(HttpMethod.Get, url)).GetProperty("claimNumber").GetString()!;
        await Handler.Send(HttpMethod.Post, $"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 30_000m, currency = "ZAR", reason = "Assessed" }, key: $"{TenantCode}:greserve:{claim}");
        var assessments = (await Handler.Send(HttpMethod.Get, $"{url}/assessments")).GetProperty("items");
        if (assessments.GetArrayLength() == 0)
        {
            var assessor = await Party(Clerk, SampleParties.Companies[1], SampleParties.Companies[1], null, new { });
            await Handler.Send(HttpMethod.Post, $"{url}/assessor", new { assessorPartyId = assessor }, key: $"{TenantCode}:gassessor:{claim}");
            await context.Settle();
            assessments = (await Handler.Send(HttpMethod.Get, $"{url}/assessments")).GetProperty("items");
        }

        if (stage == "Declined")
        {
            if (await Status() != "Declined")
            {
                await Handler.Send(HttpMethod.Post, $"{url}/decline", new { decisionCode = "OUT_OF_COVER", rationale = "Sample: the loss falls outside the cover." }, key: $"{TenantCode}:gdecline:{claim}");
                await context.Settle();
            }

            return;
        }

        var assessment = $"/api/v1/general/assessments/{assessments[0].GetProperty("id").GetGuid()}";
        await Step(Handler, assessment, "Assigned", "start");
        if ((await Handler.Send(HttpMethod.Get, assessment)).GetProperty("status").GetString() == "InProgress")
        {
            await Handler.Send(HttpMethod.Post, $"{assessment}/report", new { assessedAmount = assessed > 0 ? assessed : 20_000m }, key: $"{TenantCode}:greport:{claim}");
        }

        await Step(Handler, assessment, "Reported", "accept");
        if (stage == "Assessed")
        {
            return;
        }

        var account = await PayeeAccount(party, $"0000{holder + 100:00000}");
        if (!await Waiting(claim))
        {
            await Handler.Send(HttpMethod.Post, $"{url}/settlement", new { payeePartyId = party, bankAccountId = account, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Within the assessed amount" }, key: $"{TenantCode}:gsettle:{claim}");
            await context.Settle();
        }

        await Approve(ClaimsManager, claim);
        await context.Settle();
    }

    /// <summary>Whether a decision on the record already waits for a second person, from a run stopped after asking.</summary>
    private async Task<bool> Waiting(Guid record) =>
        (await ClaimsManager.Send(HttpMethod.Get, $"/api/v1/workflow/approvals?status=Requested&businessRefId={record}")).GetProperty("items").GetArrayLength() > 0;

    private async Task LifeClaim(int holder, string type, string stage, decimal amount)
    {
        var party = await Person(holder);
        var underwriter = context.Tenant(TenantId, "oftza-life-underwriter");
        var policy = (await underwriter.Send(HttpMethod.Get, $"/api/v1/life/policies?policyholderPartyId={party}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var view = await underwriter.Send(HttpMethod.Get, $"/api/v1/life/policies/{policy}");
        var spouse = view.GetProperty("beneficiaries")[0].GetProperty("partyId").GetGuid();
        var life = view.GetProperty("lives")[0].GetProperty("id").GetGuid();
        var existing = (await Handler.Send(HttpMethod.Get, $"/api/v1/life/claims?policyId={policy}")).GetProperty("items");
        var claim = existing.GetArrayLength() > 0 ? existing[0].GetProperty("id").GetGuid() : (await Handler.Send(HttpMethod.Post, "/api/v1/life/claims", new
        {
            policyId = policy, claimType = type, eventDate = Today, causeCode = "I21", claimantPartyId = spouse, insuredLifeId = life,
        }, key: $"{TenantCode}:lclaim:{holder}")).GetProperty("id").GetGuid();
        var url = $"/api/v1/life/claims/{claim}";
        async Task<string> Status() => (await Handler.Send(HttpMethod.Get, url)).GetProperty("status").GetString()!;
        if ((await Handler.Send(HttpMethod.Get, url)).GetProperty("claimNumber").GetString() is null or "")
        {
            await Handler.Send(HttpMethod.Post, $"{url}/register", idempotent: false);
            await context.Settle();
        }

        if (stage == "Registered")
        {
            return;
        }

        var benefit = view.GetProperty("benefits")[0].GetProperty("id").GetGuid();
        if (await Status() == "Registered")
        {
            await Handler.Send(HttpMethod.Post, $"{url}/assessment", new { lines = new[] { new { policyBenefitId = benefit, amount } } }, key: $"{TenantCode}:lassess:{claim}");
        }

        if (stage == "Assessed")
        {
            return;
        }

        var account = await PayeeAccount(spouse, $"0000{holder + 200:00000}");
        if (await Status() == "InAssessment" && !await Waiting(claim))
        {
            await Handler.Send(HttpMethod.Post, $"{url}/approval", new { payees = new[] { new { partyId = spouse, bankAccountId = account, sharePct = 100m } }, reserveType = "INDEMNITY", decisionReason = type == "DEATH" ? "PART_COVER" : "DEATH_PROVEN", rationale = "Sample: certificate verified." }, key: $"{TenantCode}:lapprove:{claim}");
            await context.Settle();
        }

        await Approve(ClaimsManager, claim);
        await context.Settle();
    }

    private async Task HealthClaim(int principal, decimal claimed, string stage)
    {
        var party = await Person(principal);
        var member = await Membership(principal);
        var existing = (await Assessor.Send(HttpMethod.Get, $"/api/v1/health/claims?membershipId={member}")).GetProperty("items");
        var today = Today;
        var claim = existing.GetArrayLength() > 0 ? existing[0].GetProperty("id").GetGuid() : (await Assessor.Send(HttpMethod.Post, "/api/v1/health/claims", new
        {
            source = "PROVIDER", providerId = "OFT-PR-1", membershipId = member, patientPartyId = party, serviceDate = today, formatCode = "API", currency = "ZAR",
            lines = new[] { new { tariffCode = "0190", diagnosisCode = "K35", quantity = 1, claimedAmount = claimed } },
        }, key: $"{TenantCode}:hclaim:{principal}")).GetProperty("id").GetGuid();
        var url = $"/api/v1/health/claims/{claim}";
        await Step(Assessor, url, "Received", stage == "Submitted" ? "none" : "validate");
        await Step(Assessor, url, "Validated", stage == "Submitted" ? "none" : "adjudicate");
        if (stage is "Approved" or "Paid")
        {
            await Step(Assessor, url, "Adjudicated", "approve");
            await context.Settle();
        }
    }

    /// <summary>The week's provider run and the claim payouts: built by the finance officer, approved and released by the finance manager, and paid or bounced by the simulator.</summary>
    private async Task PayOut()
    {
        var run = (await FinanceOfficer.Send(HttpMethod.Post, "/api/v1/health/payment-runs", new { runCode = "PRV-WEEKLY", currency = "ZAR" }, key: $"{TenantCode}:run:{Today:yyyyMMdd}")).GetProperty("id").GetGuid();
        var url = $"/api/v1/health/payment-runs/{run}";
        await Step(FinanceOfficer, url, "Open", "cut-off");
        await Step(FinanceOfficer, url, "CutOff", "calculate");
        await context.Settle();
        await Approve(FinanceManager, run);
        await context.Settle();
        await Step(FinanceManager, url, "Approved", "release");
        await context.Settle();
        foreach (var purpose in new[] { "CLAIM_PAYOUT", "REFUND" })
        {
            var batch = (await FinanceOfficer.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose, currency = "ZAR" }, key: $"{TenantCode}:payout:{purpose}:{Today:yyyyMMdd}")).GetProperty("id").GetGuid();
            await context.Settle();
            await Approve(FinanceManager, batch);
            await context.Settle();
            await Step(FinanceManager, $"/api/v1/payments/payout-batches/{batch}", "Approved", "release");
            await context.Settle();
        }

        var secret = await CallbackEndpoint();
        var payments = (await FinanceOfficer.Send(HttpMethod.Get, "/api/v1/payments/payments?status=Pending&limit=200")).GetProperty("items").EnumerateArray().ToList();
        // Collections: most paid, one bounced and one with no clear answer; none for the policies left in arrears on purpose.
        var underwriter = context.Tenant(TenantId, "oftza-life-underwriter");
        var arrears = new HashSet<Guid>();
        foreach (var holder in new[] { 11, 12, 13, 14 })
        {
            arrears.UnionWith((await underwriter.Send(HttpMethod.Get, $"/api/v1/life/policies?policyholderPartyId={await Person(holder)}")).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        }

        var outcomes = new Queue<string>(["BOUNCED", "TIMEOUT"]);
        foreach (var payment in payments)
        {
            if (payment.GetProperty("direction").GetString() == "Out")
            {
                await Callback(secret, payment.GetProperty("externalRef").GetString()!, "PAID");
                continue;
            }

            var invoice = await FinanceOfficer.Send(HttpMethod.Get, $"/api/v1/billing/invoices/{payment.GetProperty("businessRefId").GetGuid()}");
            if (!arrears.Contains(invoice.GetProperty("policyRef").GetGuid()))
            {
                await Callback(secret, payment.GetProperty("externalRef").GetString()!, outcomes.TryDequeue(out var outcome) ? outcome : "PAID");
            }
        }

        await context.Settle();
    }

    /// <summary>A refund to a policyholder whose policy was cancelled, requested by the finance officer and approved by the finance manager before it is paid.</summary>
    private async Task Refund()
    {
        var party = await Person(8);
        var policy = (await Clerk.Send(HttpMethod.Get, $"/api/v1/general/policies?policyholderPartyId={party}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var id = (await FinanceOfficer.Send(HttpMethod.Get, $"/api/v1/billing/accounts?policyRef={policy}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var account = await FinanceOfficer.Send(HttpMethod.Get, $"/api/v1/billing/accounts/{id}");
        if (account.GetProperty("refunds") is not { ValueKind: JsonValueKind.Array } refunds || refunds.GetArrayLength() == 0)
        {
            var bank = await PayeeAccount(party, "000000108");
            var refund = (await FinanceOfficer.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account.GetProperty("id").GetGuid()}/refunds", new { amount = 100m, reasonCode = "INS_REQUEST", bankAccountId = bank }, key: $"{TenantCode}:refund:{policy}")).GetProperty("id").GetGuid();
            await context.Settle();
            await Approve(FinanceManager, refund);
            await context.Settle();
        }
    }

    /// <summary>The broker's statement for the month, from the commission its paid premiums earned, approved by the finance manager.</summary>
    private async Task Commission()
    {
        var distribution = context.Tenant(TenantId, "oftza-distribution-administrator");
        var broker = SampleParties.Brokers[0].Code;
        var existing = (await distribution.Send(HttpMethod.Get, $"/api/v1/distribution/statements?intermediaryCode={broker}")).GetProperty("items");
        if (existing.GetArrayLength() == 0)
        {
            var statement = (await distribution.Send(HttpMethod.Post, "/api/v1/distribution/statements", new { intermediaryCode = broker, periodCode = $"{Today:yyyy-MM}", currency = "ZAR" }, key: $"{TenantCode}:statement:{broker}:{Today:yyyy-MM}")).GetProperty("id").GetGuid();
            await context.Settle();
            await Approve(FinanceManager, statement);
            await context.Settle();
        }
    }

    /// <summary>A policyholder disputes their claim: a complaint case linked to it, acknowledged within its timeline.</summary>
    private async Task Complaint()
    {
        var agent = context.Tenant(TenantId, "oftza-customer-service-agent");
        var party = await Person(2);
        var policy = (await Clerk.Send(HttpMethod.Get, $"/api/v1/general/policies?policyholderPartyId={party}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var claim = (await Handler.Send(HttpMethod.Get, $"/api/v1/general/claims?policyId={policy}")).GetProperty("items")[0];
        if ((await agent.Send(HttpMethod.Get, $"/api/v1/workflow/cases?linkType=CLAIM&linkId={claim.GetProperty("id").GetGuid()}")).GetProperty("items").GetArrayLength() > 0)
        {
            return;
        }

        var opened = (await agent.Send(HttpMethod.Post, "/api/v1/workflow/cases", new
        {
            caseTypeCode = "COMPLAINT", receivedChannel = "PHONE", receivedAt = DateTimeOffset.UtcNow, complainantPartyId = party, complainantName = (string?)null, complainantContact = (string?)null, onBehalfOfPartyId = (Guid?)null,
            categoryCode = "CLAIM_DISPUTE", summary = "Sample: the policyholder disputes the assessed amount.", upheldAtReceipt = (bool?)null, parentCaseId = (Guid?)null,
            links = new[] { new { linkType = "CLAIM", recordId = claim.GetProperty("id").GetGuid(), reference = claim.GetProperty("claimNumber").GetString() } },
        }, key: $"{TenantCode}:complaint:{party}")).GetProperty("id").GetGuid();
        await agent.Send(HttpMethod.Post, $"/api/v1/workflow/cases/{opened}/acknowledge", idempotent: false);
    }

    /// <summary>The provider's statement for the week, compared with INSYS's records: one claim agrees, one it has no record of.</summary>
    private async Task Statement()
    {
        var paid = (await Assessor.Send(HttpMethod.Get, $"/api/v1/health/claims?membershipId={await Membership(8)}")).GetProperty("items")[0].GetProperty("claimNumber").GetString();
        var csv = $"claim_number,amount\n{paid},500.00\nSAMPLE-UNKNOWN,120.00\n";
        await FinanceOfficer.Send(HttpMethod.Post, "/api/v1/health/provider-statements/import", new { providerId = "OFT-PR-1", statementRef = $"OFT-ST-{Today:yyyyMMdd}", periodFrom = Today.AddDays(-7), periodTo = Today, currency = "ZAR", csv }, key: $"{TenantCode}:statement:{Today:yyyyMMdd}");
    }

    /// <summary>Last month closed (soft-closed, then closed), this month open, and this month's VAT return prepared for a second person.</summary>
    private async Task Books()
    {
        var month = new DateOnly(Today.Year, Today.Month, 1);
        var last = $"/api/v1/finance/periods/{month.AddMonths(-1):yyyy-MM}";
        await Step(FinanceManager, last, "Future", "open");
        await Step(FinanceManager, last, "Open", "soft-close");
        await Step(FinanceManager, last, "SoftClose", "close");
        var returns = await FinanceOfficer.Send(HttpMethod.Get, "/api/v1/finance/tax-returns");
        if ((returns.ValueKind == JsonValueKind.Array ? returns : returns.GetProperty("items")).GetArrayLength() == 0)
        {
            await FinanceOfficer.Send(HttpMethod.Post, "/api/v1/finance/tax-returns", new { taxCode = "VAT", currency = "ZAR", periodStart = month, periodEnd = month.AddMonths(1).AddDays(-1) }, key: $"{TenantCode}:vat:{month:yyyy-MM}");
        }
    }

    /// <summary>The key and secret are shown once, so each run that has money to report issues its own, and the one before is revoked.</summary>
    private async Task<(string Path, string Secret)> CallbackEndpoint()
    {
        var integration = context.Tenant(TenantId, "oftza-integration-administrator");
        var issued = await integration.Send(HttpMethod.Post, "/api/v1/payments/callback-endpoints", new { providerCode = "SIMULATOR" }, key: $"{TenantCode}:callback:{Guid.CreateVersion7()}");
        return (issued.GetProperty("callbackPath").GetString()!, issued.GetProperty("signingSecret").GetString()!);
    }

    private async Task Callback((string Path, string Secret) endpoint, string externalRef, string status)
    {
        var body = JsonSerializer.Serialize(new { externalRef, status }, JsonSerializerOptions.Web);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var id = Guid.CreateVersion7().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Callback-Timestamp", timestamp);
        request.Headers.Add("X-Callback-Id", id);
        request.Headers.Add("X-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Secret), Encoding.UTF8.GetBytes($"{timestamp}.{id}.{body}"))));
        using var response = await FinanceOfficer.Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new SeedRefusedException($"POST {endpoint.Path}", response.StatusCode, await response.Content.ReadAsStringAsync());
        }
    }
}
