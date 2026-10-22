using Insys.BuildingBlocks.Domain;
using Insys.Compliance.Domain;
using Insys.Testing;

namespace Insys.Compliance.Tests;

public sealed class ComplianceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private const string Values = """{"grossPremium":1000}""";

    [Fact]
    public void Lifecycle_MatchesDomainModel()
    {
        var spec = DomainModelSpec.Transitions("compliance", "RegulatoryReturn")
            .Select(t => t.Replace("Regulator accepts", "RegulatorAccepts", StringComparison.Ordinal).Replace("Regulator rejects", "RegulatorRejects", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal);
        RegulatoryReturn.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ShouldBe(spec);
    }

    [Fact]
    public void Return_IsApprovedByAComplianceOfficer_OtherThanItsPreparerAndReviewer_BeforeSubmission()
    {
        var preparer = Guid.CreateVersion7();
        var reviewer = Guid.CreateVersion7();
        var officer = Guid.CreateVersion7();
        var regulatoryReturn = RegulatoryReturn.PrepareReturn(Guid.CreateVersion7(), "QRT-01", "2026-09", Values, preparer);

        regulatoryReturn.SubmitReturn("REG", "R-1", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        regulatoryReturn.ReviewReturn(preparer).Error!.Code.ShouldBe(ComplianceErrorCodes.SamePerson);
        regulatoryReturn.ReviewReturn(reviewer).IsSuccess.ShouldBeTrue();
        regulatoryReturn.ApproveReturn(reviewer).Error!.Code.ShouldBe(ComplianceErrorCodes.SamePerson);
        regulatoryReturn.ApproveReturn(officer).IsSuccess.ShouldBeTrue();
        regulatoryReturn.SubmitReturn("REG", "R-1", Now).IsSuccess.ShouldBeTrue();
        regulatoryReturn.DomainEvents.OfType<RegulatoryReturnSubmitted>().ShouldHaveSingleItem().RegulatorRef.ShouldBe("R-1");

        // Rejected, corrected back to Prepared, and reviewed and approved again.
        regulatoryReturn.RecordResponse(false, "Totals do not reconcile").IsSuccess.ShouldBeTrue();
        regulatoryReturn.Correct(Values, reviewer).IsSuccess.ShouldBeTrue();
        (regulatoryReturn.Status, regulatoryReturn.PreparedBy, regulatoryReturn.ReviewedBy, regulatoryReturn.ApprovedBy)
            .ShouldBe((ReturnStatus.Prepared, (Guid?)reviewer, (Guid?)null, (Guid?)null));
    }

    [Fact]
    public void Frequencies_EndWithTheirPeriods_AndCoverTheirMonths()
    {
        (ReturnFrequencies.EndsWith("MONTHLY", "2026-10"), ReturnFrequencies.EndsWith("QUARTERLY", "2026-10"), ReturnFrequencies.EndsWith("QUARTERLY", "2026-09"),
            ReturnFrequencies.EndsWith("ANNUAL", "2026-12")).ShouldBe((true, false, true, true));
        ReturnFrequencies.Span("QUARTERLY", "2026-09").ShouldBe((new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)));
        ReturnFrequencies.Span("ANNUAL", "2026-12").ShouldBe((new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        ReturnFrequencies.Span("MONTHLY", "2026-13").ShouldBeNull();
    }
}
