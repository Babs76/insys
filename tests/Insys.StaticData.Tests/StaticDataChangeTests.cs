using Insys.BuildingBlocks.Domain;
using Insys.StaticData.Domain.Changes;
using Insys.Testing;

namespace Insys.StaticData.Tests;

public sealed class StaticDataChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Maker = Guid.CreateVersion7();

    private static StaticDataChange Submitted(bool requiresChecker = true) =>
        StaticDataChange.SubmitChange(Guid.CreateVersion7(), "ref_currencies", "ZAR@1", "CREATE", "{}", Maker, new DateOnly(2026, 11, 1), "New", requiresChecker, "STATIC_DATA_T1", Now);

    [Fact]
    public void StaticDataChange_Lifecycle_MatchesDomainModel()
    {
        // The model's Draft state is the draft row in the owning module, and "Effective date reached" is the activation event.
        var specified = DomainModelSpec.Transitions("sdm", "StaticDataChange")
            .Where(t => !t.StartsWith("Draft-", StringComparison.Ordinal))
            .Select(t => t.Replace("Effective date reached", "ActivateVersion", StringComparison.Ordinal))
            .ToList();
        var implemented = StaticDataChange.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList();

        implemented.ShouldBe(specified.OrderBy(t => t, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Change_ApprovedByItsMaker_IsForbidden_SDM003() =>
        Submitted().ApproveChange(Maker, requiresDifferentChecker: true, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);

    [Fact]
    public void Change_ApprovedByAnotherUser_IsApproved_AndRaisesTheDecision_SDM003()
    {
        var change = Submitted();
        var checker = Guid.CreateVersion7();

        change.ApproveChange(checker, requiresDifferentChecker: true, Now).IsSuccess.ShouldBeTrue();

        change.Status.ShouldBe(ChangeStatus.Approved);
        change.CheckerId.ShouldBe(checker);
        change.DomainEvents.OfType<StaticDataChangeApproved>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Change_NeedingNoChecker_IsApprovedOnSubmission()
    {
        var change = Submitted(requiresChecker: false);

        change.Status.ShouldBe(ChangeStatus.Approved);
        change.CheckerId.ShouldBeNull();
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(2, false, false)]
    [InlineData(3, true, false)]
    public void MakerChecker_Tier1AlwaysOn_Tier2PerTenant_Tier3Off_SDM003(int tier, bool tier2Setting, bool expected) =>
        MakerCheckerPolicy.RequiresChecker(tier, tier2Setting).ShouldBe(expected);

    [Fact]
    public void RejectedChange_CannotBeApproved_IsInvalidStateTransition()
    {
        var change = Submitted();
        change.RejectChange(Guid.CreateVersion7(), "No", Now).IsSuccess.ShouldBeTrue();

        change.ApproveChange(Guid.CreateVersion7(), requiresDifferentChecker: true, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void Settings_DefaultToTier2On_AndReportWhetherTheyChanged()
    {
        var settings = TenantGovernanceSettings.Defaults(Guid.CreateVersion7());
        settings.Tier2MakerChecker.ShouldBeTrue();

        settings.ChangeTier2MakerChecker(true).ShouldBeFalse();
        settings.ChangeTier2MakerChecker(false).ShouldBeTrue();
        settings.Tier2MakerChecker.ShouldBeFalse();
    }
}
