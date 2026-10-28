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
using Insys.General.Contracts;
using Insys.Health.Contracts;
using Insys.Life.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.Party.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-091 (external-user access §2): the portals list a member's own Life and General policies, Health memberships and
/// claims, and a broker's policies, claims and clients, scoped only by the caller's link. Another member's or broker's
/// records never appear in a list or by ID; a firm principal sees its sub-broker's, the sub-broker not the principal's;
/// the same link in another tenant sees nothing. Every list pages with a cursor, each record once. Privacy check (28 October
/// 2026): an insured life who is not the policyholder, and a Health dependant, see only their own cover and claims.
/// </summary>
public sealed class PortalListTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid A = DatabaseFixture.TenantA;
    private static readonly Guid B = DatabaseFixture.TenantB;
    private static readonly DateOnly From = new(2026, 1, 1);

    private static readonly string[] MemberPermissions =
    [
        GeneralPermissions.PolicyRead, GeneralPermissions.ClaimRead, LifePermissions.PolicyRead, LifePermissions.ClaimRead, HealthPermissions.MembershipRead,
        HealthPermissions.ClaimRead,
    ];

    private static readonly string[] BrokerPermissions = [.. MemberPermissions, PartyPermissions.PartyRead];

    private InsysApiFactory _factory = null!;
    private RowFiller _rows = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        _rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
        foreach (var tenant in new[] { A, B })
        {
            foreach (var (code, audience, clientId) in new[] { ("MEM91", "CUSTOMER", "member-portal"), ("BRK91", "BROKER", "broker-portal") })
            {
                await fixture.Database.SeedStatic(tenant, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-" + code, From, ("channel_code", code), ("audience", audience),
                    ("name", audience + " portal"), ("client_ids", new[] { clientId }));
            }
        }

        // A firm principal (P91) with a sub-broker (S91) below it in the distribution hierarchy, and another broker (O91).
        foreach (var (node, parent) in new[] { ("N91-P", (string?)null), ("N91-S", "N91-P"), ("N91-O", null) })
        {
            await fixture.Database.SeedStatic(A, "commission.cfg_distribution_hierarchy", "EFFECTIVE", node, From, ("node_code", node),
                ("parent", (object?)parent ?? DBNull.Value), ("overriding_commission", false));
        }

        foreach (var (code, node) in new[] { ("P91", "N91-P"), ("S91", "N91-S"), ("O91", "N91-O") })
        {
            await fixture.Database.SeedStatic(A, "commission.mst_intermediary_register", "ACTIVE", code, From, ("intermediary_code", code), ("type", "BROKER"),
                ("legal_name", code), ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-91" })), ("status", "ACTIVE"), ("hierarchy_node", node));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task MembersAndBrokers_ListOnlyTheirOwnRecords_AndNeverAnothersByID_BR091()
    {
        var memberA = await Party("Member A");
        var memberB = await Party("Member B");
        var clientS = await Party("Client of the sub-broker");
        var clientQ = await Party("Quoted client");
        var clientL = await Party("Life applicant");
        var registered = await Party("Registered client", ("registered_by", "INTERMEDIARY:P91"));
        var survivor = await Party("Linked after review");
        await Party("Registration linked", ("registered_by", "INTERMEDIARY:P91"), ("status_code", "MERGED"), ("merged_into_id", survivor));
        await Party("Anonymised", ("registered_by", "INTERMEDIARY:P91"), ("status_code", "ANONYMISED"));

        // General: a policy and a claim each for member A (broker P91), member B (O91) and the sub-broker's client (S91); a quote of P91's.
        var policyA = await GeneralPolicy(memberA, "P91");
        var policyB = await GeneralPolicy(memberB, "O91");
        var policyS = await GeneralPolicy(clientS, "S91");
        var claimA = await GeneralClaim(policyA);
        var claimB = await GeneralClaim(policyB);
        var claimS = await GeneralClaim(policyS);
        await _rows.Insert(A, "general.quote", ("status", "DRAFT"), ("proposer_party_id", clientQ), ("intermediary_code", "P91"));

        // Life: member A's policy (P91); member B's (O91), on which member A is an insured life; member B's own (O91); a proposal of S91's.
        var lifeA = await LifePolicy(memberA, "P91");
        var lifeInsured = await LifePolicy(memberB, "O91");
        await _rows.Insert(A, "life.policy_insured_life", ("policy_id", lifeInsured), ("party_id", memberA), ("life_role", "SPOUSE"), ("entry_age", (short)40));
        var lifeB = await LifePolicy(memberB, "O91");
        await _rows.Insert(A, "life.proposal", ("applicant_party_id", clientL), ("intermediary_code", "S91"));

        // Health: member A's membership; member B's, with member A as a dependant; and member B's own.
        var membershipA = await Membership(memberA);
        var membershipDependant = await Membership(memberB);
        await _rows.Insert(A, "health.member_dependant", ("membership_id", membershipDependant), ("party_id", memberA), ("dependant_type", "SPOUSE"), ("suffix", "01"),
            ("start_date", From), ("status", "ACTIVE"));
        var membershipB = await Membership(memberB);

        var a = await Member(A, "member-91a", memberA);
        var b = await Member(A, "member-91b", memberB);
        var principal = await Broker(A, "broker-91p", "P91");
        var sub = await Broker(A, "broker-91s", "S91");
        var other = await Broker(A, "broker-91o", "O91");

        (await All(a, "general/policies")).ShouldBe([policyA]);
        (await All(a, "general/claims")).ShouldBe([claimA]);
        (await All(a, "life/policies")).ShouldBe(Sorted(lifeA, lifeInsured));
        (await All(a, "health/memberships")).ShouldBe(Sorted(membershipA, membershipDependant));
        (await All(b, "general/policies")).ShouldBe([policyB]);
        (await All(b, "general/claims")).ShouldBe([claimB]);
        (await All(b, "life/policies")).ShouldBe(Sorted(lifeInsured, lifeB));
        (await All(b, "health/memberships")).ShouldBe(Sorted(membershipDependant, membershipB));

        (await All(principal, "general/policies")).ShouldBe(Sorted(policyA, policyS), "a firm principal sees its sub-broker's");
        (await All(principal, "general/claims")).ShouldBe(Sorted(claimA, claimS));
        (await All(principal, "life/policies")).ShouldBe([lifeA]);
        (await All(principal, "party/parties")).ShouldBe(Sorted(memberA, clientS, clientQ, clientL, registered, survivor),
            "clients it or its sub-broker registered (a linked registration as the party it was linked to), hold policies of or were quoted or proposed for");
        (await All(sub, "general/policies")).ShouldBe([policyS], "a sub-broker does not see its principal's");
        (await All(sub, "general/claims")).ShouldBe([claimS]);
        (await All(sub, "life/policies")).ShouldBeEmpty();
        (await All(sub, "party/parties")).ShouldBe(Sorted(clientS, clientL));
        (await All(other, "life/policies")).ShouldBe(Sorted(lifeInsured, lifeB));
        (await All(other, "party/parties")).ShouldBe([memberB]);

        // By ID, another's record is not found, as one that does not exist.
        foreach (var path in new[] { $"general/policies/{policyB}", $"general/claims/{claimB}", $"life/policies/{lifeB}", $"health/memberships/{membershipB}" })
        {
            (await a.GetAsync($"/api/v1/portal/{path}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }

        foreach (var path in new[] { $"general/policies/{policyB}", $"general/claims/{claimB}", $"life/policies/{lifeB}", $"party/parties/{memberB}" })
        {
            (await principal.GetAsync($"/api/v1/portal/{path}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }

        (await sub.GetAsync($"/api/v1/portal/general/policies/{policyA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await sub.GetAsync($"/api/v1/portal/party/parties/{memberA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A client's portal view is its name and kind only: no identifiers, contact points, bank accounts or review status.
        var client = await (await principal.GetAsync($"/api/v1/portal/party/parties/{memberA}")).Json();
        client.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["displayName", "id", "kind", "status"]);
        client.GetProperty("displayName").GetString().ShouldBe("Member A");

        // The client list is the broker portal's: a member is refused it.
        (await a.GetAsync("/api/v1/portal/party/parties")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The same link keys in another tenant see none of this tenant's records.
        var elsewhere = await Member(B, "member-91a-b", memberA);
        var brokerElsewhere = await Broker(B, "broker-91p-b", "P91");
        foreach (var list in new[] { "general/policies", "general/claims", "life/policies", "health/memberships" })
        {
            (await All(elsewhere, list)).ShouldBeEmpty(list);
        }

        foreach (var list in new[] { "general/policies", "general/claims", "life/policies", "party/parties" })
        {
            (await All(brokerElsewhere, list)).ShouldBeEmpty(list);
        }

        (await elsewhere.GetAsync($"/api/v1/portal/general/policies/{policyA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await brokerElsewhere.GetAsync($"/api/v1/portal/party/parties/{memberA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Privacy check (Ade, 28 October 2026): an insured life who is not the policyholder sees only the benefits on their own
    /// life, without premiums, and nothing of the policyholder (their party, the other lives' cover, the beneficiaries, the
    /// policy's documents). A Health dependant sees the plan they are covered by and their own entry, not the principal or
    /// the other dependants, and only the claims where they are the patient.
    /// </summary>
    [Fact]
    public async Task AnInsuredLifeAndADependant_SeeOnlyTheirOwnCoverAndClaims_PrivacyCheck()
    {
        var holder = await Party("Policyholder");
        var insured = await Party("Insured spouse");
        var policy = await LifePolicy(holder, "X91"); // a code no broker in this class is linked to, so the list test is unaffected
        var holderLife = await _rows.Insert(A, "life.policy_insured_life", ("policy_id", policy), ("party_id", holder), ("life_role", "MAIN"), ("entry_age", (short)45));
        var insuredLife = await _rows.Insert(A, "life.policy_insured_life", ("policy_id", policy), ("party_id", insured), ("life_role", "SPOUSE"), ("entry_age", (short)43));
        await Benefit(policy, holderLife, "DEATH", 1_000_000m, 450m);
        await Benefit(policy, holderLife, "DISABILITY", 500_000m, 120m);
        await Benefit(policy, insuredLife, "DEATH", 250_000m, 90m);
        await _rows.Insert(A, "life.beneficiary", ("policy_id", policy), ("party_id", insured), ("relationship_code", "SPOUSE"), ("share_pct", 100m),
            ("valid_from", From));

        var asHolder = await Member(A, "privacy-holder", holder);
        var asInsured = await Member(A, "privacy-insured", insured);

        var full = await (await asHolder.GetAsync($"/api/v1/portal/life/policies/{policy}")).Json();
        full.GetProperty("viewedAs").GetString().ShouldBe("POLICYHOLDER");
        full.GetProperty("benefits").GetArrayLength().ShouldBe(3);
        full.GetProperty("beneficiaries").GetArrayLength().ShouldBe(1);

        var listed = await All(asInsured, "life/policies");
        listed.ShouldBe([policy]);
        var page = await (await asInsured.GetAsync("/api/v1/portal/life/policies")).Json();
        foreach (var own in new[] { await (await asInsured.GetAsync($"/api/v1/portal/life/policies/{policy}")).Json(), page.GetProperty("items")[0] })
        {
            own.GetProperty("viewedAs").GetString().ShouldBe("INSURED_LIFE");
            own.GetProperty("policyholderPartyId").ValueKind.ShouldBe(JsonValueKind.Null, "nothing of the policyholder");
            own.GetProperty("beneficiaries").ValueKind.ShouldBe(JsonValueKind.Null);
            var benefits = own.GetProperty("benefits").EnumerateArray().ToList();
            benefits.Select(b => (b.GetProperty("benefitCode").GetString(), b.GetProperty("sumAssured").GetDecimal())).ShouldBe([("DEATH", 250_000m)],
                "only the cover on their own life");
            benefits.ShouldAllBe(b => b.GetProperty("premium").ValueKind == JsonValueKind.Null, "no premiums");
            own.ToString().ShouldNotContain(holder.ToString());
        }

        // The policy's documents (its schedule shows the whole policy) are the policyholder's, not the insured life's.
        var relations = _factory.Services.GetServices<IPortalRecordRelations>().Single(r => r.Covers(LifeDocuments.PolicyRefType));
        (await relations.Related(A, Subject(holder), LifeDocuments.PolicyRefType, policy, CancellationToken.None)).ShouldBeTrue();
        (await relations.Related(A, Subject(insured), LifeDocuments.PolicyRefType, policy, CancellationToken.None)).ShouldBeFalse();

        // Health: a principal with two dependants, and a claim for each of the three.
        var principal = await Party("Principal");
        var dependant = await Party("Dependant one");
        var otherDependant = await Party("Dependant two");
        var membership = await Membership(principal);
        foreach (var (party, suffix) in new[] { (dependant, "01"), (otherDependant, "02") })
        {
            await _rows.Insert(A, "health.member_dependant", ("membership_id", membership), ("party_id", party), ("dependant_type", "CHILD"), ("suffix", suffix),
                ("start_date", From), ("status", "ACTIVE"));
        }

        var principalClaim = await MedicalClaim(membership, principal);
        var dependantClaim = await MedicalClaim(membership, dependant);
        var otherClaim = await MedicalClaim(membership, otherDependant);
        var asPrincipal = await Member(A, "privacy-principal", principal);
        var asDependant = await Member(A, "privacy-dependant", dependant);

        var whole = await (await asPrincipal.GetAsync($"/api/v1/portal/health/memberships/{membership}")).Json();
        whole.GetProperty("viewedAs").GetString().ShouldBe("PRINCIPAL");
        whole.GetProperty("dependants").GetArrayLength().ShouldBe(2);
        (await All(asPrincipal, "health/claims")).ShouldBe(Sorted(principalClaim, dependantClaim, otherClaim));

        (await All(asDependant, "health/memberships")).ShouldBe([membership]);
        var mine = await (await asDependant.GetAsync($"/api/v1/portal/health/memberships/{membership}")).Json();
        mine.GetProperty("viewedAs").GetString().ShouldBe("DEPENDANT");
        mine.GetProperty("planCode").GetString().ShouldNotBeNullOrEmpty();
        mine.GetProperty("principalPartyId").ValueKind.ShouldBe(JsonValueKind.Null);
        mine.GetProperty("dependants").EnumerateArray().Select(d => d.GetProperty("partyId").GetGuid()).ShouldBe([dependant]);
        mine.ToString().ShouldNotContain(principal.ToString());
        mine.ToString().ShouldNotContain(otherDependant.ToString());

        (await All(asDependant, "health/claims")).ShouldBe([dependantClaim], "only the claims where they are the patient");
        (await asDependant.GetAsync($"/api/v1/portal/health/claims/{dependantClaim}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await asDependant.GetAsync($"/api/v1/portal/health/claims/{principalClaim}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await asDependant.GetAsync($"/api/v1/portal/health/claims/{otherClaim}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Every page of a portal list, one record at a time, so the cursor is followed past each page boundary.</summary>
    private static async Task<List<Guid>> All(HttpClient client, string list)
    {
        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var response = await client.GetAsync($"/api/v1/portal/{list}?limit=1{(cursor is null ? "" : $"&cursor={cursor}")}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{list}: {await response.Content.ReadAsStringAsync()}");
            var body = await response.Json();
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            if (body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null)
            {
                return seen;
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }

        throw new InvalidOperationException($"{list} did not end.");
    }

    private static List<Guid> Sorted(params Guid[] ids) => [.. ids.Order()];

    private Task<Guid> Party(string name, params (string Column, object Value)[] more) =>
        _rows.Insert(A, "party.party", [("party_kind", "INDIVIDUAL"), ("status_code", "ACTIVE"), ("display_name", name), .. more]);

    private Task<Guid> GeneralPolicy(Guid holder, string broker) =>
        _rows.Insert(A, "general.policy", ("policyholder_party_id", holder), ("intermediary_code", broker), ("currency", "ZAR"));

    private Task<Guid> GeneralClaim(Guid policy) => _rows.Insert(A, "general.claim", ("policy_id", policy), ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"));

    private Task<Guid> LifePolicy(Guid holder, string broker) =>
        _rows.Insert(A, "life.policy", ("policyholder_party_id", holder), ("intermediary_code", broker), ("status", "IN_FORCE"), ("currency", "ZAR"));

    private Task<Guid> Benefit(Guid policy, Guid life, string code, decimal sumAssured, decimal premium) =>
        _rows.Insert(A, "life.policy_benefit", ("policy_id", policy), ("insured_life_id", life), ("benefit_code", code), ("sum_assured", sumAssured), ("premium", premium),
            ("start_date", From), ("status", "ACTIVE"));

    private async Task<Guid> MedicalClaim(Guid membership, Guid patient)
    {
        var claim = await _rows.Insert(A, "health.medical_claim", ("membership_id", membership), ("patient_party_id", patient), ("source", "PROVIDER"),
            ("status", "RECEIVED"), ("currency", "ZAR"));
        await _rows.Insert(A, "health.medical_claim_line", ("claim_id", claim), ("line_no", (short)1), ("tariff_code", "0190"), ("diagnosis_code", "J06.9"),
            ("quantity", (short)1));
        return claim;
    }

    private static ExternalSubject Subject(Guid party) => new(ExternalSubject.Party, party, null, new HashSet<string>(), null);

    private Task<Guid> Membership(Guid principal) => _rows.Insert(A, "health.membership", ("principal_party_id", principal), ("status", "ACTIVE"));

    private async Task<HttpClient> Member(Guid tenant, string subject, Guid party)
    {
        await Link(tenant, subject, "PARTY", party, null, MemberPermissions);
        return _factory.ClientFor(tenant, subject, "member-portal");
    }

    private async Task<HttpClient> Broker(Guid tenant, string subject, string code)
    {
        await Link(tenant, subject, "INTERMEDIARY", null, code, BrokerPermissions);
        return _factory.ClientFor(tenant, subject, "broker-portal", multiFactor: true);
    }

    private async Task Link(Guid tenant, string subject, string kind, Guid? party, string? broker, string[] permissions)
    {
        var user = await fixture.Database.UserWithPermissions(tenant, subject, permissions);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               provider_id, status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @k, @p, @i, NULL, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", tenant), ("u", user), ("k", kind), ("p", (object?)party ?? DBNull.Value), ("i", (object?)broker ?? DBNull.Value));
    }
}
