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
using System.Net.Http.Json;
using Insys.Party.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// OQ-127 (decided 29 October 2026): staff registering a person who matches an existing party, by the same names (ignoring
/// case) and birth date, get 409 PARTY_POSSIBLE_DUPLICATE with the candidates; registering again with confirmNew and a
/// reason creates the party and records the review (party.duplicate_candidate, PTY-004), audited.
/// </summary>
public sealed class PartyDuplicateTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Parties = "/api/v1/party/parties";
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dup-clerk", PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate);
        _clerk = _factory.ClientFor(DatabaseFixture.TenantA, "dup-clerk");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AStaffRegistration_MatchingAnExistingParty_IsRefusedWithTheCandidates_UntilConfirmedNewWithAReason_OQ127()
    {
        var surname = "Dup" + Guid.NewGuid().ToString("N")[..10];
        var existing = await Register(new { firstNames = "Thandi", surname, birthDate = "1979-03-12", nationality = "ZA" });
        existing.StatusCode.ShouldBe(HttpStatusCode.Created, await existing.Content.ReadAsStringAsync());
        var existingId = (await existing.Json()).GetProperty("id").GetGuid();

        // The same person, in other case and with spaces around the names: refused, naming the existing party.
        var duplicate = await Register(new { firstNames = " thandi ", surname = surname.ToUpperInvariant(), birthDate = "1979-03-12", nationality = "ZA" });
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await duplicate.Json();
        problem.GetProperty("code").GetString().ShouldBe("PARTY_POSSIBLE_DUPLICATE");
        var candidate = problem.GetProperty("candidates").EnumerateArray().Single();
        (candidate.GetProperty("partyId").GetGuid(), candidate.GetProperty("displayName").GetString()).ShouldBe((existingId, $"Thandi {surname}"));
        candidate.GetProperty("partyNumber").GetString().ShouldNotBeNullOrEmpty();

        // Another birth date is another person.
        (await Register(new { firstNames = "Thandi", surname, birthDate = "1979-03-13", nationality = "ZA" })).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Confirmed new needs a reason; with one, the party is created and the review recorded.
        var noReason = await Register(new { firstNames = "Thandi", surname, birthDate = "1979-03-12", nationality = "ZA" }, confirmNew: true);
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var confirmed = await Register(new { firstNames = "Thandi", surname, birthDate = "1979-03-12", nationality = "ZA" }, confirmNew: true,
            reason: "A different person: the client's cousin, met in person");
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Created, await confirmed.Content.ReadAsStringAsync());
        var newId = (await confirmed.Json()).GetProperty("id").GetGuid();
        (await fixture.Database.ScalarAsOwner<string>(
                "SELECT status || ':' || rule_code FROM party.duplicate_candidate WHERE party_a_id = @a AND party_b_id = @b", ("a", newId), ("b", existingId)))
            .ShouldBe("DISTINCT:NAME_DOB");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>(
                "SELECT details->>'reason' FROM audit.audit_event WHERE operation = 'RegisterParty' AND target_id = @id", ("id", newId.ToString())))
            .ShouldBe("A different person: the client's cousin, met in person");

    }

    private Task<HttpResponseMessage> Register(object individual, bool confirmNew = false, string? reason = null) =>
        _clerk.Send(HttpMethod.Post, Parties, new { individual, confirmNew, reason }, idempotent: true);
}
