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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.Distribution.Contracts;
using Insys.Documents.Contracts;
using Insys.Finance.Contracts;
using Insys.General.Contracts;
using Insys.Health.Contracts;
using Insys.Identity.Contracts;
using Insys.Integration.Contracts;
using Insys.Life.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Contracts;
using Insys.Rules.Contracts;
using Insys.StaticData.Contracts;
using Insys.Tenancy.Contracts;
using Insys.Workflow.Contracts;
using Insys.Workflow.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The rule signed off on 25 October 2026 (front-end integration guide §1, OQ-113, D-1 to D-5): every way an approval can
/// end (approved, rejected, expired, withdrawn) leaves its record in a defined state, never waiting for good. One row per
/// approval type and ending, each in a tenant of its own, through the real host: the waiting record is made as its module
/// makes it, the ending is driven through Workflow's endpoints (or its expiry job), the outbox is relayed, and the record is
/// read back. Expected states are the guide's §1 table.
/// </summary>
public sealed class ApprovalEndingsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Approved = "APPROVED";
    private const string Rejected = "REJECTED";
    private const string Expired = "EXPIRED";
    private const string Withdrawn = "WITHDRAWN";
    private const string RejectReason = "Checker says no";
    private const string WithdrawReason = "Requester no longer wants it";
    private static readonly DateOnly Today = TestClock.Today;
    private static readonly DateOnly From = Today.AddYears(-2);
    private static readonly string Month = Today.ToString("yyyy-MM");

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static TheoryData<string, string> Rows
    {
        get
        {
            var rows = new TheoryData<string, string>();
            foreach (var type in Cases.Keys)
            {
                foreach (var ending in new[] { Approved, Rejected, Expired, Withdrawn })
                {
                    rows.Add(type, ending);
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task EveryEnding_LeavesTheRecordInItsDefinedState_NeverWaitingForGood_OQ113(string approvalType, string ending)
    {
        var @case = Cases[approvalType];
        var world = await World.Create(fixture, _factory, @case);
        var waiting = await @case.Setup(world);
        await _factory.DrainOutbox();

        var approval = await fixture.Database.ScalarAsOwner<Guid?>("""
            SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @i AND status = 'REQUESTED'
            """, ("t", world.Tenant), ("r", waiting.BusinessRefType), ("i", waiting.BusinessRefId));
        approval.ShouldNotBeNull($"no {approvalType} approval waits for {waiting.BusinessRefType} {waiting.BusinessRefId}");
        (await fixture.Database.ScalarAsOwner<string>("SELECT approval_type FROM workflow.approval WHERE id = @a", ("a", approval.Value))).ShouldBe(approvalType);

        var problems = new List<string>();
        Expect(problems, "while waiting", await waiting.State(), @case.WhileWaiting);

        var ended = await End(world, approval.Value, ending);
        if (ended is not null)
        {
            problems.Add(ended);
        }
        else
        {
            await _factory.DrainOutbox();
            Expect(problems, "approval status", await fixture.Database.ScalarAsOwner<string>("SELECT status FROM workflow.approval WHERE id = @a", ("a", approval.Value)),
                ending);
            Expect(problems, $"record after {ending}", await waiting.State(), ending == Approved ? @case.WhenApproved : [@case.WhenNotApproved]);

            var parked = await fixture.Database.ScalarAsOwner<long>("""
                SELECT count(*) FROM integration.parked_delivery
                WHERE tenant_id = @t AND prerequisite_code = 'APPROVAL_ENDING_NOT_APPLIED' AND prerequisite_item LIKE '%' || @a
                """, ("t", world.Tenant), ("a", approval.Value.ToString()));
            if (parked != 0)
            {
                problems.Add($"the ending was parked APPROVAL_ENDING_NOT_APPLIED ({parked})");
            }

            if (ending == Expired)
            {
                var followUps = await fixture.Database.ScalarAsOwner<long>("""
                    SELECT count(*) FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
                    WHERE p.tenant_id = @t AND p.process_code = 'APPROVAL_FOLLOW_UP' AND p.business_ref_id = @r AND t.assignee_id = @m
                    """, ("t", world.Tenant), ("r", waiting.BusinessRefId), ("m", world.MakerId));
                if (followUps != 1)
                {
                    problems.Add($"the requester has {followUps} APPROVAL_FOLLOW_UP tasks, not 1");
                }
            }

            if (ending != Approved && waiting.View is not null)
            {
                await LatestRejection(problems, world, await waiting.View(), approvalType, ending);
            }

            if (waiting.After is not null)
            {
                problems.AddRange(await waiting.After(ending));
            }
        }

        problems.ShouldBeEmpty($"{approvalType} {ending}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    public static TheoryData<string> Types => new(Cases.Keys);

    /// <summary>
    /// D-5, extended (25 October 2026): a granted approval its record will not take, because the record changed after it was
    /// submitted, is never only logged. The module's delivery parks (APPROVAL_NOT_APPLIED, with an alert), the approval is
    /// NOT_APPLIED naming the module, the requester gets a follow-up task, and the record is left as the change left it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public async Task AGrantTheRecordWillNotTake_Parks_IsMarkedNotApplied_AndTellsTheRequester_D5(string approvalType)
    {
        var @case = Cases[approvalType];
        @case.Refuse.ShouldNotBeNull($"{approvalType} has no change that makes its record refuse the approval");
        var world = await World.Create(fixture, _factory, @case);
        var waiting = await @case.Setup(world);
        await _factory.DrainOutbox();
        var approval = (await fixture.Database.ScalarAsOwner<Guid?>("""
            SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @i AND status = 'REQUESTED'
            """, ("t", world.Tenant), ("r", waiting.BusinessRefType), ("i", waiting.BusinessRefId))).ShouldNotBeNull();

        await @case.Refuse(world, waiting);
        var problems = new List<string>();
        if (await End(world, approval, Approved) is { } failed)
        {
            problems.Add(failed);
        }
        else
        {
            await _factory.DrainOutbox();
            Expect(problems, "approval status", await fixture.Database.ScalarAsOwner<string>("SELECT status FROM workflow.approval WHERE id = @a", ("a", approval)),
                "NOT_APPLIED");
            Expect(problems, "approval not applied by",
                await fixture.Database.ScalarAsOwner<string>("SELECT not_applied_by FROM workflow.approval WHERE id = @a", ("a", approval)) is { Length: > 0 } ? "a module" : null,
                "a module");
            Expect(problems, $"record after the refused approval", await waiting.State(), @case.WhenRefused);
            var parked = await fixture.Database.ScalarAsOwner<long>("""
                SELECT count(*) FROM integration.parked_delivery
                WHERE tenant_id = @t AND status = 'PARKED' AND prerequisite_code = 'APPROVAL_NOT_APPLIED' AND prerequisite_item LIKE '%' || @a
                """, ("t", world.Tenant), ("a", approval.ToString()));
            Expect(problems, "parked APPROVAL_NOT_APPLIED deliveries", parked.ToString(), "1");
            var followUps = await fixture.Database.ScalarAsOwner<long>("""
                SELECT count(*) FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
                WHERE p.tenant_id = @t AND p.process_code = 'APPROVAL_FOLLOW_UP' AND p.business_ref_id = @r AND t.assignee_id = @m
                """, ("t", world.Tenant), ("r", waiting.BusinessRefId), ("m", world.MakerId));
            Expect(problems, "the requester's APPROVAL_FOLLOW_UP tasks", followUps.ToString(), "1");
        }

        problems.ShouldBeEmpty($"{approvalType} refused:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    private async Task<string?> End(World world, Guid approval, string ending)
    {
        if (ending == Expired)
        {
            await fixture.Database.ExecuteAsOwner("UPDATE workflow.approval SET expires_at = now() - interval '1 minute' WHERE id = @id", ("id", approval));
            await _factory.Services.GetRequiredService<WorkflowJobs>().Run(CancellationToken.None);
            return null;
        }

        var read = await world.Checker.GetAsync($"/api/v1/workflow/approvals/{approval}");
        if (read.StatusCode != HttpStatusCode.OK)
        {
            return $"reading the approval returned {read.StatusCode}: {await read.Content.ReadAsStringAsync()}";
        }

        var ifMatch = $"\"{(await read.Json()).GetProperty("rowVersion").GetInt64()}\"";
        var response = ending switch
        {
            Approved => await world.Checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: ifMatch),
            Rejected => await world.Checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = RejectReason }, ifMatch: ifMatch),
            _ => await world.Maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/withdraw", new { reason = WithdrawReason }, ifMatch: ifMatch),
        };
        return response.StatusCode == HttpStatusCode.NoContent ? null : $"{ending} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
    }

    /// <summary>The record's own view shows how its approval ended (OQ-113): every record an approval is about has latestRejection.</summary>
    private static Task LatestRejection(List<string> problems, World world, JsonElement view, string approvalType, string ending)
    {
        if (!view.TryGetProperty("latestRejection", out var latest))
        {
            problems.Add("the record's view has no latestRejection (OQ-113)");
            return Task.CompletedTask;
        }

        if (latest.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"latestRejection is {latest.ValueKind}");
            return Task.CompletedTask;
        }

        Expect(problems, "latestRejection.ending", latest.GetProperty("ending").GetString(), ending);
        Expect(problems, "latestRejection.approvalType", latest.GetProperty("approvalType").GetString(), approvalType);
        var by = latest.GetProperty("by");
        Expect(problems, "latestRejection.by", by.ValueKind == JsonValueKind.Null ? null : by.GetGuid().ToString(), ending == Rejected ? world.CheckerId.ToString() : null);
        var reason = latest.GetProperty("reason").GetString();
        if (ending == Expired ? string.IsNullOrWhiteSpace(reason) : reason != (ending == Rejected ? RejectReason : WithdrawReason))
        {
            problems.Add($"latestRejection.reason is '{reason}'");
        }

        return Task.CompletedTask;
    }

    private static void Expect(List<string> problems, string what, string? actual, params string?[] expected)
    {
        if (!expected.Contains(actual))
        {
            problems.Add($"{what}: expected {string.Join(" or ", expected.Select(e => e ?? "null"))}, was {actual ?? "null"}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The cases: how each approval type's record is made to wait, how its state is read, and the guide's expected states.
    // ------------------------------------------------------------------------------------------------------------------

    /// <param name="Setup">Makes the record wait for an approval of the type; returns what the approval is about and how to read the record.</param>
    /// <param name="WhileWaiting">The record's state while the approval waits.</param>
    /// <param name="WhenApproved">Its state once approved (and any follow-on state the module reaches by itself).</param>
    /// <param name="WhenNotApproved">Its state once rejected, expired or withdrawn.</param>
    /// <param name="Operator">The record is the platform operator's (a first tenant administrator).</param>
    /// <param name="Refuse">Changes the waiting record, as a user or the module could after it was submitted, so that it will not take the approval (D-5).</param>
    /// <param name="WhenRefused">Its state once the approval is granted after that change: as the change left it.</param>
    private sealed record Case(string[] Permissions, string[] AuthorityTypes, Func<World, Task<Waiting>> Setup, string WhileWaiting, string[] WhenApproved,
        string WhenNotApproved, bool Operator = false, Func<World, Waiting, Task>? Refuse = null, string? WhenRefused = null);

    /// <param name="State">The record's state, as a short text compared with the case's expected states.</param>
    /// <param name="View">The record's own view, for latestRejection.</param>
    /// <param name="After">Further checks after the ending, returning any problems.</param>
    private sealed record Waiting(string BusinessRefType, Guid BusinessRefId, Func<Task<string>> State, Func<Task<JsonElement>>? View = null,
        Func<string, Task<IReadOnlyList<string>>>? After = null);

    private static readonly string[] Approving = [WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove];

    private static readonly string[] StaticPermissionSet =
    [
        StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update, StaticDataPermissions.Approve,
        StaticDataGovernancePermissions.ChangeRequestRead, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    // Built on first use: the permission sets further down are static fields initialised in textual order.
    private static readonly Lazy<Dictionary<string, Case>> AllCases = new(BuildCases);

    private static Dictionary<string, Case> Cases => AllCases.Value;

    private static Dictionary<string, Case> BuildCases() => new()
    {
        [GeneralApprovals.SettlementApprovalType] = new(GeneralPermissionSet, [GeneralApprovals.SettlementApprovalType, "BANK_ACCOUNT_VERIFICATION"], GeneralSettlement,
            "UnderAssessment|settlement pending", ["Approved"], "UnderAssessment|no settlement", Refuse: RefuseGeneralSettlement,
            WhenRefused: "UnderAssessment|settlement pending"),
        [LifeApprovals.ClaimApprovalType] = new(LifePermissionSet, [PaymentApprovals.PayoutApprovalType, "BANK_ACCOUNT_VERIFICATION"], LifeClaim,
            "InAssessment|approval pending|payees 1", ["Approved|payees 1"], "InAssessment|payees 0", Refuse: RefuseLifeClaim,
            WhenRefused: "InAssessment|approval pending|payees 1"),
        [LifeApprovals.UnderwritingApprovalType] = new(LifePermissionSet, [LifeApprovals.UnderwritingApprovalType], LifeUnderwriting,
            "InAssessment|decision pending", ["Decided"], "InAssessment", Refuse: RefuseLifeUnderwriting, WhenRefused: "InAssessment|decision pending"),
        [GeneralApprovals.EndorsementApprovalType] = new(GeneralPermissionSet, [GeneralApprovals.EndorsementApprovalType], GeneralEndorsement,
            "PendingApproval", ["Approved", "Applied"], "Rejected", Refuse: RefuseGeneralEndorsement, WhenRefused: "PendingApproval"),
        [ClaimsApprovals.InstructionApprovalType] = new(MoneyPermissionSet, [ClaimsApprovals.InstructionApprovalType, "BANK_ACCOUNT_VERIFICATION"], ClaimPayment,
            "PendingApproval", ["Approved"], "Rejected"),
        [BillingApprovals.RefundApprovalType] = new(MoneyPermissionSet, [BillingApprovals.RefundApprovalType, "BANK_ACCOUNT_VERIFICATION"], Refund,
            "Requested", ["Approved"], "Rejected"),
        [DistributionApprovals.StatementApprovalType] = new(CommissionPermissionSet, [DistributionApprovals.StatementApprovalType, "BANK_ACCOUNT_VERIFICATION"],
            CommissionStatement, "Draft|approval pending", ["Approved"], "Draft"),
        [FinanceApprovals.ReopenApprovalType] = new([FinancePermissions.PeriodRead, FinancePermissions.PeriodUpdate, .. Approving], [FinanceApprovals.ReopenApprovalType],
            PeriodReopen, "SoftClose|reopen requested", ["Open"], "SoftClose"),
        [ProductApprovals.ApprovalType] = new(ProductPermissionSet, [ProductApprovals.ApprovalType], ProductPublication, "Submitted", ["Approved", "Published"], "Draft",
            Refuse: RefuseProductPublication, WhenRefused: "Draft"),
        ["STATIC_DATA_T1"] = new(StaticPermissionSet, ["STATIC_DATA_T1"], w => StaticDataChange(w, tier1: true),
            "version SUBMITTED|change PENDING_APPROVAL", ["version APPROVED|change APPROVED"], "version DRAFT|change REJECTED", Refuse: RefuseStaticDataChange,
            WhenRefused: "version SUBMITTED|change REJECTED"),
        ["STATIC_DATA_T2"] = new(StaticPermissionSet, ["STATIC_DATA_T2"], w => StaticDataChange(w, tier1: false),
            "version SUBMITTED|change PENDING_APPROVAL", ["version APPROVED|change APPROVED"], "version DRAFT|change REJECTED", Refuse: RefuseStaticDataChange,
            WhenRefused: "version SUBMITTED|change REJECTED"),
        [IdentityApprovals.ApprovalType] = new([IdentityPermissions.UserRead, IdentityPermissions.UserCreate, IdentityPermissions.UserUpdate, .. Approving],
            [IdentityApprovals.ApprovalType], PrivilegedRole, "PendingApproval", ["Active"], "Rejected", Refuse: RefusePrivilegedRole, WhenRefused: "Revoked"),
        [IdentityApprovals.SubjectLinkApprovalType] = new([IdentityPermissions.UserRead, IdentityPermissions.UserUpdate, PartyPermissions.PartyCreate, .. Approving],
            [IdentityApprovals.SubjectLinkApprovalType], SubjectLink, "PENDING", ["ACTIVE"], "REJECTED", Refuse: RefuseSubjectLink, WhenRefused: "ENDED"),
        [IdentityApprovals.TenantAdministratorApprovalType] = new(
            [TenancyPermissions.Read, TenancyPermissions.Create, TenancyPermissions.Update, IdentityPermissions.UserRead, IdentityPermissions.UserCreate, .. Approving],
            [IdentityApprovals.TenantAdministratorApprovalType], TenantAdministrator, "PendingApproval", ["Provisioned"], "Rejected", Operator: true,
            Refuse: RefuseTenantAdministrator, WhenRefused: "PendingApproval"),
        [PartyApprovals.MatchResolutionType] = new([PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate, .. Approving],
            [PartyApprovals.MatchResolutionType], PartyMatch, "PROPOSED", ["LINKED"], "PENDING_REVIEW", Refuse: RefusePartyMatch, WhenRefused: "PROPOSED"),
        ["BANK_ACCOUNT_VERIFICATION"] = new(
            [PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
             .. Approving],
            ["BANK_ACCOUNT_VERIFICATION"], BankAccount, "new PENDING_VERIFICATION|previous ACTIVE", ["new ACTIVE|previous SUPERSEDED"], "new REJECTED|previous ACTIVE",
            Refuse: RefuseBankAccount, WhenRefused: "new PENDING_VERIFICATION|previous ACTIVE"),
        [HealthApprovals.RunApprovalType] = new(HealthPermissionSet, [HealthApprovals.RunApprovalType, "BANK_ACCOUNT_VERIFICATION"], ProviderPaymentRun,
            "Calculated", ["Approved"], "Rejected"),
        [PaymentApprovals.PayoutApprovalType] = new(MoneyPermissionSet, [PaymentApprovals.PayoutApprovalType, BillingApprovals.RefundApprovalType, "BANK_ACCOUNT_VERIFICATION"],
            PayoutBatch, "Built", ["Approved"], "Rejected"),
    };

    /// <summary>A tenant of its own for one row, with a maker and a checker (another user, holding authority for the case's types).</summary>
    private sealed class World
    {
        public Guid Tenant { get; private init; }

        public string Code { get; private init; } = "";

        public required TestDatabase Db { get; init; }

        public required InsysApiFactory Factory { get; init; }

        public HttpClient Maker { get; private set; } = null!;

        public HttpClient Checker { get; private set; } = null!;

        public Guid MakerId { get; private set; }

        public Guid CheckerId { get; private set; }

        public string MakerRole { get; private set; } = "";

        public string ApproverRole { get; private set; } = "";

        public string Suffix { get; private init; } = "";

        public static async Task<World> Create(DatabaseFixture fixture, InsysApiFactory factory, Case @case)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenant = @case.Operator ? DatabaseFixture.Operator : Guid.CreateVersion7();
            var world = new World { Db = fixture.Database, Factory = factory, Tenant = tenant, Code = "AE" + suffix.ToUpperInvariant(), Suffix = suffix };
            if (!@case.Operator)
            {
                await world.Db.SeedTenant(tenant, world.Code);
            }

            world.MakerId = await world.Db.UserWithPermissions(tenant, $"maker-{suffix}", @case.Permissions);
            world.CheckerId = await world.Db.UserWithPermissions(tenant, $"checker-{suffix}", @case.Permissions);
            world.MakerRole = await world.Db.RoleWithPermissions(tenant);
            await world.Db.AssignRole(tenant, world.MakerId, world.MakerRole);
            world.ApproverRole = await world.Db.RoleWithPermissions(tenant);
            await world.Db.AssignRole(tenant, world.CheckerId, world.ApproverRole);
            foreach (var type in @case.AuthorityTypes)
            {
                await world.Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}-{suffix}", ("role_code", world.ApproverRole),
                    ("transaction_type", type), ("limit_value", 100_000_000m), ("delegation_allowed", false));
            }

            world.Maker = factory.ClientFor(tenant, $"maker-{suffix}");
            world.Checker = factory.ClientFor(tenant, $"checker-{suffix}");
            return world;
        }

        public Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
            Db.SeedStatic(Tenant, table, status, code, From, fields);

        public async Task<Guid> Created(string url, object body)
        {
            var response = await Maker.Send(HttpMethod.Post, url, body, idempotent: true);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, $"{url}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Json()).GetProperty("id").GetGuid();
        }

        public async Task<JsonElement> Get(string url, HttpClient? client = null)
        {
            var response = await (client ?? Maker).GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{url}: {await response.Content.ReadAsStringAsync()}");
            return await response.Json();
        }

        public async Task Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) =>
            response.StatusCode.ShouldBe(expected, $"{response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");

        /// <summary>The checker approves another approval the setup needs on the way (a refund before its payout batch).</summary>
        public async Task ApproveOnTheWay(string businessRefType, Guid businessRefId)
        {
            await Factory.DrainOutbox();
            var approval = await Db.ScalarAsOwner<Guid>("""
                SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @i AND status = 'REQUESTED'
                """, ("t", Tenant), ("r", businessRefType), ("i", businessRefId));
            await Ok(await Checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\""), HttpStatusCode.NoContent);
            await Factory.DrainOutbox();
        }

        public async Task Locale() =>
            await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE", ("tenant", Code), ("base_currency", "ZAR"), ("languages", new[] { "en" }),
                ("default_language", "en"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));

        /// <summary>Finance: accounts, posting rules and the months given opened.</summary>
        public async Task Finance(params int[] monthsBack)
        {
            foreach (var account in new[] { "1100", "1200", "2100", "2200", "4000", "5000", "6000" })
            {
                await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", ("account_code", account), ("name", account), ("type", "ASSET"),
                    ("gl_account", "GL" + account), ("status", "ACTIVE"));
            }

            foreach (var (rule, type, debit, credit) in new[]
                     {
                         ("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"), ("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"),
                         ("PR-CREDIT", TransactionTypes.PremiumCredited, "4000", "1100"), ("PR-CLM-UP", TransactionTypes.ClaimReserveIncrease, "5000", "2100"),
                         ("PR-CLM-PAID", TransactionTypes.ClaimPaid, "2100", "1200"), ("PR-REFUND", TransactionTypes.RefundPaid, "2100", "1200"),
                         ("PR-REVERSED", TransactionTypes.PaymentReversed, "1100", "1200"),
                     })
            {
                await Seed("finance.cfg_posting_rules", "EFFECTIVE", rule, ("rule_code", rule), ("transaction_type", type), ("debit_account", debit), ("credit_account", credit));
            }

            var month = new DateOnly(Today.Year, Today.Month, 1);
            foreach (var back in monthsBack.OrderByDescending(m => m))
            {
                var period = month.AddMonths(-back);
                await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{period:yyyy-MM}", ("period_code", $"{period:yyyy-MM}"), ("start_date", period),
                    ("end_date", period.AddMonths(1).AddDays(-1)));
                await Ok(await Maker.PostAsync($"/api/v1/finance/periods/{period:yyyy-MM}/open", null));
            }
        }

        /// <summary>Billing and Payments: monthly debit orders and EFT payouts through the simulator provider.</summary>
        public async Task Money()
        {
            await Seed("billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", ("label", "Monthly"), ("periods_per_year", 12));
            await Seed("billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("payment_method", "DEBIT_ORDER"),
                ("lead_time", TimeSpan.FromDays(2)));
            await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "OVERPAID", ("label", "Overpaid"), ("requires_approval", false),
                ("accounting_impact", "REFUND"));
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
        }

        /// <summary>A payee registered and its bank account verified through Workflow (the checker holds BANK_ACCOUNT_VERIFICATION authority).</summary>
        public async Task<(Guid Party, Guid Account)> Payee(string surname, string number)
        {
            var party = await Maker.RegisterPerson(surname);
            return (party, await Maker.AddVerifiedBankAccount(Checker, Factory, party, number));
        }
    }

    // ---- Party, Identity, Static Data -------------------------------------------------------------------------------------

    private static async Task<Waiting> BankAccount(World w)
    {
        var (party, previous) = await w.Payee("Account", "62000111222");
        var added = await w.Maker.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/bank-accounts", new
        {
            accountNumber = "62000333444", bankCode = "632005", accountHolderName = "Holder", accountType = "CURRENT", currency = "ZAR",
        }, idempotent: true);
        await w.Ok(added, HttpStatusCode.Created);
        var account = (await added.Json()).GetProperty("id").GetGuid();

        async Task<string> State()
        {
            var accounts = (await w.Get($"/api/v1/party/parties/{party}/bank-accounts")).EnumerateArray().ToList();
            string StatusOf(Guid id) => accounts.Single(a => a.GetProperty("id").GetGuid() == id).GetProperty("status").GetString()!;
            return $"new {StatusOf(account)}|previous {StatusOf(previous)}";
        }

        return new(PartyApprovals.BankAccountBusinessRefType, account, State);
    }

    /// <summary>
    /// No API changes an account once it waits for verification (another account withdraws its approval instead), so an
    /// owner update stands for one changed since it was submitted: its number's hash, which the approval's subject binds.
    /// </summary>
    private static Task RefuseBankAccount(World w, Waiting waiting) =>
        w.Db.ExecuteAsOwner("UPDATE party.bank_account SET account_number_hash = repeat('f', 64) WHERE tenant_id = @t AND id = @a",
            ("t", w.Tenant), ("a", waiting.BusinessRefId));

    private static async Task<Waiting> PartyMatch(World w)
    {
        var known = await w.Maker.RegisterPerson("Known");
        var broker = await w.Db.UserWithPermissions(w.Tenant, $"broker-{w.Suffix}", PartyPermissions.PartyCreate);
        await w.Db.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'INTERMEDIARY', NULL, 'BRK-A', 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", w.Tenant), ("u", broker));
        await w.Seed("commission.mst_intermediary_register", "ACTIVE", "BRK-A", ("intermediary_code", "BRK-A"), ("type", "BROKER"), ("legal_name", "BRK-A"),
            ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", "ACTIVE"), ("hierarchy_node", "NODE-BRK-A"));
        await w.Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-BRK", ("channel_code", "BRK"), ("audience", "BROKER"), ("name", "Broker portal"),
            ("client_ids", new[] { "broker-portal" }));
        await w.Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-STF", ("channel_code", "STF"), ("audience", "STAFF"), ("name", "Back office"),
            ("client_ids", new[] { "insys-web" }));

        var portal = w.Factory.ClientFor(w.Tenant, $"broker-{w.Suffix}", "broker-portal", multiFactor: true);
        var registered = await portal.Send(HttpMethod.Post, "/api/v1/portal/party/parties",
            new { individual = new { firstNames = "Test", surname = "Known", birthDate = "1985-06-15", nationality = "ZA" } }, idempotent: true);
        await w.Ok(registered, HttpStatusCode.Accepted);
        var registration = (await registered.Json()).GetProperty("id").GetGuid();
        await w.Factory.DrainOutbox();
        var reviewed = await w.Get($"/api/v1/party/parties/{registration}");
        reviewed.GetProperty("review").GetProperty("matchedPartyId").GetGuid().ShouldBe(known);
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/party/parties/{registration}/match-resolution", new { resolution = "LINK" },
            ifMatch: $"\"{reviewed.GetProperty("rowVersion").GetInt64()}\""), HttpStatusCode.NoContent);

        return new(PartyApprovals.MatchBusinessRefType, registration,
            async () => (await w.Get($"/api/v1/party/parties/{registration}")).GetProperty("review").GetProperty("status").GetString()!);
    }

    /// <summary>Through the API: the party the registration matched is merged into another, so it will not take the link.</summary>
    private static async Task RefusePartyMatch(World w, Waiting waiting)
    {
        var known = (await w.Get($"/api/v1/party/parties/{waiting.BusinessRefId}")).GetProperty("review").GetProperty("matchedPartyId").GetGuid();
        var survivor = await w.Maker.RegisterPerson("Survivor");
        await w.Db.UserWithPermissions(w.Tenant, $"merger-{w.Suffix}", PartyPermissions.PartyMerge);
        await w.Ok(await w.Factory.ClientFor(w.Tenant, $"merger-{w.Suffix}").Send(HttpMethod.Post, "/api/v1/party/merges",
            new { survivorId = survivor, mergedId = known, reason = "Same person" }, idempotent: true));
    }

    private static async Task<Waiting> PrivilegedRole(World w)
    {
        var role = "NIGHT_OP";
        await w.Db.ExecuteAsOwner("""
            INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code)
            VALUES (@t, @role, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @role)
            """, ("t", w.Tenant), ("role", role));
        await w.Db.GrantFunctionsWithin(w.Tenant, role, Today.AddDays(-1), IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate);
        await w.Db.ExecuteAsOwner("""
            INSERT INTO iam.cfg_authentication_and_session_policies
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, policy_code, mfa_required_roles, session_timeout, lockout_attempts)
            VALUES (@t, 'MC_POLICY', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', 'MC_POLICY', @roles, interval '30 minutes', 5)
            """, ("t", w.Tenant), ("roles", new[] { role }));
        var target = await w.Db.UserWithPermissions(w.Tenant, $"night-{w.Suffix}");
        var user = await w.Maker.GetAsync($"/api/v1/identity/users/{target}");
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/identity/users/{target}/assign-role", new { roleCode = role }, ifMatch: user.ETag()),
            HttpStatusCode.NoContent);

        async Task<JsonElement> Assignment() =>
            (await w.Get($"/api/v1/identity/users/{target}")).GetProperty("roleAssignments").EnumerateArray().Single(a => a.GetProperty("roleCode").GetString() == role);

        return new(IdentityApprovals.BusinessRefType, (await Assignment()).GetProperty("id").GetGuid(), async () => (await Assignment()).GetProperty("status").GetString()!);
    }

    /// <summary>
    /// Revoking a role applies only to assignments in effect, so no API changes one waiting for approval: an owner update
    /// stands for an assignment revoked since it was submitted.
    /// </summary>
    private static Task RefusePrivilegedRole(World w, Waiting waiting) =>
        w.Db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET status = 'REVOKED', revoked_at = now(), revoked_by = assigned_by WHERE tenant_id = @t AND id = @a",
            ("t", w.Tenant), ("a", waiting.BusinessRefId));

    private static async Task<Waiting> SubjectLink(World w)
    {
        var party = await w.Maker.RegisterPerson("Linked");
        var user = await w.Db.UserWithPermissions(w.Tenant, $"portal-{w.Suffix}");
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/identity/users/{user}/subject-link", new { kind = "Party", partyId = party },
            ifMatch: (await w.Maker.GetAsync($"/api/v1/identity/users/{user}")).ETag()), HttpStatusCode.NoContent);
        var link = (await w.Get($"/api/v1/identity/users/{user}/subject-link")).GetProperty("id").GetGuid();
        return new(IdentityApprovals.SubjectLinkBusinessRefType, link,
            async () => (await w.Db.ScalarAsOwner<string>("SELECT status FROM iam.user_subject_link WHERE id = @l", ("l", link)))!);
    }

    /// <summary>Through the API: the administrator ends the link while it waits for approval.</summary>
    private static async Task RefuseSubjectLink(World w, Waiting waiting)
    {
        var user = await w.Db.ScalarAsOwner<Guid>("SELECT user_id FROM iam.user_subject_link WHERE id = @l", ("l", waiting.BusinessRefId));
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/identity/users/{user}/subject-link/end",
            ifMatch: (await w.Maker.GetAsync($"/api/v1/identity/users/{user}")).ETag()), HttpStatusCode.NoContent);
    }

    private static async Task<Waiting> TenantAdministrator(World w)
    {
        var code = "TA" + w.Suffix[..6].ToUpperInvariant();
        var registered = await w.Maker.Send(HttpMethod.Post, "/api/v1/tenancy/tenants",
            new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" }, idempotent: true);
        await w.Ok(registered, HttpStatusCode.Created);
        var tenantId = (await registered.Json()).GetProperty("id").GetGuid();
        var url = $"/api/v1/tenancy/tenants/{tenantId}";
        var etag = (await w.Maker.GetAsync(url)).ETag();
        await w.Db.SeedReadyTenant(tenantId, Today.AddDays(-30));
        foreach (var step in new[] { "PLATFORM_REFERENCE_DATA", "TENANT_REGISTRATION" })
        {
            var completed = await w.Maker.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step }, ifMatch: etag);
            await w.Ok(completed, HttpStatusCode.NoContent);
            etag = completed.ETag();
        }

        var request = await w.Created("/api/v1/identity/tenant-administrators", new
        {
            tenantId, username = $"admin-{w.Suffix}", displayName = "First administrator", idpCode = "OIDC", externalSubject = $"admin-{w.Suffix}", mfaEnrolled = true,
            functions = new[] { "ASSIGN_ROLE", "IDENTITY_USERS", "INTEGRATION_DEAD_LETTERS", "PROVISION_USER" },
        });
        return new(IdentityApprovals.TenantAdministratorBusinessRefType, request,
            async () => (await w.Get($"/api/v1/identity/tenant-administrators/{request}")).GetProperty("status").GetString()!);
    }

    /// <summary>
    /// No API adds a user to a tenant that has no administrator yet, so an owner insert stands for the new tenant having had
    /// a user since the request was made: its first administrator is then no longer provisioned.
    /// </summary>
    private static async Task RefuseTenantAdministrator(World w, Waiting waiting)
    {
        var tenant = await w.Db.ScalarAsOwner<Guid>("SELECT target_tenant_id FROM iam.tenant_administrator_request WHERE id = @r", ("r", waiting.BusinessRefId));
        await w.Db.UserWithPermissions(tenant, $"early-{w.Suffix}");
    }

    private static async Task<Waiting> StaticDataChange(World w, bool tier1)
    {
        var (url, code, body) = tier1
            ? ("/api/v1/identity/static/roles-and-permission-sets", "ROLE_AE", (object)new { code = "ROLE_AE", roleCode = "ROLE_AE", scopeProducts = new[] { "LIFE-TERM" },
                changeReason = "Test role" })
            : ("/api/v1/identity/static/access-review-schedules", "SCHED_AE",
                new { code = "SCHED_AE", scope = "PRIVILEGED", frequency = "P3M", reviewerRole = "AUDITOR", changeReason = "Test schedule" });
        var created = await w.Maker.Send(HttpMethod.Post, url, body, idempotent: true);
        await w.Ok(created, HttpStatusCode.Created);
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"{url}/{code}/versions/1/submit", ifMatch: created.ETag()));
        await w.Factory.DrainOutbox();
        var change = await w.Db.ScalarAsOwner<Guid>("SELECT id FROM sdm.change_request WHERE tenant_id = @t AND record_key = @k", ("t", w.Tenant), ("k", $"{code}@1"));

        async Task<string> State()
        {
            var version = (await w.Get($"{url}/{code}/versions"))[0].GetProperty("recordStatus").GetString();
            var status = await w.Db.ScalarAsOwner<string>("SELECT status FROM sdm.change_request WHERE id = @c", ("c", change));
            return $"version {version}|change {status}";
        }

        return new(StaticDataApprovals.BusinessRefType, change, State);
    }

    /// <summary>
    /// No API withdraws a submitted static data change, so an owner update stands for a change request rejected since it
    /// was submitted; the version stays submitted.
    /// </summary>
    private static Task RefuseStaticDataChange(World w, Waiting waiting) =>
        w.Db.ExecuteAsOwner("UPDATE sdm.change_request SET status = 'REJECTED' WHERE tenant_id = @t AND id = @c", ("t", w.Tenant), ("c", waiting.BusinessRefId));

    // ---- Money: Billing, Claims Framework, Payments, Finance, Distribution ---------------------------------------------

    private static readonly string[] MoneyPermissionSet =
    [
        PartyPermissions.PartyCreate, PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountVerify,
        BillingPermissions.AccountRead, BillingPermissions.AccountCreate, BillingPermissions.AccountUpdate, BillingPermissions.InvoiceRead,
        BillingPermissions.InvoiceCreate, BillingPermissions.RefundRead, BillingPermissions.RefundCreate, PaymentPermissions.PaymentRead, PaymentPermissions.PaymentResolve,
        PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.ReconciliationRead,
        PaymentPermissions.CallbackEndpointUpdate, FinancePermissions.JournalRead, FinancePermissions.PeriodRead, FinancePermissions.PeriodUpdate,
        FinancePermissions.PostingExceptionRead, ClaimsPermissions.ClaimRead, ClaimsPermissions.ClaimCreate, ClaimsPermissions.FinancialsRead,
        ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead, ClaimsPermissions.InstructionCreate, ClaimsPermissions.InstructionRelease,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private static async Task MoneyWorld(World w)
    {
        await w.Locale();
        await w.Money();
        await w.Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "GENERAL", ("domain", "GENERAL"), ("prefix", "GEN"), ("sequence_length", 8));
        await w.Finance(0);
    }

    /// <summary>A refund of 25 against the payer's first invoice, to the payer's verified account; its approval waits.</summary>
    private static async Task<Guid> RequestRefund(World w)
    {
        var (payer, bankAccount) = await w.Payee("Refundee", "62000555501");
        var account = (await (await w.Maker.Send(HttpMethod.Post, "/api/v1/billing/accounts/",
            new { payerPartyId = payer, currency = "ZAR", collectionMethod = "DEBIT_ORDER" }, idempotent: true)).Json()).GetProperty("id").GetGuid();
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/schedules", new
        {
            sourceDomain = "GENERAL", policyRef = Guid.CreateVersion7(), productVersionRef = "MOTOR-v1", frequency = "MONTHLY", startDate = Today,
            endDate = (DateOnly?)null, instalmentAmount = 80m,
        }, idempotent: true));
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/invoices", new { asOf = Today }, idempotent: true));
        await w.Factory.DrainOutbox();
        return await w.Created($"/api/v1/billing/accounts/{account}/refunds", new { amount = 25m, reasonCode = "OVERPAID", bankAccountId = bankAccount });
    }

    private static async Task<Waiting> Refund(World w)
    {
        await MoneyWorld(w);
        var refund = await RequestRefund(w);
        return new(BillingApprovals.RefundBusinessRefType, refund, async () => (await w.Get($"/api/v1/billing/refunds/{refund}")).GetProperty("status").GetString()!,
            () => w.Get($"/api/v1/billing/refunds/{refund}"));
    }

    private static async Task<Waiting> ClaimPayment(World w)
    {
        await MoneyWorld(w);
        var number = (await (await w.Maker.Send(HttpMethod.Post, "/api/v1/claims/claims",
            new { domain = "GENERAL", domainClaimId = Guid.CreateVersion7(), policyRef = Guid.CreateVersion7() })).Json()).GetProperty("claimNumber").GetString()!;
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/claims/claims/{number}/reserves",
            new { action = "Set", reserveType = "INDEMNITY", amount = 1000m, currency = "ZAR", reason = "Assessed" }));
        var (payee, bankAccount) = await w.Payee("Claimant", "62000777701");
        var instruction = await w.Created($"/api/v1/claims/claims/{number}/payment-instructions", new
        {
            instructionRef = "SETTLE-1", payeePartyId = payee, bankAccountId = bankAccount, amount = 400m, approvedAmount = 400m, currency = "ZAR", reserveType = "INDEMNITY",
        });
        await w.Ok(await w.Maker.PostAsync($"/api/v1/claims/payment-instructions/{instruction}/submit", null));
        var url = $"/api/v1/claims/payment-instructions/{instruction}";
        return new(ClaimsApprovals.InstructionBusinessRefType, instruction, async () => (await w.Get(url)).GetProperty("status").GetString()!, () => w.Get(url));
    }

    /// <summary>
    /// D-1: an approved refund's payment in a built batch. After an ending other than approval, the batch's payments leave it
    /// (payout_batch_id NULL) and a new batch takes them.
    /// </summary>
    private static async Task<Waiting> PayoutBatch(World w)
    {
        await MoneyWorld(w);
        var refund = await RequestRefund(w);
        await w.ApproveOnTheWay(BillingApprovals.RefundBusinessRefType, refund);
        var payment = await w.Db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r", ("r", refund));
        var batch = await w.Created("/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.Refund, currency = "ZAR" });
        (await w.Db.ScalarAsOwner<Guid?>("SELECT payout_batch_id FROM payment.payment WHERE id = @p", ("p", payment))).ShouldBe(batch);
        var url = $"/api/v1/payments/payout-batches/{batch}";

        async Task<IReadOnlyList<string>> After(string ending)
        {
            if (ending == Approved)
            {
                return [];
            }

            var problems = new List<string>();
            var inBatch = await w.Db.ScalarAsOwner<Guid?>("SELECT payout_batch_id FROM payment.payment WHERE id = @p", ("p", payment));
            if (inBatch is not null)
            {
                problems.Add($"D-1: the payment is still in batch {inBatch}");
            }

            var again = await w.Maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.Refund, currency = "ZAR" }, idempotent: true);
            if (again.StatusCode != HttpStatusCode.Created)
            {
                problems.Add($"D-1: a new batch could not be built: {again.StatusCode} {await again.Content.ReadAsStringAsync()}");
            }
            else if (await w.Db.ScalarAsOwner<Guid?>("SELECT payout_batch_id FROM payment.payment WHERE id = @p", ("p", payment)) != (await again.Json()).GetProperty("id").GetGuid())
            {
                problems.Add("D-1: the new batch does not contain the released payment");
            }

            return problems;
        }

        return new(PaymentApprovals.PayoutBusinessRefType, batch, async () => (await w.Get(url)).GetProperty("status").GetString()!, () => w.Get(url), After);
    }

    private static async Task<Waiting> PeriodReopen(World w)
    {
        await w.Finance(0);
        await w.Ok(await w.Maker.PostAsync($"/api/v1/finance/periods/{Month}/soft-close", null));
        await w.Ok(await w.Maker.PostAsync($"/api/v1/finance/periods/{Month}/reopen", null));

        async Task<JsonElement> Period() =>
            (await w.Get("/api/v1/finance/periods")).EnumerateArray().Single(p => p.GetProperty("periodCode").GetString() == Month);

        async Task<string> State()
        {
            var period = await Period();
            return period.GetProperty("status").GetString() + (period.GetProperty("reopenRequestedBy").ValueKind == JsonValueKind.Null ? "" : "|reopen requested");
        }

        return new(FinanceApprovals.PeriodBusinessRefType, (await Period()).GetProperty("id").GetGuid(), State, Period);
    }

    private static readonly string[] CommissionPermissionSet =
    [
        PartyPermissions.PartyCreate, PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
        DistributionPermissions.IntermediaryRead, DistributionPermissions.CommissionRead, DistributionPermissions.CommissionUpdate, DistributionPermissions.StatementRead,
        DistributionPermissions.StatementCreate, PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, WorkflowPermissions.ApprovalRead,
        WorkflowPermissions.ApprovalApprove, FinancePermissions.PeriodUpdate,
    ];

    /// <summary>
    /// A broker with a verified account and a payable entitlement (seeded as Distribution leaves one once the premium is
    /// paid), and its statement prepared. After an ending other than approval, the entitlement is on the next statement.
    /// </summary>
    private static async Task<Waiting> CommissionStatement(World w)
    {
        await w.Locale();
        await w.Money();
        var (party, _) = await w.Payee("Broker", "8800112233");
        await w.Seed("commission.cfg_commission_plans", "EFFECTIVE", "PLAN-10", ("plan_code", "PLAN-10"), ("basis", "PREMIUM"), ("rate", 10m), ("clawback_rule", "UNEARNED"));
        await w.Seed("commission.mst_intermediary_register", "ACTIVE", "BRK-1", ("intermediary_code", "BRK-1"), ("type", "BROKER"), ("legal_name", "BRK-1"),
            ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", "ACTIVE"), ("hierarchy_node", "NODE-1"), ("party_id", party));
        var entitlement = Guid.CreateVersion7();
        await w.Db.ExecuteAsOwner("""
            INSERT INTO commission.commission_entitlement (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, intermediary_code, source_type,
                                                           source_id, policy_ref, plan_code, plan_version, basis_amount, rate, amount, currency, status, product_code,
                                                           source_domain, due_date)
            VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', 'BRK-1', 'INVOICE', gen_random_uuid(), gen_random_uuid(), 'PLAN-10', 1, 254.17, 10, 25.42, 'ZAR',
                    'PAYABLE', 'AE-GMOT', 'GENERAL', @d)
            """, ("t", w.Tenant), ("id", entitlement), ("d", Today));
        var statement = await w.Created("/api/v1/distribution/statements", new { intermediaryCode = "BRK-1", periodCode = Month, currency = "ZAR" });
        var url = $"/api/v1/distribution/statements/{statement}";

        async Task<string> State()
        {
            var view = await w.Get(url);
            return view.GetProperty("status").GetString() + (view.GetProperty("approvalPending").GetBoolean() ? "|approval pending" : "");
        }

        async Task<IReadOnlyList<string>> After(string ending)
        {
            if (ending == Approved)
            {
                return [];
            }

            var again = await w.Maker.Send(HttpMethod.Post, "/api/v1/distribution/statements", new { intermediaryCode = "BRK-1", periodCode = Month, currency = "ZAR" },
                idempotent: true);
            return again.IsSuccessStatusCode && (await again.Json()).GetProperty("netAmount").GetDecimal() == 25.42m
                ? []
                : [$"the entitlement was not released for the next statement: {again.StatusCode} {await again.Content.ReadAsStringAsync()}"];
        }

        return new(DistributionApprovals.StatementBusinessRefType, statement, State, () => w.Get(url), After);
    }

    // ---- Product Factory ---------------------------------------------------------------------------------------------

    private static readonly string[] ProductPermissionSet =
    [
        ProductPermissions.ProductRead, ProductPermissions.ProductCreate, ProductPermissions.ProductUpdate, ProductPermissions.ProductPublish,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead,
        StaticDataPermissions.Read,
    ];

    /// <summary>A version validated, simulated and submitted. After an ending other than approval it is a draft whose simulations are stale.</summary>
    private static async Task<Waiting> ProductPublication(World w)
    {
        const string product = "AE-MOTOR";
        var versionUrl = $"/api/v1/product-factory/products/{product}/versions/1";
        async Task Static(string table, string code, string columns, string values, bool reference = false) =>
            await w.Db.ExecuteAsOwner($"""
                INSERT INTO {table} (tenant_id, code, version, effective_from, record_status, created_by, change_reason, {columns})
                VALUES (@t, @code, 1, current_date - 1, '{(reference ? "ACTIVE" : "EFFECTIVE")}', 'seed', 'test seed', {values})
                """, ("t", w.Tenant), ("code", code));

        await Static("product.ref_product_taxonomy", "GEN-MOTOR", "line, family_code, family_name", "'GENERAL', 'GEN-MOTOR', 'Motor insurance'", reference: true);
        await Static("product.cfg_coverage_and_benefit_library", "OWN_DAMAGE", "type, name, basis, measure, domain", "'COVERAGE', 'Own damage', 'INDEMNITY', 'SUM_INSURED', 'GENERAL'");
        await Static("product.cfg_coverage_and_benefit_library", "THEFT", "type, name, basis, measure, domain", "'COVERAGE', 'Theft', 'INDEMNITY', 'SUM_INSURED', 'GENERAL'");
        await Static("rating.cfg_rating_factor_definitions", "VEHICLE_GROUP", "factor_code, data_type, source_attribute", "'VEHICLE_GROUP', 'CODE', 'vehicle_group'");
        await Static("rating.cfg_rating_tables", "MOT-BASE", "table_code, dimensions, values, interpolation",
            """'MOT-BASE', ARRAY['VEHICLE_GROUP'], '[{"key":["G3"],"value":0.03}]', 'NONE'""");
        await Static("rating.cfg_premium_parameters", "AE-BASE", "parameter_code, product_version, value, unit", $"'MOT-BASE', '{product} v1', 1, 'FACTOR'");
        await Static("rating.cfg_premium_parameters", "AE-FEE", "parameter_code, product_version, value, unit", $"'POLICY_FEE', '{product} v1', 50, 'AMOUNT'");
        await Static("rating.cfg_rounding_and_currency_precision_rules", "ZAR-2DP", "rule_code, currency, step, method, stage", "'ZAR-2DP', 'ZAR', 0.01, 'HALF_UP', 'FINAL_PREMIUM'");
        await Static("rating.cfg_rating_test_case_library", "AE-TC-1", "case_id, product_version, inputs, expected_premium, tolerance",
            $$"""'AE-TC-1', '{{product}} v1', '{"currency":"ZAR","frequency":"MONTHLY","sum_insured":100000,"vehicle_group":"G3"}', 3050, 0.01""");

        await w.Created("/api/v1/product-factory/products", new
        {
            productCode = product, domain = "General", familyCode = "GEN-MOTOR", name = "Motor comprehensive", coverages = new[] { "OWN_DAMAGE", "THEFT" }, effectiveFrom = Today,
        });
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"{versionUrl}/validate", ifMatch: (await w.Maker.GetAsync(versionUrl)).ETag()), HttpStatusCode.NoContent);
        (await (await w.Maker.Send(HttpMethod.Post, $"{versionUrl}/simulations", idempotent: true)).Json()).GetProperty("status").GetString().ShouldBe("Passed");
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"{versionUrl}/submit", new { reason = "Launch" }, ifMatch: (await w.Maker.GetAsync(versionUrl)).ETag()),
            HttpStatusCode.NoContent);
        var version = await w.Db.ScalarAsOwner<Guid>("SELECT id FROM product.cfg_product_definitions_and_versions WHERE tenant_id = @t AND code = @c AND version = 1",
            ("t", w.Tenant), ("c", product));

        async Task<IReadOnlyList<string>> After(string ending)
        {
            if (ending == Approved)
            {
                return [];
            }

            // Simulations stale: validated again, it cannot be submitted until simulated again.
            var validated = await w.Maker.Send(HttpMethod.Post, $"{versionUrl}/validate", ifMatch: (await w.Maker.GetAsync(versionUrl)).ETag());
            if (validated.StatusCode != HttpStatusCode.NoContent)
            {
                return [$"the draft could not be validated again: {validated.StatusCode} {await validated.Content.ReadAsStringAsync()}"];
            }

            var submitted = await w.Maker.Send(HttpMethod.Post, $"{versionUrl}/submit", new { reason = "Again" }, ifMatch: (await w.Maker.GetAsync(versionUrl)).ETag());
            var code = submitted.IsSuccessStatusCode ? "accepted" : await submitted.Code();
            return code == Insys.ProductFactory.Domain.Products.ProductErrorCodes.SimulationRequired ? [] : [$"simulations are not stale: submitting again was {code}"];
        }

        return new(ProductApprovals.BusinessRefType, version, async () => (await w.Get(versionUrl)).GetProperty("status").GetString()!, After: After);
    }

    /// <summary>No API changes a submitted version, so an owner update stands for one returned to draft since it was submitted.</summary>
    private static Task RefuseProductPublication(World w, Waiting waiting) =>
        w.Db.ExecuteAsOwner("UPDATE product.cfg_product_definitions_and_versions SET record_status = 'DRAFT' WHERE tenant_id = @t AND id = @v",
            ("t", w.Tenant), ("v", waiting.BusinessRefId));

    // ---- General ---------------------------------------------------------------------------------------------------------

    private const string GeneralProduct = "AE-GMOT";

    private static readonly string[] GeneralPermissionSet =
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
        DocumentPermissions.DocumentRead, DocumentPermissions.DocumentCreate, PartyPermissions.PartyCreate, PartyPermissions.PartyUpdate,
        PartyPermissions.PartyRead, PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
    ];

    /// <summary>The General world of GeneralProcessTests: a published motor product rated at sum insured × 0.03 + 50, and a policy issued on it.</summary>
    private static async Task<(Guid Policy, Guid Vehicle)> GeneralPolicy(World w)
    {
        await w.Db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'GENERAL', 'Motor', @coverages, 'GEN-MOTOR', 'MOTOR', 'ISSUE')
            """, ("t", w.Tenant), ("p", GeneralProduct), ("from", From), ("coverages", new[] { "OWN_DAMAGE", "THEFT" }));
        await w.Seed("product.cfg_limit_and_deductible_schedules", "EFFECTIVE", "MOT-LIM", ("schedule_code", "MOT-LIM"), ("coverage", "OWN_DAMAGE"), ("limit_type", "LIMIT"),
            ("amount", 500_000m), ("rule", "FIXED"), ("currency", "ZAR"));
        await w.Seed("product.cfg_limit_and_deductible_schedules", "EFFECTIVE", "MOT-EXC", ("schedule_code", "MOT-EXC"), ("coverage", "OWN_DAMAGE"), ("limit_type", "EXCESS"),
            ("amount", 5_000m), ("percentage", 5m), ("rule", "GREATER_OF"), ("currency", "ZAR"));
        foreach (var (type, fields) in new[]
                 {
                     ("POLICY_SCHEDULE", new[] { "policy_number", "annual_premium" }), ("CERTIFICATE", new[] { "policy_number", "period_start" }),
                     ("ENDORSEMENT", new[] { "endorsement_number", "effective_date" }),
                 })
        {
            await w.Seed("document.ref_document_categories", "ACTIVE", type, ("category_code", type), ("label", type), ("classification", "PERSONAL"), ("domain", "ALL"),
                ("allowed_roles", Array.Empty<string>()));
            await w.Seed("document.cfg_document_templates", "EFFECTIVE", $"TPL-{type}", ("template_code", $"TPL-{type}"), ("language", "en"), ("document_type", type),
                ("body_ref", $"doc://templates/{type}/v1"), ("merge_fields", fields));
            await w.Seed("product.cfg_product_template_assignments", "EFFECTIVE", $"TA-{type}", ("product_code", GeneralProduct), ("document_type", type),
                ("template", $"TPL-{type}"), ("language", "en"));
        }

        await w.Seed("rating.cfg_rating_factor_definitions", "EFFECTIVE", "VEHICLE_GROUP", ("factor_code", "VEHICLE_GROUP"), ("data_type", "CODE"),
            ("source_attribute", "vehicle_group"));
        await w.Db.ExecuteAsOwner("""
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'GMOT-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'GMOT-BASE', ARRAY['VEHICLE_GROUP'], '[{"key":["G3"],"value":0.03}]', 'NONE')
            """, ("t", w.Tenant), ("from", From));
        await w.Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GMOT-P1", ("parameter_code", "GMOT-BASE"), ("product_version", $"{GeneralProduct} v1"), ("value", 1m),
            ("unit", "FACTOR"));
        await w.Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GMOT-P2", ("parameter_code", "POLICY_FEE"), ("product_version", $"{GeneralProduct} v1"), ("value", 50m),
            ("unit", "AMOUNT"));
        await w.Seed("rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", ("rule_code", "ZAR-2DP"), ("currency", "ZAR"), ("step", 0.01m),
            ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));

        await w.Db.ExecuteAsOwner("""
            INSERT INTO general.cfg_risk_object_types_and_attribute_schemas (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                                             object_type, attributes, validation)
            VALUES (@t, 'VEHICLE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'VEHICLE', '["registration:String(10):Y","vehicle_group:String(5):Y","year:Integer:Y"]',
                    '["year <= current_year"]')
            """, ("t", w.Tenant), ("from", From));
        await w.Seed("general.cfg_quote_validity_periods", "EFFECTIVE", "QV-GMOT", ("product_code", GeneralProduct), ("days_valid", 30), ("re_rate_on_expiry", false));
        await w.Seed("general.cfg_policy_numbering_schemes", "EFFECTIVE", "GP", ("product_line", "GENERAL"), ("prefix", "GP"), ("sequence_length", 9));
        await w.Seed("general.cfg_cancellation_reasons_and_refund_rules", "EFFECTIVE", "INS_REQUEST", ("reason_code", "INS_REQUEST"), ("initiator", "INSURED"),
            ("refund_basis", "PRO_RATA"), ("fee", 0m));
        await w.Seed("general.cfg_endorsement_types", "EFFECTIVE", "CHANGE_SI", ("premium_method", "PRO_RATA"), ("documents", new[] { "ENDORSEMENT" }),
            ("requires_underwriting", false));
        await w.Seed("general.ref_loss_cause_codes", "ACTIVE", "COLLISION", ("cause_code", "COLLISION"), ("peril", "ACC"), ("label", "Collision"));
        await w.Seed("general.ref_peril_codes", "ACTIVE", "ACC", ("peril_code", "ACC"), ("label", "Accident"), ("line", "MOTOR"), ("catastrophe", false));
        await w.Seed("general.mst_repairer_and_assessor_panels", "ACTIVE", "ASSESSOR-1", ("party", Assessor.ToString()), ("panel_type", "ASSESSOR"), ("location", "CPT"),
            ("approved_lines", new[] { "MOTOR" }), ("status", "ACTIVE"));
        await w.Seed("general.mst_repairer_and_assessor_panels", "ACTIVE", "REPAIRER-1", ("party", Repairer.ToString()), ("panel_type", "REPAIRER"), ("location", "CPT"),
            ("approved_lines", new[] { "MOTOR" }), ("status", "ACTIVE"));
        await w.Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "GENERAL", ("domain", "GENERAL"), ("prefix", "GEN"), ("sequence_length", 8));
        await w.Locale();
        await w.Money();
        await w.Finance(0);

        var vehicle = await w.Created("/api/v1/general/risk-objects",
            new { objectType = "VEHICLE", attributes = new { registration = "CA123456", vehicle_group = "G3", year = Today.Year - 2 } });
        var quote = await w.Created("/api/v1/general/quotes", new
        {
            productCode = GeneralProduct, proposerPartyId = Guid.CreateVersion7(), currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1),
            risks = new[]
            {
                new
                {
                    riskObjectId = vehicle, sumInsured = 100_000m,
                    coverages = new object[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m, excessScheduleCode = "MOT-EXC" }, new { coverageCode = "THEFT", sumInsured = 50_000m } },
                },
            },
        });
        await GeneralRate(w, GeneralRatingSubjects.Quote, quote, 100_000m);
        await w.Ok(await w.Maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null));
        await w.Ok(await w.Maker.PostAsync($"/api/v1/general/quotes/{quote}/accept", null));
        var proposal = await w.Created("/api/v1/general/proposals", new { quoteId = quote, declarations = new { claims_last_3_years = 0 } });
        await w.Ok(await w.Maker.PostAsync($"/api/v1/general/proposals/{proposal}/accept", null));
        var policy = await w.Created("/api/v1/general/policies", new { quoteId = quote, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER" });
        await w.Factory.DrainOutbox();
        return (policy, vehicle);
    }

    private static readonly Guid Assessor = Guid.CreateVersion7();
    private static readonly Guid Repairer = Guid.CreateVersion7();

    private static async Task GeneralRate(World w, string subjectType, Guid subjectId, decimal sumInsured)
    {
        var calculated = await w.Maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"AE-{subjectId:N}"[..20], sourceDomain = GeneralRatingSubjects.SourceDomain, subjectType, subjectId, productCode = GeneralProduct, productVersion = 1,
            currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = sumInsured, vehicle_group = "G3" },
        }, idempotent: true);
        await w.Ok(calculated, HttpStatusCode.Created);
        await w.Factory.DrainOutbox();
    }

    private static async Task<Waiting> GeneralEndorsement(World w)
    {
        var (policy, vehicle) = await GeneralPolicy(w);
        var endorsement = await w.Created($"/api/v1/general/policies/{policy}/endorsements", new
        {
            endorsementType = "CHANGE_SI", effectiveDate = Today,
            changes = new[] { new { action = "ChangeRisk", riskObjectId = vehicle, sumInsured = 150_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 150_000m } } } },
        });
        await GeneralRate(w, GeneralRatingSubjects.Endorsement, endorsement, 150_000m);
        await w.Ok(await w.Maker.PostAsync($"/api/v1/general/endorsements/{endorsement}/submit", null));
        return new(GeneralApprovals.EndorsementBusinessRefType, endorsement,
            async () => (await w.Get($"/api/v1/general/endorsements/{endorsement}")).GetProperty("status").GetString()!);
    }

    /// <summary>Through the API: the policy is cancelled while the endorsement waits, so it no longer takes endorsements.</summary>
    private static async Task RefuseGeneralEndorsement(World w, Waiting waiting)
    {
        var policy = await w.Db.ScalarAsOwner<Guid>("SELECT policy_id FROM general.endorsement WHERE id = @e", ("e", waiting.BusinessRefId));
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/general/policies/{policy}/cancel", new { reasonCode = "INS_REQUEST", effectiveDate = Today }));
    }

    private static async Task<Waiting> GeneralSettlement(World w)
    {
        var (policy, vehicle) = await GeneralPolicy(w);
        var claim = await w.Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC", description = "Rear-ended",
        });
        var number = (await (await w.Maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json()).GetProperty("claimNumber").GetString()!;
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/claims/claims/{number}/reserves",
            new { action = "Set", reserveType = "INDEMNITY", amount = 50_000m, currency = "ZAR", reason = "Assessed" }));
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Assessor }));
        await w.Factory.DrainOutbox();
        var assessment = (await w.Get($"/api/v1/general/claims/{claim}/assessments")).EnumerateArray().Single().GetProperty("id").GetGuid();
        await w.Ok(await w.Maker.PostAsync($"/api/v1/general/assessments/{assessment}/start", null));
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/general/assessments/{assessment}/estimates", new { repairerPartyId = Repairer, amount = 41_000m }));
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/general/assessments/{assessment}/report", new { assessedAmount = 40_000m }));
        await w.Ok(await w.Maker.PostAsync($"/api/v1/general/assessments/{assessment}/accept", null));
        var (payee, account) = await w.Payee("Repairs", "62000123450");
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/general/claims/{claim}/settlement", new
        {
            payeePartyId = payee, bankAccountId = account, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Repair estimate within the assessed amount",
        }));

        async Task<string> State()
        {
            var view = await w.Get($"/api/v1/general/claims/{claim}");
            var settlement = view.GetProperty("settlement");
            return view.GetProperty("status").GetString() + (view.GetProperty("status").GetString() == "Approved" ? ""
                : settlement.ValueKind == JsonValueKind.Null ? "|no settlement" : settlement.GetProperty("pending").GetBoolean() ? "|settlement pending" : "|settlement not pending");
        }

        return new(GeneralApprovals.SettlementBusinessRefType, claim, State);
    }

    /// <summary>
    /// No API changes a settlement waiting for approval (nor declines the claim meanwhile), so an owner update stands for
    /// one changed since it was submitted: its amount, which the approval's subject binds.
    /// </summary>
    private static Task RefuseGeneralSettlement(World w, Waiting waiting) =>
        w.Db.ExecuteAsOwner("UPDATE general.claim SET settlement_amount = settlement_amount - 1 WHERE tenant_id = @t AND id = @c", ("t", w.Tenant), ("c", waiting.BusinessRefId));

    // ---- Life --------------------------------------------------------------------------------------------------------------

    private const string LifeProduct = "AE-LTERM";

    private static readonly string[] LifePermissionSet =
    [
        LifePermissions.ProposalRead, LifePermissions.ProposalCreate, LifePermissions.ProposalUpdate, LifePermissions.UnderwritingRead, LifePermissions.UnderwritingUpdate,
        LifePermissions.UnderwritingWaive, LifePermissions.UnderwritingDecide, LifePermissions.PolicyRead, LifePermissions.PolicyCreate, LifePermissions.PolicyUpdate,
        LifePermissions.ClaimRead, LifePermissions.ClaimCreate, LifePermissions.ClaimUpdate, LifePermissions.ClaimDecide, RatingPermissions.CalculationCreate,
        RatingPermissions.CalculationRead, RulesPermissions.DecisionCreate, RulesPermissions.DecisionRead, BillingPermissions.AccountRead, BillingPermissions.InvoiceRead,
        PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackEndpointUpdate,
        ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead, ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead,
        ClaimsPermissions.InstructionRelease, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate, WorkflowPermissions.ApprovalRead,
        WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, DocumentPermissions.DocumentRead, PartyPermissions.PartyCreate,
        PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
    ];

    /// <summary>The Life world of LifeProcessTests: a published term product, its rating, rules and Life configuration.</summary>
    private static async Task LifeWorld(World w)
    {
        // The maker (underwriter) decides standard terms alone up to 2 000 000 sum assured when the rules agree.
        await w.Seed("rules.cfg_authority_matrices", "EFFECTIVE", "AM-UW", ("role_code", w.MakerRole), ("product_code", LifeProduct), ("max_sum_assured", 2_000_000m),
            ("decision_types", new[] { "STD" }));
        await w.Seed("rules.cfg_acceptance_and_referral_rules", "EFFECTIVE", "UW-SA-LIMIT", ("rule_code", "UW-SA-LIMIT"), ("condition", "sum_assured > threshold"),
            ("threshold", 5_000_000m), ("outcome", "REFER"));
        // Life claims are approved within the claims authority limits by claim type; only the checker's role has one.
        await w.Seed("life.cfg_claims_authority_limits", "EFFECTIVE", "CAL-DEATH", ("role", w.ApproverRole), ("claim_type", "DEATH"), ("limit_value", 5_000_000m));
        await w.Db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on,
                                                                     eligibility_ruleset_code)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'LIFE', 'Term life', @coverages, 'LIFE-RISK', 'TERM', 'ISSUE', 'LTERM-ELIG')
            """, ("t", w.Tenant), ("p", LifeProduct), ("from", From), ("coverages", new[] { "DEATH", "DISABILITY" }));
        await w.Seed("document.ref_document_categories", "ACTIVE", "POLICY_SCHEDULE", ("category_code", "POLICY_SCHEDULE"), ("label", "Policy schedule"),
            ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await w.Seed("document.cfg_document_templates", "EFFECTIVE", "TPL-LSCH", ("template_code", "TPL-LSCH"), ("language", "en"), ("document_type", "POLICY_SCHEDULE"),
            ("body_ref", "doc://templates/life-schedule/v1"), ("merge_fields", new[] { "policy_number", "sum_assured" }));
        await w.Seed("product.cfg_product_template_assignments", "EFFECTIVE", "TA-LSCH", ("product_code", LifeProduct), ("document_type", "POLICY_SCHEDULE"),
            ("template", "TPL-LSCH"), ("language", "en"));
        await w.Seed("rating.cfg_rating_factor_definitions", "EFFECTIVE", "SMOKER", ("factor_code", "SMOKER"), ("data_type", "CODE"), ("source_attribute", "smoker"));
        await w.Db.ExecuteAsOwner("""
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'LTERM-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'LTERM-BASE', ARRAY['SMOKER'], '[{"key":["NS"],"value":0.00045}]', 'NONE')
            """, ("t", w.Tenant), ("from", From));
        await w.Seed("rating.cfg_premium_parameters", "EFFECTIVE", "LTERM-P1", ("parameter_code", "LTERM-BASE"), ("product_version", $"{LifeProduct} v1"), ("value", 1m),
            ("unit", "FACTOR"));
        await w.Seed("rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", ("rule_code", "ZAR-2DP"), ("currency", "ZAR"), ("step", 0.01m),
            ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));
        await w.Seed("rules.cfg_eligibility_rule_sets", "EFFECTIVE", "LTERM-ADULT", ("ruleset_code", "LTERM-ELIG"), ("conditions", "age_next >= 18 AND age_next <= 65"),
            ("outcome", "ELIGIBLE"));
        await w.Seed("life.cfg_policy_numbering_schemes", "EFFECTIVE", "LIFE", ("product_line", "LIFE"), ("prefix", "LP"), ("sequence_length", 9));
        await w.Seed("life.cfg_nomination_rules", "EFFECTIVE", "NOM-LTERM", ("product_code", LifeProduct), ("max_beneficiaries", 5), ("min_share", 10m),
            ("allowed_relationships", new[] { "SPOUSE", "CHILD" }));
        foreach (var relationship in new[] { "SPOUSE", "CHILD" })
        {
            await w.Seed("life.ref_beneficiary_relationship_types", "ACTIVE", relationship, ("label", relationship));
        }

        await w.Seed("life.cfg_grace_period_rules", "EFFECTIVE", "GRACE-M", ("frequency", "MONTHLY"), ("grace_days", 30), ("cover_during_grace", true));
        await w.Seed("life.cfg_lapse_and_reinstatement_rules", "EFFECTIVE", "LAPSE-LTERM", ("product_code", LifeProduct), ("missed_payments_to_lapse", 2),
            ("reinstatement_window", TimeSpan.FromDays(180)), ("evidence_required", "DECLARATION"));
        await w.Seed("life.ref_underwriting_decision_codes", "ACTIVE", "STD", ("label", "Standard terms"), ("requires_terms", false));
        await w.Seed("life.ref_underwriting_decision_codes", "ACTIVE", "EXCL", ("label", "Accepted with an exclusion"), ("requires_terms", true));
        await w.Seed("life.ref_life_claim_types", "ACTIVE", "DEATH", ("label", "Death"), ("benefit_type", "DEATH"));
        await w.Db.SeedGlobalStatic("life.ref_cause_of_event_codes", "ACTIVE", "I21", Today.AddYears(-1), ("code_system", "ICD-10"), ("accidental", false));
        await w.Db.SeedGlobalStatic("life.ref_life_benefit_types", "ACTIVE", "DEATH", Today.AddDays(-30), ("label", "Death"), ("payout_type", "LUMP_SUM"));
        await w.Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "LIFE", ("domain", "LIFE"), ("prefix", "LIF"), ("sequence_length", 8));
        await w.Locale();
        await w.Money();
        await w.Finance(1, 0);
    }

    private static int AgeNext(DateOnly on)
    {
        var birth = new DateOnly(1985, 6, 15);
        return on.Year - birth.Year - (birth.AddYears(on.Year - birth.Year) > on ? 1 : 0) + 1;
    }

    /// <summary>A proposal quoted, found eligible and submitted; returns it and its underwriting case, in assessment.</summary>
    private static async Task<(Guid Proposal, Guid Case)> LifeCase(World w, Guid applicant, Guid spouse, DateOnly inception)
    {
        var proposal = await w.Created("/api/v1/life/proposals", new
        {
            productCode = LifeProduct, applicantPartyId = applicant, channelCode = "BRANCH", currency = "ZAR", inceptionDate = inception, frequency = "MONTHLY",
            collectionMethod = "DEBIT_ORDER",
            lives = new[] { new { partyId = applicant, lifeRole = "MAIN", smokerStatus = "NS" } },
            covers = new[]
            {
                new { insuredPartyId = applicant, benefitCode = "DEATH", sumAssured = 1_000_000m, termYears = 20 },
                new { insuredPartyId = applicant, benefitCode = "DISABILITY", sumAssured = 500_000m, termYears = 20 },
            },
            disclosures = new[] { new { insuredPartyId = applicant, questionCode = "SMOKE_12M", questionVersion = 1, answer = JsonSerializer.SerializeToElement(new { value = false }) } },
            nominations = new[] { new { partyId = spouse, relationshipCode = "SPOUSE", sharePct = 100m } },
        });
        await w.Ok(await w.Maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"L-{proposal:N}"[..20], sourceDomain = LifeSubjects.SourceDomain, subjectType = LifeSubjects.Proposal, subjectId = proposal, productCode = LifeProduct,
            productVersion = 1, currency = "ZAR", frequency = "MONTHLY", values = new { sum_insured = 1_500_000m, smoker = "NS" },
        }, idempotent: true), HttpStatusCode.Created);
        await w.Factory.DrainOutbox();
        await w.Ok(await w.Maker.Send(HttpMethod.Post, "/api/v1/rules/decisions",
            new { kind = "Eligibility", code = "LTERM-ELIG", subjectType = LifeSubjects.Proposal, subjectId = proposal, inputs = new { age_next = AgeNext(inception) } },
            idempotent: true), HttpStatusCode.Created);
        await w.Factory.DrainOutbox();
        await w.Ok(await w.Maker.PostAsync($"/api/v1/life/proposals/{proposal}/submit", null));
        await w.Factory.DrainOutbox();
        var caseId = (await w.Get($"/api/v1/life/proposals/{proposal}/underwriting-case")).GetProperty("id").GetGuid();
        await w.Ok(await w.Maker.PostAsync($"/api/v1/life/underwriting-cases/{caseId}/start-assessment", null));
        return (proposal, caseId);
    }

    /// <summary>A decision against the rules (no rule outcome, an exclusion), so it is proposed for a second person (guide §4).</summary>
    private static async Task<Waiting> LifeUnderwriting(World w)
    {
        await LifeWorld(w);
        var (_, caseId) = await LifeCase(w, await w.Maker.RegisterPerson("Applicant"), await w.Maker.RegisterPerson("Spouse"), Today);
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/life/underwriting-cases/{caseId}/decision", new
        {
            outcome = "Accept", decisionCode = "EXCL", reason = "AVIATION",
            items = new[] { new { benefitCode = "DEATH", decisionCode = "EXCL", exclusionCode = "EXC-AVIATION" } },
        }));
        var url = $"/api/v1/life/underwriting-cases/{caseId}";

        async Task<string> State()
        {
            var view = await w.Get(url);
            return view.GetProperty("status").GetString() + (view.GetProperty("decisionPending").GetBoolean() ? "|decision pending" : "");
        }

        return new(LifeApprovals.UnderwritingBusinessRefType, caseId, State, () => w.Get(url));
    }

    /// <summary>
    /// No API changes a decision waiting for approval, so an owner update stands for one changed since it was submitted:
    /// its reason, which the approval's subject binds.
    /// </summary>
    private static Task RefuseLifeUnderwriting(World w, Waiting waiting) =>
        w.Db.ExecuteAsOwner("UPDATE life.underwriting_case SET decision_reason = 'CHANGED' WHERE tenant_id = @t AND id = @c", ("t", w.Tenant), ("c", waiting.BusinessRefId));

    /// <summary>A death claim on a policy in force, assessed at its 1 000 000 sum assured; its approval is above the maker's claims authority.</summary>
    private static async Task<Waiting> LifeClaim(World w)
    {
        await LifeWorld(w);
        var applicant = await w.Maker.RegisterPerson("Insured");
        var spouse = await w.Maker.RegisterPerson("Widow");
        var inception = Today.AddMonths(-1);
        var (proposal, caseId) = await LifeCase(w, applicant, spouse, inception);
        await w.Ok(await w.Maker.Send(HttpMethod.Post, "/api/v1/rules/decisions",
            new { kind = "Underwriting", code = LifeProduct, subjectType = LifeSubjects.UnderwritingCase, subjectId = caseId, inputs = new { sum_assured = 1_500_000m } },
            idempotent: true), HttpStatusCode.Created);
        await w.Factory.DrainOutbox();
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/life/underwriting-cases/{caseId}/decision", new { outcome = "Accept", decisionCode = "STD", reason = "STANDARD" }));
        await w.Factory.DrainOutbox();
        var policy = await w.Created("/api/v1/life/policies", new { proposalId = proposal });
        await w.Factory.DrainOutbox();
        var view = await w.Get($"/api/v1/life/policies/{policy}");
        var life = view.GetProperty("lives")[0].GetProperty("id").GetGuid();
        var death = view.GetProperty("benefits").EnumerateArray().Single(b => b.GetProperty("benefitCode").GetString() == "DEATH").GetProperty("id").GetGuid();

        var claim = await w.Created("/api/v1/life/claims", new { policyId = policy, claimType = "DEATH", eventDate = Today, causeCode = "I21", claimantPartyId = spouse, insuredLifeId = life });
        await w.Ok(await w.Maker.PostAsync($"/api/v1/life/claims/{claim}/register", null));
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/life/claims/{claim}/assessment", new { lines = new[] { new { policyBenefitId = death, amount = 1_000_000m } } }));
        var account = await w.Maker.AddVerifiedBankAccount(w.Checker, w.Factory, spouse, "4400112233");
        await w.Ok(await w.Maker.Send(HttpMethod.Post, $"/api/v1/life/claims/{claim}/approval", new
        {
            payees = new[] { new { partyId = spouse, bankAccountId = account, sharePct = 100m } }, reserveType = "INDEMNITY", decisionReason = "DEATH_PROVEN",
            rationale = "Death certificate verified; cause not excluded.",
        }));
        var url = $"/api/v1/life/claims/{claim}";

        async Task<string> State()
        {
            var claimView = await w.Get(url);
            return claimView.GetProperty("status").GetString() + (claimView.GetProperty("approvalPending").GetBoolean() ? "|approval pending" : "")
                + $"|payees {claimView.GetProperty("payees").GetArrayLength()}";
        }

        return new(LifeApprovals.ClaimBusinessRefType, claim, State, () => w.Get(url));
    }

    /// <summary>
    /// No API changes a claim's approval request while it waits, so an owner update stands for one changed since it was
    /// submitted: its reason, which the approval's subject binds.
    /// </summary>
    private static Task RefuseLifeClaim(World w, Waiting waiting) =>
        w.Db.ExecuteAsOwner("UPDATE life.claim SET decision_reason = 'CHANGED' WHERE tenant_id = @t AND id = @c", ("t", w.Tenant), ("c", waiting.BusinessRefId));

    // ---- Health ------------------------------------------------------------------------------------------------------------

    private static readonly string[] HealthPermissionSet =
    [
        HealthPermissions.SchemeRead, HealthPermissions.SchemeCreate, HealthPermissions.SchemeUpdate, HealthPermissions.MembershipRead, HealthPermissions.MembershipCreate,
        HealthPermissions.MembershipUpdate, HealthPermissions.EligibilityCheck, HealthPermissions.PreauthRead, HealthPermissions.PreauthCreate,
        HealthPermissions.PreauthReview, HealthPermissions.ClaimRead, HealthPermissions.ClaimCreate, HealthPermissions.ClaimUpdate, HealthPermissions.ClaimApprove,
        HealthPermissions.AccumulatorRead, HealthPermissions.CaseRead, HealthPermissions.CaseUpdate, HealthPermissions.RunRead, HealthPermissions.RunUpdate,
        BillingPermissions.AccountRead, BillingPermissions.InvoiceRead, PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate,
        PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackEndpointUpdate, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, PartyPermissions.PartyCreate,
        PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
    ];

    /// <summary>
    /// D-2: a member's GP claim adjudicated and approved for payment, in a run cut off and calculated (HealthProcessTests).
    /// After an ending other than approval, the run's claims leave it (payment_run_id NULL) for a new run.
    /// </summary>
    private static async Task<Waiting> ProviderPaymentRun(World w)
    {
        await w.Seed("health.mst_sponsor_register", "ACTIVE", "S-ACME", ("sponsor_code", "S-ACME"), ("party", Guid.CreateVersion7().ToString()), ("schemes", Array.Empty<string>()));
        await w.Seed("health.cfg_scheme_member_categories", "EFFECTIVE", "ALL_STAFF", ("category_code", "ALL_STAFF"), ("eligibility_rule", "employment_type = 'PERMANENT'"),
            ("plan", "HLT-CORE"));
        await w.Seed("health.cfg_member_numbering_schemes", "EFFECTIVE", "HM", ("prefix", "HM"), ("sequence_length", 9), ("dependant_suffix", "-01"));
        await w.Seed("health.cfg_health_waiting_period_rules", "EFFECTIVE", "WP-GEN", ("rule_code", "WP-GEN"), ("type", "GENERAL"), ("duration", TimeSpan.FromDays(90)),
            ("exceptions", new[] { "EMERGENCY" }));
        await w.Seed("health.cfg_plans_and_options", "EFFECTIVE", "HLT-CORE-A", ("plan_code", "HLT-CORE"), ("option_code", "A"), ("benefit_schedule", "BS-CORE"),
            ("network", "NET-A"), ("reference_tariff_schedule_code", "REF"), ("benefit_year", "CALENDAR"));
        await w.Seed("health.cfg_network_definitions", "EFFECTIVE", "NET-A", ("network_code", "NET-A"), ("name", "Core network"), ("providers", new[] { "PR-1" }),
            ("plans", new[] { "HLT-CORE" }), ("tariff_schedule_code", "NET-1"));
        await w.Seed("health.cfg_payment_run_calendars", "EFFECTIVE", "PRV-WEEKLY", ("run_code", "PRV-WEEKLY"), ("frequency", "WEEKLY"), ("cut_off", "Wednesday 12:00"),
            ("calendar", "ZA-OFFICE"));
        await w.Db.ExecuteAsOwner("""
            INSERT INTO health.cfg_claim_format_definitions (tenant_id, code, version, effective_from, record_status, created_by, change_reason, format_code, field_mapping, transport)
            VALUES (@t, 'API', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'API', '{}', 'API')
            """, ("t", w.Tenant), ("from", From));
        await w.Seed("health.cfg_benefit_category_mappings", "EFFECTIVE", "M-GP", ("code_system", "NRPL"), ("code_from", "0190"), ("code_to", "0199"),
            ("benefit_category", "GP_VISITS"));
        await w.Seed("health.cfg_limit_definitions", "EFFECTIVE", "LIM-GP-FAM", ("benefit", "GP_VISITS"), ("period", "ANNUAL"), ("amount", 2_000m), ("basis", "PER_FAMILY"));
        await w.Seed("health.cfg_tariff_schedules", "EFFECTIVE", "CON-1-0190", ("tariff_code", "0190"), ("price", 500m), ("schedule_code", "CON-1"));
        await w.Seed("health.cfg_tariff_schedules", "EFFECTIVE", "REF-0190", ("tariff_code", "0190"), ("price", 400m), ("schedule_code", "REF"));
        await w.Db.SeedGlobalStatic("health.ref_procedure_and_service_codes", "ACTIVE", "0190", From, ("code_system", "NRPL"), ("active", true));
        await w.Locale();
        await w.Money();
        await w.Finance(0);

        var scheme = await w.Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await w.Maker.RegisterPerson("Claimant");
        var membership = await w.Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        await w.Maker.AddVerifiedBankAccount(w.Checker, w.Factory, principal, "5500112233");
        var claim = await w.Created("/api/v1/health/claims", new
        {
            source = "MEMBER", membershipId = membership, patientPartyId = principal, serviceDate = Today.AddDays(-3), formatCode = "API", currency = "ZAR",
            lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 650m } },
        });
        await w.Ok(await w.Maker.PostAsync($"/api/v1/health/claims/{claim}/validate", null));
        await w.Ok(await w.Maker.PostAsync($"/api/v1/health/claims/{claim}/adjudicate", null));
        await w.Ok(await w.Maker.PostAsync($"/api/v1/health/claims/{claim}/approve", null));
        var run = await w.Created("/api/v1/health/payment-runs", new { runCode = "PRV-WEEKLY", currency = "ZAR" });
        await w.Ok(await w.Maker.PostAsync($"/api/v1/health/payment-runs/{run}/cut-off", null));
        await w.Ok(await w.Maker.PostAsync($"/api/v1/health/payment-runs/{run}/calculate", null));
        (await w.Db.ScalarAsOwner<Guid?>("SELECT payment_run_id FROM health.medical_claim WHERE id = @c", ("c", claim))).ShouldBe(run);
        var url = $"/api/v1/health/payment-runs/{run}";

        async Task<IReadOnlyList<string>> After(string ending)
        {
            if (ending == Approved)
            {
                return [];
            }

            var inRun = await w.Db.ScalarAsOwner<Guid?>("SELECT payment_run_id FROM health.medical_claim WHERE id = @c", ("c", claim));
            return inRun is null ? [] : [$"D-2: the claim is still in run {inRun}"];
        }

        return new(HealthApprovals.RunBusinessRefType, run, async () => (await w.Get(url)).GetProperty("status").GetString()!, () => w.Get(url), After);
    }
}
