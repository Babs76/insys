/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
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
using Insys.BuildingBlocks.Application.Context;

namespace Insys.BuildingBlocks.Tests;

/// <summary>F005 §3: what a channel decision allows a command, by product and approval.</summary>
public sealed class ChannelGateTests
{
    private static readonly HashSet<string> Enabled = new(StringComparer.Ordinal) { "GEN-MOTOR", "GEN-HOME" };

    [Fact]
    public void ARowForEveryProduct_AllowsTheChannelsEnabledProductsOnly()
    {
        var gate = new ChannelGate(new ChannelDecision("BRK", null, null, Enabled, [new ChannelRule(null, false)]), hasApprovalFlow: false);

        (gate.Allows("GEN-MOTOR"), gate.Allows("GEN-HOME"), gate.Allows("LIFE-TERM"), gate.Allows(null)).ShouldBe((true, true, false, false));
        gate.Refusal("LIFE-TERM")!.Value.Code.ShouldBe("CHANNEL_NOT_ALLOWED");
        gate.RefusalForEveryProduct.ShouldBeNull();
    }

    [Fact]
    public void ApprovalRequired_IsSatisfiedByAnApprovalFlow_AndRefusedWithoutOne()
    {
        var decision = new ChannelDecision("BRK", null, null, null, [new ChannelRule("GEN-MOTOR", true), new ChannelRule("GEN-HOME", false)]);

        var withFlow = new ChannelGate(decision, hasApprovalFlow: true);
        (withFlow.Allows("GEN-MOTOR"), withFlow.Allows("GEN-HOME")).ShouldBe((true, true));

        var withoutFlow = new ChannelGate(decision, hasApprovalFlow: false);
        (withoutFlow.Allows("GEN-MOTOR"), withoutFlow.Allows("GEN-HOME")).ShouldBe((false, true));
        withoutFlow.Refusal("GEN-MOTOR")!.Value.Code.ShouldBe("CHANNEL_APPROVAL_REQUIRED");
    }

    [Fact]
    public void OnlyApprovalRows_RefuseACommandWithoutAnApprovalFlowWhateverItsProduct()
    {
        var gate = new ChannelGate(new ChannelDecision("BRK", null, null, null, [new ChannelRule(null, true)]), hasApprovalFlow: false);

        gate.RefusalForEveryProduct!.Value.Code.ShouldBe("CHANNEL_APPROVAL_REQUIRED");
    }

    [Fact]
    public void ANarrowedScope_KeepsTheRoleScopeAndTheChannelsProducts()
    {
        var scope = new AccessScope([new PermissionGrant(null, new HashSet<string> { "BR-A" }), new PermissionGrant(new HashSet<string> { "GEN-HOME", "LIFE-TERM" }, null)]);

        var narrowed = scope.RestrictProducts(Enabled);

        (narrowed.Allows("GEN-MOTOR", "BR-A"), narrowed.Allows("LIFE-TERM", "BR-A"), narrowed.Allows("GEN-HOME", "BR-Z"), narrowed.Allows("GEN-MOTOR", "BR-Z"))
            .ShouldBe((true, false, true, false));
    }
}
