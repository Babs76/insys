/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity.Tests
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
using Insys.Identity.Domain.Tenants;

namespace Insys.Identity.Tests;

/// <summary>A granted request whose grant can't be applied ends NotApplied (OQ-099 follow-up, Ade, 21 November 2026).</summary>
public sealed class TenantAdministratorRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Maker = Guid.CreateVersion7();
    private static readonly Guid Checker = Guid.CreateVersion7();

    private static TenantAdministratorRequest Request() => TenantAdministratorRequest.ForNewTenant(Guid.CreateVersion7(),
        [new("a", "A", "OIDC", "a", true, null), new("b", "B", "OIDC", "b", true, null)], ["IDENTITY_USERS"], Maker, Now).Value;

    [Fact]
    public void AGrantNotApplied_EndsTheRequest_WithTheReasonAndTheChecker_OQ099()
    {
        var request = Request();
        request.MarkNotApplied("The tenant already has users.", Checker, Now).IsSuccess.ShouldBeTrue();
        (request.Status, request.NotAppliedReason, request.DecidedBy, request.DecidedAt)
            .ShouldBe((TenantAdministratorRequestStatus.NotApplied, "The tenant already has users.", (Guid?)Checker, (DateTimeOffset?)Now));

        // Final: neither a decision nor a second not-applied changes it.
        request.MarkNotApplied("Again.", Checker, Now).IsFailure.ShouldBeTrue();
        request.Decide(true, Checker, [Guid.CreateVersion7(), Guid.CreateVersion7()], Now).IsFailure.ShouldBeTrue();
        request.NotAppliedReason.ShouldBe("The tenant already has users.");
    }

    [Fact]
    public void AGrantNotApplied_IsNeverDecidedByTheRequester_SRS002()
    {
        var request = Request();
        request.MarkNotApplied("The tenant already has users.", Maker, Now).IsFailure.ShouldBeTrue();
        request.Status.ShouldBe(TenantAdministratorRequestStatus.PendingApproval);
    }

    [Fact]
    public void ANotAppliedReason_IsKeptToItsColumnsLength()
    {
        var request = Request();
        request.MarkNotApplied(new string('x', 600), Checker, Now).IsSuccess.ShouldBeTrue();
        request.NotAppliedReason!.Length.ShouldBe(TenantAdministratorRequest.NotAppliedReasonLength);
    }
}
