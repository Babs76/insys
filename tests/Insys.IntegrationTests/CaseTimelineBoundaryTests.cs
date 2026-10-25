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
using Insys.Workflow.Application;
using Microsoft.Extensions.Time.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// ADR-025 (addenda/cases.md §12): at 23:30 UTC on Friday 30 October 2026 it is 01:30 on Saturday in Johannesburg. A case
/// received then is acknowledged within two business days on the Johannesburg calendar: Monday and Tuesday, so by Tuesday
/// 17:00 local (15:00 UTC), not counted from the UTC Friday. A host with a fixed clock of its own, as BusinessDateTests.
/// </summary>
public sealed class CaseTimelineBoundaryTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Instant = new(2026, 10, 30, 23, 30, 0, TimeSpan.Zero);
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new InsysApiFactory(fixture.Database, time: new FakeTimeProvider(Instant));
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "case-clerk", CasePermissions.Read, CasePermissions.Create, CasePermissions.Update);
        await CaseTests.SeedConfiguration(fixture.Database);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ATimeline_RunsOnItsBusinessCalendar_InTheCalendarsTimeZone_AtHalfPastElevenUtc_ADR025()
    {
        var response = await _factory.ClientFor(DatabaseFixture.TenantA, "case-clerk").Send(HttpMethod.Post, "/api/v1/workflow/cases", new
        {
            caseTypeCode = "COMPLAINT_T", receivedChannel = "EMAIL", complainantPartyId = Guid.CreateVersion7(), categoryCode = "SERVICE", summary = "Late night complaint",
        }, idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var opened = await response.Json();
        opened.GetProperty("receivedAt").GetDateTimeOffset().ShouldBe(Instant);
        opened.GetProperty("expectedBy").GetDateTimeOffset().ShouldBe(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero),
            "Monday and Tuesday are the two working days: Tuesday 17:00 in Johannesburg is 15:00 UTC");
    }
}
