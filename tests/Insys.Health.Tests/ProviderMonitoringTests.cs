/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health.Tests
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
using Insys.Health.Application;
using Insys.Health.Domain.Providers;
using Shouldly;

namespace Insys.Health.Tests;

/// <summary>Wave 5a §4.1, §4.2 (F028, F030): judging a measure, and matching a provider's statement.</summary>
public sealed class ProviderMonitoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(19, 0.5, false, false)] // too few claims: shown, not judged
    [InlineData(20, 0.25, true, false)] // on the threshold is no breach
    [InlineData(20, 0.26, true, true)]
    public void AMeasure_IsJudgedOnlyWithEnoughClaims_AndBreachedOnlyPastItsThreshold_F028(int claims, double value, bool judged, bool breached)
    {
        var measure = new ProviderMeasure(ProviderMeasureCodes.RejectionRate, null, 0.25m, ProviderMeasure.Above, 20);
        var result = ProviderMeasureResult.Record(Guid.CreateVersion7(), "PR-1", "2026-10", measure.MeasureCode, (decimal)value, claims, measure, Now);
        (result.Judged, result.IsBreached).ShouldBe((judged, breached));
        result.DomainEvents.OfType<ProviderMeasureBreached>().Count().ShouldBe(breached ? 1 : 0);
    }

    [Fact]
    public void AMeasureWithNoThresholdInForce_IsRecordedAndNotJudged_F028()
    {
        var result = ProviderMeasureResult.Record(Guid.CreateVersion7(), "PR-1", "2026-10", ProviderMeasureCodes.AveragePaid, 100m, 50, null, Now);
        (result.Judged, result.IsBreached, result.Threshold).ShouldBe((false, false, (decimal?)null));
    }

    [Fact]
    public void AStatement_IsMatchedLineByLine_AndReconciledOnceEachExceptionIsResolved_F030()
    {
        var paid = new Dictionary<string, decimal> { ["A"] = 100m, ["B"] = 200m, ["C"] = 300m };
        var statement = ProviderStatement.Match(Guid.CreateVersion7(), "PR-1", "S-1", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "ZAR",
            [new("A", 100m), new("B", 210m), new("X", 50m)], paid, new HashSet<string> { "A", "B", "C" }).Value;
        statement.Lines.ToDictionary(l => l.Reference, l => l.MatchResult).ShouldBe(new Dictionary<string, string>
        {
            ["A"] = StatementMatch.Matched, ["B"] = StatementMatch.AmountDiffers, ["X"] = StatementMatch.NotOnOurRecords, ["C"] = StatementMatch.NotOnStatement,
        }, ignoreOrder: true);
        statement.Status.ShouldBe(StatementStatus.Exceptions);

        var matched = statement.Lines.Single(l => l.Reference == "A");
        statement.ResolveLine(matched.Id, "n/a", Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue("a matched line is no exception");
        foreach (var line in statement.Lines.Where(l => l.Open).ToList())
        {
            statement.Status.ShouldBe(StatementStatus.Exceptions);
            statement.ResolveLine(line.Id, "Agreed", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        }

        statement.Status.ShouldBe(StatementStatus.Reconciled);
    }

    [Fact]
    public void AStatementNamingAReferenceTwice_IsRefused_F030() =>
        ProviderStatement.Match(Guid.CreateVersion7(), "PR-1", "S-1", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "ZAR", [new("A", 1m), new("A", 2m)],
            new Dictionary<string, decimal>(), new HashSet<string>()).IsFailure.ShouldBeTrue();

    [Theory]
    [InlineData("amount,claim\nA,1", false)]
    [InlineData("claim_number,amount\nA,not-a-number", false)]
    [InlineData("claim_number,amount\nA", false)]
    [InlineData("claim_number,amount\r\nA,100.50\r\nB,2\r\n", true)]
    public void TheFixedCsvLayout_IsParsedOrRefusedWithItsLine_OQ142(string csv, bool ok)
    {
        var parsed = ProviderStatementCsv.Parse(csv);
        parsed.IsSuccess.ShouldBe(ok);
        if (ok)
        {
            parsed.Value.ShouldBe([new StatementLineInput("A", 100.50m), new StatementLineInput("B", 2m)]);
        }
    }
}
