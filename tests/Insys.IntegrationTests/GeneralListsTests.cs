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
using Insys.ClaimsFramework.Contracts;
using Insys.General.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-034: paged lists for General (risk objects, proposals, a policy's endorsements, a claim's and all assessments) and a
/// search of the claim register. Each filters and pages, every record once, within the caller's scope (F103): a user
/// scoped to one product sees nothing of another's, and a record outside scope opened by ID is not found.
/// </summary>
public sealed class GeneralListsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private const string In = "BR034-IN", Out = "BR034-OUT";

    private static readonly string[] Permissions =
    [
        GeneralPermissions.RiskRead, GeneralPermissions.QuoteRead, GeneralPermissions.PolicyRead, GeneralPermissions.EndorsementRead, GeneralPermissions.ClaimRead,
        ClaimsPermissions.ClaimRead,
    ];

    private InsysApiFactory _factory = null!;
    private RowFiller _rows = null!;
    private HttpClient _all = null!;
    private HttpClient _scoped = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        _rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
        await fixture.Database.UserWithPermissions(T, "br034-all", Permissions);
        var scoped = await fixture.Database.UserWithPermissions(T, "br034-scoped", Permissions);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY[@p] WHERE user_id = @u", ("p", In), ("u", scoped));
        _all = _factory.ClientFor(T, "br034-all");
        _scoped = _factory.ClientFor(T, "br034-scoped");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RiskObjects_FilterByTypeZoneAndPolicy_ThePolicyWithinScope_BR034()
    {
        var zone = "Z" + Guid.NewGuid().ToString("N")[..8];
        var vehicles = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            vehicles.Add(await Risk("VEHICLE", zone));
        }

        var building = await Risk("BUILDING", zone);
        var inScope = await Policy(In);
        var outOfScope = await Policy(Out);
        await Attach(inScope, vehicles[0]);
        await Attach(outOfScope, vehicles[1]);

        (await All(_all, $"general/risk-objects?riskZone={zone}&objectType=VEHICLE")).ShouldBe(Sorted([.. vehicles]));
        (await All(_all, $"general/risk-objects?riskZone={zone}")).ShouldBe(Sorted([.. vehicles, building]));
        (await All(_all, $"general/risk-objects?policyId={inScope}")).ShouldBe([vehicles[0]]);
        (await All(_scoped, $"general/risk-objects?policyId={inScope}")).ShouldBe([vehicles[0]]);
        (await All(_scoped, $"general/risk-objects?policyId={outOfScope}")).ShouldBeEmpty("the policy is outside the caller's scope");
    }

    [Fact]
    public async Task Proposals_FilterByQuoteAndStatus_ThroughTheirQuotesScope_BR034()
    {
        var quoteIn = await Quote(In);
        var quoteOut = await Quote(Out);
        var proposalIn = await Proposal(quoteIn, "SUBMITTED");
        var proposalOut = await Proposal(quoteOut, "ACCEPTED");

        (await All(_all, $"general/proposals?quoteId={quoteIn}")).ShouldBe([proposalIn]);
        (await All(_all, $"general/proposals?quoteId={quoteOut}&status=Accepted")).ShouldBe([proposalOut]);
        (await All(_all, $"general/proposals?quoteId={quoteOut}&status=Submitted")).ShouldBeEmpty();
        (await All(_scoped, $"general/proposals?quoteId={quoteOut}")).ShouldBeEmpty("its quote is outside the caller's scope");
        (await All(_scoped, "general/proposals")).ShouldNotContain(proposalOut);
    }

    [Fact]
    public async Task APolicysEndorsements_PageEveryOneOnce_AndAPolicyOutsideScopeIsNotFound_BR034()
    {
        var policy = await Policy(In);
        var other = await Policy(Out);
        var endorsements = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            endorsements.Add(await _rows.Insert(T, "general.endorsement", ("policy_id", policy), ("status", "DRAFT"), ("currency", "ZAR"),
                ("change_set", JsonDocument.Parse("[]").RootElement)));
        }

        await _rows.Insert(T, "general.endorsement", ("policy_id", other), ("status", "DRAFT"), ("currency", "ZAR"), ("change_set", JsonDocument.Parse("[]").RootElement));
        (await All(_all, $"general/policies/{policy}/endorsements")).ShouldBe(Sorted([.. endorsements]));
        (await _scoped.GetAsync($"/api/v1/general/policies/{other}/endorsements")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Assessments_ListByClaimStatusAndAssessor_OnlyOnClaimsInScope_BR034()
    {
        var claimIn = await Claim(In);
        var claimOut = await Claim(Out);
        var assessor = Guid.CreateVersion7();
        var assigned = await Assessment(claimIn, assessor, "ASSIGNED");
        var reported = await Assessment(claimIn, assessor, "REPORTED");
        var outside = await Assessment(claimOut, assessor, "ASSIGNED");

        (await All(_all, $"general/claims/{claimIn}/assessments")).ShouldBe(Sorted(assigned, reported));
        (await All(_all, $"general/assessments?assessorPartyId={assessor}")).ShouldBe(Sorted(assigned, reported, outside));
        (await All(_all, $"general/assessments?assessorPartyId={assessor}&status=Reported")).ShouldBe([reported]);
        (await All(_scoped, $"general/assessments?assessorPartyId={assessor}")).ShouldBe(Sorted(assigned, reported), "only on claims in scope");
        (await _scoped.GetAsync($"/api/v1/general/claims/{claimOut}/assessments")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _scoped.GetAsync($"/api/v1/general/assessments/{outside}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "read through its claim's scope");
    }

    [Fact]
    public async Task TheClaimRegister_IsSearchedByNumberDomainPolicyAndRegistrationTime_WithinScope_BR034()
    {
        var policy = Guid.CreateVersion7();
        var start = DateTimeOffset.UtcNow.AddDays(-10);
        var general = await Registered("GENERAL", policy, start, In);
        var life = await Registered("LIFE", policy, start.AddDays(2), In);
        var outside = await Registered("GENERAL", policy, start.AddDays(4), Out);

        (await All(_all, $"claims/claims?policyRef={policy}", "id")).ShouldBe(Sorted(general.Id, life.Id, outside.Id));
        (await All(_all, $"claims/claims?policyRef={policy}&domain=general", "id")).ShouldBe(Sorted(general.Id, outside.Id));
        (await All(_all, $"claims/claims?claimNumber={life.Number}", "id")).ShouldBe([life.Id]);
        var from = Uri.EscapeDataString(start.AddDays(1).ToString("O"));
        var to = Uri.EscapeDataString(start.AddDays(3).ToString("O"));
        (await All(_all, $"claims/claims?policyRef={policy}&registeredFrom={from}&registeredTo={to}", "id")).ShouldBe([life.Id]);
        (await All(_scoped, $"claims/claims?policyRef={policy}", "id")).ShouldBe(Sorted(general.Id, life.Id), "within the caller's scope");
        (await _all.GetAsync($"/api/v1/claims/claims?registeredFrom={to}&registeredTo={from}")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static async Task<List<Guid>> All(HttpClient client, string list, string idProperty = "id")
    {
        var seen = new List<Guid>();
        string? cursor = null;
        var separator = list.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        for (var page = 0; page < 50; page++)
        {
            var response = await client.GetAsync($"/api/v1/{list}{separator}limit=2{(cursor is null ? "" : $"&cursor={cursor}")}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{list}: {await response.Content.ReadAsStringAsync()}");
            var body = await response.Json();
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(idProperty).GetGuid()));
            if (body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null)
            {
                return seen;
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }

        throw new InvalidOperationException($"{list} did not end.");
    }

    private static List<Guid> Sorted(params Guid[] ids) => [.. ids.Order()];

    private Task<Guid> Risk(string type, string zone) => _rows.Insert(T, "general.risk_object", ("object_type", type), ("risk_zone", zone), ("status", "ACTIVE"),
        ("attributes", JsonDocument.Parse("{}").RootElement));

    private Task<Guid> Policy(string product) => _rows.Insert(T, "general.policy", ("product_code", product), ("currency", "ZAR"));

    private Task Attach(Guid policy, Guid risk) => _rows.Insert(T, "general.policy_risk", ("policy_id", policy), ("risk_object_id", risk), ("attached_from", new DateOnly(2026, 1, 1)));

    private Task<Guid> Quote(string product) => _rows.Insert(T, "general.quote", ("product_code", product), ("status", "DRAFT"), ("currency", "ZAR"));

    private Task<Guid> Proposal(Guid quote, string status) => _rows.Insert(T, "general.proposal", ("quote_id", quote), ("status", status),
        ("declarations", JsonDocument.Parse("{}").RootElement));

    private Task<Guid> Claim(string product) => _rows.Insert(T, "general.claim", ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"), ("product_code", product));

    private Task<Guid> Assessment(Guid claim, Guid assessor, string status) =>
        _rows.Insert(T, "general.loss_assessment", ("claim_id", claim), ("assessor_party_id", assessor), ("status", status), ("currency", "ZAR"),
            ("assessed_amount", 1000m), ("excess_applied", 0m), ("assessed_at", DateTimeOffset.UtcNow));

    private async Task<(Guid Id, string Number)> Registered(string domain, Guid policy, DateTimeOffset at, string product)
    {
        var number = $"BR34-{Guid.NewGuid():N}"[..20];
        var id = await _rows.Insert(T, "claims.claim_register", ("claim_number", number), ("domain", domain), ("policy_ref", policy), ("registered_at", at),
            ("product_code", product));
        return (id, number);
    }
}
