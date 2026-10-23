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
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// The HS256 development signing key is a local development and test convenience only (security fix, Phase 8): any
/// other environment refuses to start with it configured, and does not accept tokens signed with it.
/// </summary>
public sealed class DevelopmentSigningKeyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Host_WithTheDevelopmentKeyConfigured_RefusesToStart_OutsideDevelopmentAndTesting(string environment)
    {
        await using var factory = new InsysApiFactory(fixture.Database, environment: environment);

        var refused = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        refused.Message.ShouldContain("Authentication:DevelopmentSigningKey");
        refused.Message.ShouldContain(environment);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task TokenSignedWithTheDevelopmentKey_IsRejected_OutsideDevelopmentAndTesting(string environment)
    {
        // Without the key the host starts; a token signed with it is still unauthenticated.
        await using var factory = new InsysApiFactory(fixture.Database, new Dictionary<string, string?> { ["Authentication:DevelopmentSigningKey"] = "" }, environment);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dev-key-user", "static_data.read");

        var response = await factory.ClientFor(DatabaseFixture.TenantA, "dev-key-user").GetAsync("/api/v1/static-data/static/currencies");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSignedWithTheDevelopmentKey_IsAccepted_InTesting()
    {
        await using var factory = new InsysApiFactory(fixture.Database);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dev-key-tester", "static_data.read");

        (await factory.ClientFor(DatabaseFixture.TenantA, "dev-key-tester").GetAsync("/api/v1/static-data/static/currencies")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
