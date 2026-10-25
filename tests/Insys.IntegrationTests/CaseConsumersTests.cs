/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tests
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
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Communications.Contracts;
using Insys.Health.Contracts;
using Insys.Payments.Application;
using Insys.Workflow.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insys.IntegrationTests;

/// <summary>
/// What the other modules do with Workflow's cases (addenda/cases.md §2, §7; Wave 2): Communications sends the case
/// notices with merge values from ICaseNotices, never from the event; an upheld claim dispute reopens a General, a Life
/// and a Health claim (OQ-103); a partial approval records its reason and sends CLAIM_DECISION_RIGHTS (OQ-102); approved
/// redress is paid out through Payments (OQ-104). Workflow's notice data is a test double here.
/// </summary>
public sealed class CaseConsumersTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly From = TestClock.Today.AddDays(-30);
    private const string Audit = "row_version, created_at, created_by, updated_at, updated_by";
    private const string Stamp = "1, now(), 'seed', now(), 'seed'";

    private static readonly StubCaseNotices Notices = new();
    private static readonly SemaphoreSlim Seeded = new(1, 1);
    private static bool _seeded;

    private WebApplicationFactory<Program> _host = null!;

    public async ValueTask InitializeAsync()
    {
        var factory = await fixture.Host();
        _host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaseNotices>();
            services.AddSingleton<ICaseNotices>(Notices);
        }));

        await Seeded.WaitAsync();
        try
        {
            if (!_seeded)
            {
                await fixture.Database.SeedEmailNotices(Tenant, From, "CASES",
                    (NotificationTypes.CaseAcknowledged, "Case {case_number}", "We received your {case_type} {case_number}; expect an answer by {expected_date}."),
                    (NotificationTypes.CaseUpdate, "Case {case_number}", "Case {case_number}: now expected by {expected_date}."),
                    (NotificationTypes.CaseDecision, "Case {case_number}", "{decision_reasons} {review_options} {ombud_details}"),
                    (NotificationTypes.ClaimDecisionRights, "Claim {claim_number}", "Claim {claim_number}: {amount} {currency} of {claimed_amount}. {decision_reasons}"));
                _seeded = true;
            }
        }
        finally
        {
            Seeded.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task CaseNotices_AreQueuedOnTheirEvents_WithMergeValuesFromTheCase_NotTheEvent_Cases7()
    {
        var complainant = Guid.CreateVersion7();
        var caseId = Guid.CreateVersion7();
        var expected = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        Notices.Cases[caseId] = new CaseNoticeData("CS-0001", "CUSTOMER_COMPLAINT", CaseFamilies.Complaint, expected, "We found the premium was correct.",
            "Ask for an internal review within 30 days.", "National Financial Ombud, 0860 800 900", null, complainant, null, null);

        // The event carries other values: the notice takes the case's.
        var acknowledged = Envelope("workflow.CaseAcknowledged", caseId, new CaseAcknowledged(caseId, "EVENT-NUMBER", CaseFamilies.Complaint, Guid.CreateVersion7(), null));
        await Publish(acknowledged);
        await Publish(acknowledged);
        await Publish(Envelope("workflow.CaseUpdated", caseId, new CaseUpdated(caseId, "EVENT-NUMBER", CaseFamilies.Complaint, complainant, "EXTENDED", null)));
        await Publish(Envelope("workflow.CaseDecided", caseId, new CaseDecided(caseId, "EVENT-NUMBER", "CUSTOMER_COMPLAINT", CaseFamilies.Complaint, CaseOutcomes.Rejected,
            complainant, [])));

        var rows = await Notifications(caseId);
        rows.Select(r => r.Type).Order().ShouldBe([NotificationTypes.CaseAcknowledged, NotificationTypes.CaseDecision, NotificationTypes.CaseUpdate],
            "one per event and recipient: a redelivered event queues nothing");
        rows.ShouldAllBe(r => r.Recipient == complainant && r.SourceType == "CASE" && r.Reference == "CS-0001");
        var ack = rows.Single(r => r.Type == NotificationTypes.CaseAcknowledged).Merge;
        (ack.GetProperty("case_number").GetString(), ack.GetProperty("case_type").GetString(), ack.GetProperty("expected_date").GetString())
            .ShouldBe(("CS-0001", "CUSTOMER_COMPLAINT", "2026-11-20"));
        var decision = rows.Single(r => r.Type == NotificationTypes.CaseDecision).Merge;
        decision.GetProperty("decision_reasons").GetString().ShouldBe("We found the premium was correct.");
        decision.GetProperty("review_options").GetString().ShouldBe("Ask for an internal review within 30 days.");
        decision.GetProperty("ombud_details").GetString().ShouldBe("National Financial Ombud, 0860 800 900");
        decision.TryGetProperty("representations_deadline", out _).ShouldBeFalse("a value the case does not have is left out");
    }

    [Fact]
    public async Task ACaseNotice_GoesToTheProviderForItsDispute_AndParksWhenTheTypeIsNotConfigured_ADR022()
    {
        var provider = Guid.CreateVersion7();
        await fixture.Database.SeedStaticFilled(Tenant, "health.mst_provider_register", "ACTIVE", "PRV-CASES", From, ("provider_id", "PRV-CASES"), ("status", "ACTIVE"),
            ("party_id", provider));
        var member = Guid.CreateVersion7();
        var caseId = Guid.CreateVersion7();
        Notices.Cases[caseId] = new CaseNoticeData("CS-0002", "PROVIDER_CLAIM_DISPUTE", CaseFamilies.Dispute, null, null, null, null, null, member, null, "PRV-CASES");
        await Publish(Envelope("workflow.CaseAcknowledged", caseId, new CaseAcknowledged(caseId, "CS-0002", CaseFamilies.Dispute, member, null)));
        (await Notifications(caseId)).Select(r => r.Recipient).Order().ShouldBe(new[] { member, provider }.Order());

        // Tenant B has no case notification types: the delivery parks, naming the type.
        var other = Guid.CreateVersion7();
        Notices.Cases[other] = new CaseNoticeData("CS-B-1", "CUSTOMER_COMPLAINT", CaseFamilies.Complaint, null, null, null, null, null, Guid.CreateVersion7(), null, null);
        var parked = Envelope("workflow.CaseAcknowledged", other, new CaseAcknowledged(other, "CS-B-1", CaseFamilies.Complaint, null, null), DatabaseFixture.TenantB);
        await Publish(parked);
        (await fixture.Database.ScalarAsOwner<string>(
            "SELECT prerequisite_code || ' ' || prerequisite_item FROM integration.parked_delivery WHERE event_id = @e AND status = 'PARKED'", ("e", parked.EventId)))
            .ShouldBe($"{PrerequisiteCodes.NotificationTypeNotConfigured} {NotificationTypes.CaseAcknowledged}");
    }

    [Fact]
    public async Task AnUpheldClaimDispute_ReopensTheGeneralLifeAndHealthClaimsItLinks_OnlyOnce_F011_F013_OQ103()
    {
        var general = await GeneralClaim("DECLINED");
        var life = await LifeClaim("DECLINED");
        var health = await HealthClaim("REJECTED", 1_000m, 0m);
        var openGeneral = await GeneralClaim("UNDER_ASSESSMENT");
        CaseLinkRef[] links =
        [
            new(CaseLinkTypes.Claim, general, "GC-1"), new(CaseLinkTypes.Claim, life, "LC-1"), new(CaseLinkTypes.Claim, health, "HC-1"),
            new(CaseLinkTypes.Claim, openGeneral, "GC-2"), new(CaseLinkTypes.Policy, Guid.CreateVersion7(), "P-1"),
        ];

        // A rejected dispute, and an upheld complaint, reopen nothing.
        await Publish(Decided(CaseFamilies.Dispute, CaseOutcomes.Rejected, links));
        await Publish(Decided(CaseFamilies.Complaint, CaseOutcomes.Upheld, links));
        (await Status("general.claim", general), await Status("life.claim", life), await Status("health.medical_claim", health))
            .ShouldBe(("DECLINED", "DECLINED", "REJECTED"));

        var upheld = Decided(CaseFamilies.Dispute, CaseOutcomes.PartlyUpheld, links);
        await Publish(upheld);
        (await Status("general.claim", general), await Status("life.claim", life), await Status("health.medical_claim", health), await Status("general.claim", openGeneral))
            .ShouldBe(("REOPENED", "IN_ASSESSMENT", "VALIDATED", "UNDER_ASSESSMENT"));
        await Drain();
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM audit.audit_event WHERE operation = 'ReopenClaim' AND target_id = ANY(@ids) AND details->>'case' = 'CS-UPHELD'",
            ("ids", new[] { general.ToString(), life.ToString(), health.ToString() }))).ShouldBe(3L, "each reopen is audited with the case as its reason");

        // An appeal upheld again (a second event): the claims are open, so nothing changes.
        await Publish(Decided(CaseFamilies.Appeal, CaseOutcomes.Upheld, links));
        (await Status("general.claim", general), await Status("life.claim", life), await Status("health.medical_claim", health))
            .ShouldBe(("REOPENED", "IN_ASSESSMENT", "VALIDATED"));
    }

    [Fact]
    public async Task APartialHealthApproval_RecordsItsReason_AndSendsClaimDecisionRights_OQ102()
    {
        await fixture.Database.SeedStatic(Tenant, "claims.ref_claim_decision_reason_codes", "ACTIVE", "BENEFIT_LIMIT", From, ("decision_type", "PARTIAL"),
            ("customer_wording", "Part of the claim is above your benefit limit."));
        await fixture.Database.SeedStatic(Tenant, "claims.ref_claim_decision_reason_codes", "ACTIVE", "PAID_IN_FULL", From, ("decision_type", "APPROVE"),
            ("customer_wording", "Paid."));
        await fixture.Database.UserWithPermissions(Tenant, "case-health-approver", HealthPermissions.ClaimApprove, HealthPermissions.ClaimRead);
        var approver = _host.CreateClient();
        approver.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(Tenant, "case-health-approver", DateTime.UtcNow));
        var claim = await HealthClaim("ADJUDICATED", 1_000m, 600m);
        var url = $"/api/v1/health/claims/{claim}/approve";

        var noReason = await approver.Send(HttpMethod.Post, url);
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await noReason.Content.ReadAsStringAsync());
        (await approver.Send(HttpMethod.Post, url, new { reasonCode = "PAID_IN_FULL" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the reason is not PARTIAL");
        var approved = await approver.Send(HttpMethod.Post, url, new { reasonCode = "BENEFIT_LIMIT" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        var view = await approved.Json();
        (view.GetProperty("outcome").GetString(), view.GetProperty("decisionReasonCode").GetString()).ShouldBe(("PARTLY_APPROVED", "BENEFIT_LIMIT"));
        (await fixture.Database.ScalarAsOwner<string>("SELECT decision_outcome || ' ' || decision_reason_code FROM health.medical_claim WHERE id = @id", ("id", claim)))
            .ShouldBe("PARTLY_APPROVED BENEFIT_LIMIT");

        await Drain();
        var rights = (await Notifications(claim)).Single(r => r.Type == NotificationTypes.ClaimDecisionRights);
        (rights.SourceType, rights.Reference).ShouldBe(("HEALTH_CLAIM", $"HC-{claim:N}"[..25]));
        rights.Merge.GetProperty("amount").GetString().ShouldBe("600.00");
        rights.Merge.TryGetProperty("decision_reasons", out _).ShouldBeFalse("a Health notice never carries a reason (HLT-008)");
    }

    [Fact]
    public async Task ApprovedRedress_IsPaidOutToThePayeesAccount_ThroughAPayoutBatch_Once_OQ104()
    {
        await fixture.Database.SeedStatic(Tenant, "payment.ref_payment_methods", "ACTIVE", "EFT", From, ("method_code", "EFT"), ("direction", "PAY"),
            ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await fixture.Database.SeedStatic(Tenant, "payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", From, ("provider_code", "SIMULATOR"),
            ("type", "PAYMENT_GATEWAY"), ("supported_methods", new[] { "EFT" }), ("credential_ref", "vault:simulator"));
        var redress = Guid.CreateVersion7();
        var payee = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var approved = Envelope("workflow.CaseRedressApproved", redress,
            new CaseRedressApproved(redress, Guid.CreateVersion7(), "CS-0003", "GOODWILL", 750.25m, "ZAR", payee, account));
        await Publish(approved);
        await Publish(approved);

        var payment = await fixture.Database.ScalarAsOwner<string>("""
            SELECT direction || ' ' || purpose || ' ' || business_ref_type || ' ' || business_ref_id || ' ' || amount || ' ' || counterparty_party_id || ' ' || bank_account_id || ' ' || count(*) OVER ()
            FROM payment.payment WHERE idempotency_key = @k
            """, ("k", $"CASE_REDRESS:{redress:N}"));
        payment.ShouldBe($"OUT {CaseApprovals.RedressPaymentPurpose} {CaseApprovals.RedressBusinessRefType} {redress} 750.25 {payee} {account} 1");

        await fixture.Database.UserWithPermissions(Tenant, "case-payout-builder", Payments.Contracts.PaymentPermissions.PayoutCreate, Payments.Contracts.PaymentPermissions.PayoutRead);
        var builder = _host.CreateClient();
        builder.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(Tenant, "case-payout-builder", DateTime.UtcNow));
        var batch = await builder.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = "CASE_REDRESS", currency = "ZAR" }, idempotent: true);
        batch.StatusCode.ShouldBe(HttpStatusCode.Created, await batch.Content.ReadAsStringAsync());
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM payment.payment WHERE idempotency_key = @k AND payout_batch_id IS NOT NULL",
            ("k", $"CASE_REDRESS:{redress:N}"))).ShouldBe(1L, "the redress is paid through the batch");

        // No account named and none verified for the payee: parked until the payee has one.
        var unpaid = Guid.CreateVersion7();
        var noAccount = Envelope("workflow.CaseRedressApproved", unpaid,
            new CaseRedressApproved(unpaid, Guid.CreateVersion7(), "CS-0004", "COMPENSATION", 100m, "ZAR", Guid.CreateVersion7(), null));
        await Publish(noAccount);
        (await fixture.Database.ScalarAsOwner<string>(
            "SELECT prerequisite_code FROM integration.parked_delivery WHERE event_id = @e AND consumer LIKE 'payment.%' AND status = 'PARKED'", ("e", noAccount.EventId)))
            .ShouldBe(PaymentPrerequisites.PayeeAccountNotPayable);
    }

    private static EventEnvelope Decided(string family, string outcome, IReadOnlyList<CaseLinkRef> links)
    {
        var caseId = Guid.CreateVersion7();
        Notices.Cases[caseId] = new CaseNoticeData("CS-UPHELD", "CLAIM_DISPUTE", family, null, "Upheld", null, null, null, null, null, null);
        return Envelope("workflow.CaseDecided", caseId, new CaseDecided(caseId, "CS-UPHELD", "CLAIM_DISPUTE", family, outcome, null, links));
    }

    private async Task<Guid> GeneralClaim(string status)
    {
        var (policy, claim) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await fixture.Database.ExecuteAsOwner($"""
            INSERT INTO general.policy (tenant_id, id, {Audit}, policy_number, product_code, product_version, rating_version, policyholder_party_id, status,
                                        period_start, period_end, currency, annual_premium, billing_account_ref, cover_starts_on)
            VALUES (@t, @p, {Stamp}, @pn, 'MOTOR', 1, 1, gen_random_uuid(), 'IN_FORCE', current_date - 10, current_date + 355, 'ZAR', 1200, gen_random_uuid(), 'ISSUE');
            INSERT INTO general.claim (tenant_id, id, {Audit}, claim_number, policy_id, loss_date, notified_at, cause_code, peril_code, status, coverage_code)
            VALUES (@t, @c, {Stamp}, @cn, @p, current_date, now(), 'COLLISION', 'ACC', @s, 'OWN_DAMAGE');
            """, ("t", Tenant), ("p", policy), ("c", claim), ("pn", "GP-" + policy.ToString("N")[..12]), ("cn", "GC-" + claim.ToString("N")[..20]), ("s", status));
        return claim;
    }

    private async Task<Guid> LifeClaim(string status)
    {
        var (policy, claim) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await fixture.Database.ExecuteAsOwner($"""
            INSERT INTO life.policy (tenant_id, id, {Audit}, policy_number, proposal_id, product_code, product_version, rating_version, policyholder_party_id, status,
                                     inception_date, currency, premium_frequency, billing_account_ref)
            VALUES (@t, @p, {Stamp}, @pn, gen_random_uuid(), 'TERM', 1, 1, gen_random_uuid(), 'IN_FORCE', current_date - 100, 'ZAR', 'MONTHLY', gen_random_uuid());
            INSERT INTO life.claim (tenant_id, id, {Audit}, claim_number, policy_id, claim_type, event_date, claimant_party_id, status, insured_life_id, currency, notified_at)
            VALUES (@t, @c, {Stamp}, @cn, @p, 'DEATH', current_date - 5, gen_random_uuid(), @s, gen_random_uuid(), 'ZAR', now());
            """, ("t", Tenant), ("p", policy), ("c", claim), ("pn", "LP-" + policy.ToString("N")[..12]), ("cn", "LC-" + claim.ToString("N")[..20]), ("s", status));
        return claim;
    }

    private async Task<Guid> HealthClaim(string status, decimal claimed, decimal paid)
    {
        var (membership, claim, principal) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await fixture.Database.ExecuteAsOwner($"""
            INSERT INTO health.membership (tenant_id, id, {Audit}, member_number, principal_party_id, plan_code, option_code, plan_version, status, start_date, currency)
            VALUES (@t, @m, {Stamp}, @mn, @pp, 'HLT-CORE', 'A', 1, 'ACTIVE', current_date - 100, 'ZAR');
            INSERT INTO health.medical_claim (tenant_id, id, {Audit}, claim_number, source, provider_id, membership_id, patient_party_id, service_date, submitted_at,
                                              format_code, total_claimed, total_paid, status, currency)
            VALUES (@t, @c, {Stamp}, @cn, 'PROVIDER', 'PRV-1', @m, @pp, current_date - 3, now(), 'API', @claimed, @paid, @s, 'ZAR');
            """, ("t", Tenant), ("m", membership), ("c", claim), ("pp", principal), ("mn", "M" + membership.ToString("N")[..10]),
            ("cn", $"HC-{claim:N}"[..25]), ("claimed", claimed), ("paid", paid), ("s", status));
        return claim;
    }

    private Task<string?> Status(string table, Guid id) => fixture.Database.ScalarAsOwner<string>($"SELECT status FROM {table} WHERE id = @id", ("id", id));

    private async Task<List<(string Type, Guid Recipient, string SourceType, string? Reference, JsonElement Merge)>> Notifications(Guid sourceId)
    {
        var rows = new List<(string, Guid, string, string?, JsonElement)>();
        var json = await fixture.Database.ScalarAsOwner<string>("""
            SELECT coalesce(json_agg(json_build_object('t', type_code, 'r', recipient_party_id, 's', source_type, 'ref', source_reference, 'm', merge_values)), '[]')::text
            FROM communication.notification WHERE source_id = @s
            """, ("s", sourceId));
        foreach (var row in JsonDocument.Parse(json!).RootElement.EnumerateArray())
        {
            rows.Add((row.GetProperty("t").GetString()!, row.GetProperty("r").GetGuid(), row.GetProperty("s").GetString()!,
                row.GetProperty("ref").ValueKind == JsonValueKind.Null ? null : row.GetProperty("ref").GetString(), row.GetProperty("m").Clone()));
        }

        return rows;
    }

    private Task Publish(EventEnvelope envelope) => _host.Services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);

    private async Task Drain()
    {
        var processor = _host.Services.GetRequiredService<Insys.BuildingBlocks.Infrastructure.Messaging.OutboxProcessor>();
        for (var pass = 0; pass < 10 && await processor.ProcessOnce(CancellationToken.None) > 0; pass++)
        {
        }
    }

    private static EventEnvelope Envelope<T>(string type, Guid aggregate, T payload, Guid? tenant = null) =>
        new(Guid.CreateVersion7(), tenant ?? Tenant, type, 1, DateTimeOffset.UtcNow, "Case", aggregate, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(payload, ModuleDbContext.EventJson));

    /// <summary>Workflow's notice data, as a test double: the case's values, by case ID.</summary>
    private sealed class StubCaseNotices : ICaseNotices
    {
        public ConcurrentDictionary<Guid, CaseNoticeData> Cases { get; } = new();

        public Task<CaseNoticeData?> ForNotice(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult(Cases.TryGetValue(caseId, out var data) ? data : null);
    }
}
