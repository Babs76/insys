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
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.Seed;

namespace Insys.IntegrationTests;

/// <summary>
/// The OFTZA sample's demo roles (Ade, 23 November 2026): DEMO_MAKER and DEMO_CHECKER have approval authority for every approval type OFTZA can
/// raise, which is every declared type but TENANT_ADMINISTRATOR (the operator tenant's) and LEGAL_HOLD_RELEASE (the compliance officer's alone).
/// A type added later must join the list, or the demo users can't decide it.
/// </summary>
public sealed class DemoRolesTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task TheDemoRoles_MayApproveEveryTypeOftzaRaises_ButALegalHoldRelease()
    {
        await fixture.Host();   // the modules declare their approval types as the host starts
        var expected = ApprovalKinds.ApprovalTypes.Where(t => t is not "TENANT_ADMINISTRATOR" and not "LEGAL_HOLD_RELEASE").Order(StringComparer.Ordinal);
        OftzaSeed.DemoApprovalTypes.Order(StringComparer.Ordinal).ShouldBe(expected);
        OftzaSeed.DemoApprovalTypes.Distinct(StringComparer.Ordinal).Count().ShouldBe(OftzaSeed.DemoApprovalTypes.Count);
    }
}
