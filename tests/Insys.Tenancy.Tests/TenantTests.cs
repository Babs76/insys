/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy.Tests
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
using Insys.BuildingBlocks.Domain;
using Insys.Tenancy.Domain.Tenants;
using Insys.Testing;

namespace Insys.Tenancy.Tests;

public sealed class TenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operator = Guid.CreateVersion7();

    private static Tenant Registered() => Tenant.RegisterTenant(Guid.CreateVersion7(), "ACME", "Acme", IsolationProfile.B, Now).Value;

    private static Tenant Active()
    {
        var tenant = Registered();
        foreach (var step in OnboardingSteps.All)
        {
            tenant.CompleteOnboardingStep(step, Now);
        }

        tenant.ActivateTenant(Operator, "Go live", Now);
        return tenant;
    }

    [Fact]
    public void Tenant_Lifecycle_MatchesDomainModel() =>
        Tenant.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList()
            .ShouldBe(DomainModelSpec.Transitions("tenant", "Tenant"));

    [Fact]
    public void Tenant_IdentifierIsRequired_TEN001() =>
        Tenant.RegisterTenant(Guid.Empty, "ACME", "Acme", IsolationProfile.A, Now).Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);

    [Fact]
    public void Activation_RequiresCompletedOnboarding_TEN008()
    {
        var tenant = Registered();
        tenant.ActivateTenant(Operator, "Go live", Now).Error!.Code.ShouldBe("ONBOARDING_INCOMPLETE");
        tenant.CompleteOnboardingStep(OnboardingSteps.All[1], Now).Error!.Code.ShouldBe("ONBOARDING_STEP_OUT_OF_ORDER");
    }

    [Fact]
    public void Suspension_RaisesTenantSuspended_AndIsRecordedWithItsReason_TEN008()
    {
        var tenant = Active();

        tenant.SuspendTenant(Operator, "Unpaid fees", Now).IsSuccess.ShouldBeTrue();

        tenant.Status.ShouldBe(TenantStatus.Suspended);
        tenant.DomainEvents.OfType<TenantSuspended>().ShouldHaveSingleItem();
        tenant.StatusChanges[^1].Reason.ShouldBe("Unpaid fees");
    }

    [Fact]
    public void StatusChange_WithoutAReason_IsRefused_TEN008() =>
        Active().SuspendTenant(Operator, " ", Now).Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);

    [Fact]
    public void Terminated_CannotBeReactivated_IsInvalidStateTransition()
    {
        var tenant = Active();
        tenant.TerminateTenant(Operator, "Contract ended", Now).IsSuccess.ShouldBeTrue();

        tenant.ReactivateTenant(Operator, "Oops", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void ChangeIsolationProfile_RequiresAnApprovedDataMigration() =>
        Active().ChangeIsolationProfile(IsolationProfile.C, "", Operator, Now).Error!.Code.ShouldBe("DATA_MIGRATION_REQUIRED");
}
