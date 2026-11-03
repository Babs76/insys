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
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>The INSYS Preview's labels: any signed-in user learns the environment and which integrations stubs answer, so the console can say so.</summary>
public sealed class EnvironmentInfoTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task AnySignedInUser_SeesTheEnvironment_AndEveryStubInUse()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "environment-reader");
        var host = await fixture.Host();

        var response = await host.ClientFor(DatabaseFixture.TenantA, "environment-reader").GetAsync("/api/v1/platform/environment");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var view = await response.Json();
        DocumentedSchema.ResponseProblems(view, "core", "PlatformGetEnvironment").ShouldBeEmpty();
        (view.GetProperty("environment").GetString(), view.GetProperty("nonProduction").GetBoolean()).ShouldBe(("Testing", true));
        view.GetProperty("stubs").EnumerateArray().Select(s => s.GetProperty("area").GetString()).ShouldBe(
            ["EMAIL", "GENERAL_LEDGER", "IDENTITY_VERIFICATION", "PAYMENTS", "PUSH", "REGULATOR", "REINSURER", "SCREENING", "SMS", "WHATSAPP"]);
    }

    [Fact]
    public async Task ACallerWhoIsNoUserOfTheTenant_IsRefused()
    {
        var host = await fixture.Host();

        var response = await host.ClientFor(DatabaseFixture.TenantA, "nobody-here").GetAsync("/api/v1/platform/environment");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
