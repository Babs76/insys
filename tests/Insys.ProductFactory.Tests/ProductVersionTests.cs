/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory.Tests
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
using Insys.ProductFactory.Domain.Products;
using Insys.Testing;

namespace Insys.ProductFactory.Tests;

public sealed class ProductVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static readonly ProductValidationContext Library = new(
        new Dictionary<string, CoverageDefinition>
        {
            ["OWN_DAMAGE"] = new("OWN_DAMAGE", ProductDomain.General),
            ["THEFT"] = new("THEFT", ProductDomain.General),
            ["TPL"] = new("TPL", ProductDomain.General),
            ["DEATH"] = new("DEATH", ProductDomain.Life),
        },
        [new BenefitDependency("THEFT", "REQUIRES", "OWN_DAMAGE"), new BenefitDependency("TPL", "EXCLUDES", "DEATH")],
        Today,
        null);

    private static ProductVersion Draft(params string[] coverages) =>
        ProductVersion.RegisterProduct(Guid.CreateVersion7(), "GEN-MOTOR-PV", ProductDomain.General, "GEN-MOTOR", null, "Private vehicle",
            coverages.Length == 0 ? ["OWN_DAMAGE", "THEFT"] : coverages, Today.AddDays(30), Now);

    private static ProductVersion Published()
    {
        var version = Draft();
        version.ValidateVersion(Library, Now).IsSuccess.ShouldBeTrue();
        version.SubmitForApproval(Guid.CreateVersion7(), simulationPassed: true, "Launch", Now).IsSuccess.ShouldBeTrue();
        version.ApproveVersion(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        version.PublishVersion(Now).IsSuccess.ShouldBeTrue();
        return version;
    }

    [Fact]
    public void ProductVersion_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("product", "ProductVersion")
            .Select(t => t.Replace("Approval rejected", "ApprovalRejected", StringComparison.Ordinal).Replace("Effective date reached", "EffectiveDateReached", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        ProductVersion.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void EligibilityRuleSetAndReinstatementArrears_AreEditedInDraft_AndCarriedToTheNextVersion()
    {
        var draft = Draft();
        draft.ReinstatementArrears.ShouldBe(ReinstatementArrears.None);
        draft.EditConfiguration(draft.Name, draft.Coverages, draft.EffectiveFrom, eligibilityRulesetCode: "LTERM-ELIG",
            reinstatementArrears: ReinstatementArrears.Required).IsSuccess.ShouldBeTrue();
        draft.ValidateVersion(Library, Now).IsSuccess.ShouldBeTrue();
        draft.SubmitForApproval(Guid.CreateVersion7(), simulationPassed: true, "Launch", Now).IsSuccess.ShouldBeTrue();
        draft.ApproveVersion(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        draft.PublishVersion(Now).IsSuccess.ShouldBeTrue();

        var next = ProductVersion.CreateDraftVersion(Guid.CreateVersion7(), draft, Today.AddDays(60));
        (next.EligibilityRulesetCode, next.ReinstatementArrears).ShouldBe(("LTERM-ELIG", ReinstatementArrears.Required));
        next.EditConfiguration(next.Name, next.Coverages, next.EffectiveFrom, eligibilityRulesetCode: "").IsSuccess.ShouldBeTrue();
        next.EligibilityRulesetCode.ShouldBeNull();
    }

    [Fact]
    public void Validation_RefusesUnknownWrongDomainAndIncompatibleBenefits_PRD004_PRD009()
    {
        var unknown = Draft("OWN_DAMAGE", "GLASS").ValidateVersion(Library, Now);
        unknown.Error!.Code.ShouldBe(ProductErrorCodes.ValidationFailed);
        unknown.Error.Message.ShouldContain("GLASS is not in the coverage and benefit library");

        Draft("OWN_DAMAGE", "DEATH").ValidateVersion(Library, Now).Error!.Message.ShouldContain("DEATH is a Life coverage");
        Draft("THEFT").ValidateVersion(Library, Now).Error!.Message.ShouldContain("THEFT requires OWN_DAMAGE");
        Draft("OWN_DAMAGE", "TPL", "DEATH").ValidateVersion(Library, Now).Error!.Message.ShouldContain("TPL cannot be combined with DEATH");
    }

    [Fact]
    public void ValidationErrors_KeepTheVersionInDraft()
    {
        var version = Draft("GLASS");
        version.ValidateVersion(Library, Now).IsFailure.ShouldBeTrue();
        version.Status.ShouldBe(ProductVersionStatus.Draft);
        version.DomainEvents.OfType<ProductVersionValidated>().ShouldBeEmpty();
    }

    [Fact]
    public void EffectiveDate_InThePast_IsInvalid_PRD007() =>
        Draft().ValidateVersion(Library with { Today = Today.AddDays(31) }, Now).Error!.Message.ShouldContain("in the past");

    [Fact]
    public void Submission_WithoutAPassedSimulation_IsRefused_PRD005()
    {
        var version = Draft();
        version.ValidateVersion(Library, Now).IsSuccess.ShouldBeTrue();
        version.SubmitForApproval(Guid.CreateVersion7(), simulationPassed: false, "", Now).Error!.Code.ShouldBe(ProductErrorCodes.SimulationRequired);
        version.Status.ShouldBe(ProductVersionStatus.Validated);
    }

    [Fact]
    public void Submission_AsksWorkflowForApproval_PRD006()
    {
        var version = Draft();
        var maker = Guid.CreateVersion7();
        version.ValidateVersion(Library, Now).IsSuccess.ShouldBeTrue();
        version.SubmitForApproval(maker, simulationPassed: true, "Launch", Now).IsSuccess.ShouldBeTrue();
        version.DomainEvents.OfType<ProductVersionSubmitted>().Single().MakerId.ShouldBe(maker);
    }

    [Fact]
    public void PublishedVersion_IsImmutable_PRD002()
    {
        var version = Published();
        version.Status.ShouldBe(ProductVersionStatus.Published);
        version.EditConfiguration("Changed", ["OWN_DAMAGE"], Today.AddDays(60)).Error!.Code.ShouldBe(ProductErrorCodes.VersionImmutable);
        version.DomainEvents.OfType<ProductVersionPublished>().Single().EffectiveFrom.ShouldBe(Today.AddDays(30));
    }

    [Fact]
    public void ChangeToAPublishedProduct_IsANewVersion_ThatSupersedesIt_PRD003()
    {
        var published = Published();
        var next = ProductVersion.CreateDraftVersion(Guid.CreateVersion7(), published, Today.AddDays(90));
        next.Version.ShouldBe(2);
        next.Status.ShouldBe(ProductVersionStatus.Draft);
        next.SupersedesId.ShouldBe(published.Id);
        next.Coverages.ShouldBe(published.Coverages);

        next.ValidateVersion(Library with { SupersededFrom = Today.AddDays(90) }, Now).Error!.Message.ShouldContain("must be after");
        published.CloseAt(Today.AddDays(90));
        published.EffectiveTo.ShouldBe(Today.AddDays(90));
    }

    [Fact]
    public void RejectedApproval_ReturnsTheVersionToDraft()
    {
        var version = Draft();
        version.ValidateVersion(Library, Now).IsSuccess.ShouldBeTrue();
        version.SubmitForApproval(Guid.CreateVersion7(), true, "", Now).IsSuccess.ShouldBeTrue();
        version.RejectionApplied.ShouldBeFalse();
        version.RejectApproval().IsSuccess.ShouldBeTrue();
        version.Status.ShouldBe(ProductVersionStatus.Draft);
        version.RejectionApplied.ShouldBeTrue("an expired, withdrawn or repeated ending finds it back with the maker");
        version.RejectApproval().IsFailure.ShouldBeTrue("the handler parks this, unless already applied");
    }

    [Fact]
    public void EditingAValidatedVersion_ReturnsItToDraft()
    {
        var version = Draft();
        version.ValidateVersion(Library, Now).IsSuccess.ShouldBeTrue();
        version.EditConfiguration("Renamed", ["OWN_DAMAGE"], Today.AddDays(30)).IsSuccess.ShouldBeTrue();
        version.Status.ShouldBe(ProductVersionStatus.Draft);
    }

    [Fact]
    public void Revalidation_AfterALibraryChange_ReturnsAnInvalidVersionToDraft()
    {
        var version = Draft();
        version.ValidateVersion(Library, Now).IsSuccess.ShouldBeTrue();
        var withoutTheft = Library with { Coverages = Library.Coverages.Where(c => c.Key != "THEFT").ToDictionary(c => c.Key, c => c.Value) };
        version.Revalidate(withoutTheft).ShouldBeTrue();
        version.Status.ShouldBe(ProductVersionStatus.Draft);
    }

    [Fact]
    public void Version_BecomesEffective_OnItsEffectiveDate()
    {
        var version = Published();
        version.BecomeEffective(Today).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        version.BecomeEffective(Today.AddDays(30)).IsSuccess.ShouldBeTrue();
        version.Status.ShouldBe(ProductVersionStatus.Effective);
    }

    [Fact]
    public void SimulationRun_WithoutCases_DoesNotPass_AndGoesStale()
    {
        var version = Draft();
        SimulationRun.Record(Guid.CreateVersion7(), version, Guid.CreateVersion7(), [], Now).Status.ShouldBe(SimulationStatus.NoCases);
        var failed = SimulationRun.Record(Guid.CreateVersion7(), version, Guid.CreateVersion7(),
            [new SimulationCase("A", "{}", "{}", true), new SimulationCase("B", "{}", "{}", false)], Now);
        failed.Status.ShouldBe(SimulationStatus.Failed);
        failed.CasesFailed.ShouldBe(1);
        failed.MarkStale();
        failed.Status.ShouldBe(SimulationStatus.Stale);
    }
}
