/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist.Tests
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
using System.Text.Json;
using Insys.Assist.Application;
using Insys.Assist.Domain;
using Insys.ProductFactory.Contracts;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>Wave 7, batch 7.5: AI-1's review rules (AIA-018 to AIA-020) and its evaluation's format and scoring (SRS §4.3).</summary>
public sealed class DraftReviewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 19);
    private static readonly ProductDraftTarget Target = new("P", "LIFE", 1, "Funeral", ["FUNERAL"], ["FUNERAL", "ACCIDENTAL_DEATH"], "Issue", "None");

    private static DraftItem Kept(string kind = DraftItemKinds.Value, string field = "version.name", string flag = DraftItemFlags.Found) =>
        DraftItem.Proposed(Guid.NewGuid(), kind, field, "\"Sample\"", flag, Guid.NewGuid(), 1, "Sample quote here", null, true);

    [Fact]
    public void ADroppedProposal_IsNeverDecided_AndAMissingValue_IsOnlyFilledByAnEdit_AIA019()
    {
        var dropped = DraftItem.Proposed(Guid.NewGuid(), DraftItemKinds.Value, "version.name", "\"X\"", DraftItemFlags.Found, null, null, null, "NO_CITATION", false);
        dropped.Decide(DraftItemDecisions.Accepted, null, null, "u", At).Error!.Code.ShouldBe("INVALID_STATE_TRANSITION");

        var missing = DraftItem.Missing(Guid.NewGuid(), "version.coverages");
        missing.Decide(DraftItemDecisions.Accepted, null, null, "u", At).IsFailure.ShouldBeTrue();
        missing.Decide(DraftItemDecisions.Rejected, null, null, "u", At).IsFailure.ShouldBeTrue();
        missing.Decide(DraftItemDecisions.Edited, "[\"FUNERAL\"]", "From the brochure", "u", At).IsSuccess.ShouldBeTrue();
        missing.EditedValue.ShouldBe("[\"FUNERAL\"]");
    }

    [Fact]
    public void AnEdit_GivesItsValueAndWhy_KeptBesideTheProposal_AndOnlyAnEditGivesAValue_AIA019()
    {
        var item = Kept();
        item.Decide(DraftItemDecisions.Edited, "\"Plus\"", null, "u", At).IsFailure.ShouldBeTrue("an edit says why");
        item.Decide(DraftItemDecisions.Edited, null, "why", "u", At).IsFailure.ShouldBeTrue("an edit gives the value");
        item.Decide(DraftItemDecisions.Accepted, "\"Plus\"", null, "u", At).IsFailure.ShouldBeTrue("only an edit gives a value");
        item.Decide("MAYBE", null, null, "u", At).IsFailure.ShouldBeTrue();

        item.Decide(DraftItemDecisions.Edited, "\"Plus\"", "The brochure's full name", "u", At).IsSuccess.ShouldBeTrue();
        (item.Value, item.EditedValue, item.Decision, item.DecidedBy).ShouldBe(("\"Sample\"", "\"Plus\"", "EDITED", "u"), "the AI's proposal is never changed");
        DraftReviewRules.Check(item, DraftItemDecisions.Rejected, null, null).IsSuccess.ShouldBeTrue("a decision can be changed");
    }

    [Theory]
    [InlineData("version.name", "\"Sample Funeral Plan\"", true)]
    [InlineData("version.name", "\"\"", false)]
    [InlineData("version.coverages", "[\"FUNERAL\", \"ACCIDENTAL_DEATH\"]", true)]
    [InlineData("version.coverages", "[\"NOT_A_CODE\"]", false)]
    [InlineData("version.coverages", "[\"FUNERAL\", \"FUNERAL\"]", false)]
    [InlineData("version.coverages", "[]", false)]
    [InlineData("version.coverStartsOn", "\"FirstPayment\"", true)]
    [InlineData("version.coverStartsOn", "\"Tomorrow\"", false)]
    [InlineData("version.reinstatementArrears", "\"Required\"", true)]
    [InlineData("version.effectiveFrom", "\"2026-11-19\"", true)]
    [InlineData("version.effectiveFrom", "\"2026-11-18\"", false)]
    [InlineData("version.effectiveFrom", "\"19/11/2026\"", false)]
    public void APersonsValue_ForAVersionField_IsCheckedAsTheFieldAllows_AIA012_AIA019(string field, string json, bool allowed) =>
        DraftReviewRules.CheckVersionValue(field, json, Target, Today).IsSuccess.ShouldBe(allowed);

    [Theory]
    [InlineData(DraftItemKinds.RateTable, "[[\"Age band\", \"Premium\"], [\"18-40\", \"45.00\"]]", true)]
    [InlineData(DraftItemKinds.RateTable, "{\"rows\": 1}", false)]
    [InlineData(DraftItemKinds.Rule, "\"Cover ends at age 75\"", true)]
    [InlineData(DraftItemKinds.Rule, "{\"expression\": \"age >= 75\"}", false)]
    [InlineData(DraftItemKinds.SimulationCase, "{\"description\": \"Youngest\"}", true)]
    [InlineData(DraftItemKinds.SimulationCase, "\"youngest\"", false)]
    public void APersonsValue_ForAnythingElse_TakesItsKindsShape_AIA015_AIA016(string kind, string json, bool allowed) =>
        DraftReviewRules.CheckOtherValue(kind, json).IsSuccess.ShouldBe(allowed);

    [Fact]
    public void ARejectedVersionField_GoesBackToTheCopiedVersionsValue_ComparedAsJson_AIA019()
    {
        var copied = new ProductDraftVersion("P", 1, "Published", "Funeral", ["FUNERAL"], new DateOnly(2026, 1, 1), "Issue", "None", 3);
        DraftReviewRules.FieldOf(copied, "version.name").ShouldBe("\"Funeral\"");
        DraftReviewRules.SameJson(DraftReviewRules.FieldOf(copied, "version.coverages"), "[ \"FUNERAL\" ]").ShouldBeTrue();
        DraftReviewRules.Fields("version.coverages", "[\"FUNERAL\"]").Fields.Coverages.ShouldBe(["FUNERAL"]);
        DraftReviewRules.Fields("version.effectiveFrom", "\"2026-12-01\"").EffectiveFrom.ShouldBe(new DateOnly(2026, 12, 1));
    }

    [Theory]
    [InlineData("{\"inputs\": {\"age\": 18, \"currency\": \"ZAR\", \"frequency\": \"MONTHLY\"}, \"expected\": {\"premium\": 45.00, \"tolerance\": 0.01}}", null)]
    [InlineData("{\"inputs\": {\"age\": 18, \"currency\": \"ZAR\", \"frequency\": \"MONTHLY\"}, \"expected\": 45.00}", null)]
    [InlineData("{\"inputs\": {\"age\": 18, \"currency\": \"ZAR\", \"frequency\": \"MONTHLY\"}}", "NO_EXPECTED_PREMIUM")]
    [InlineData("{\"inputs\": {\"age\": 18}, \"expected\": 45.00}", "NO_CURRENCY_OR_FREQUENCY")]
    [InlineData("{\"inputs\": {\"age\": 18, \"currency\": \"rand\", \"frequency\": \"MONTHLY\"}, \"expected\": 45.00}", "NO_CURRENCY_OR_FREQUENCY")]
    [InlineData("[1]", "MALFORMED")]
    public void ASimulationCase_BecomesALine_OnlyWithAStatedExpectedPremium_AndItsCurrencyAndFrequency_AIA018(string json, string? reason)
    {
        var (line, why) = ProductDraftSimulationCases.Line(json, "FUN", 2, Today, Guid.NewGuid(), 1);
        why.ShouldBe(reason);
        if (reason is null)
        {
            (line!.Code, line.CaseId, line.ProductVersion, line.ExpectedPremium, line.EffectiveFrom).ShouldBe(("FUN-V2-AI-1", "AI-1", "FUN v2", 45.00m, Today));
        }
    }

    [Fact]
    public void TheAi1EvaluationSet_HoldsOnlyAnInventedCase_ForLifeFuneral_AndEveryMeasureWaits_D7()
    {
        var set = Ai1Evaluation.Read(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "evaluation", "ai1-evaluation-set.json")));
        (set.Line, set.Status).ShouldBe(("LIFE_FUNERAL", Ai1Evaluation.Waiting));
        set.Cases.ShouldAllBe(c => c.Invented, "this repository is public: D-7's documents and expected values are never committed (OQ-191)");
        var report = Ai1Evaluation.AllWaiting(set);
        report.Select(r => r.Code).ShouldBe(Ai1Evaluation.Measures.Select(m => m.Code));
        report.ShouldAllBe(r => r.Status == Ai1Evaluation.Waiting);
    }

    [Fact]
    public void TheScore_CountsFoundValuesThatMatch_RateTableCells_MissingUnflagged_AndCitations_SRS43()
    {
        var expected = new Ai1Case("C", "P", Invented: true, Injected: false, [],
            [
                new Ai1ExpectedField("version.name", JsonDocument.Parse("\"Sample Funeral Plan\"").RootElement, "FOUND", true),
                new Ai1ExpectedField("version.coverages", JsonDocument.Parse("[\"FUNERAL\"]").RootElement, "FOUND", true),
                new Ai1ExpectedField("version.coverStartsOn", null, "MISSING", true),
            ],
            [new Ai1ExpectedTable(2, [["Age band", "Premium"], ["18-40", "45.00"]])]);
        IReadOnlyList<CheckedItem> actual =
        [
            new(DraftItemKinds.Value, "version.name", "\"Sample Funeral Plan\"", DraftItemFlags.Found, Guid.NewGuid(), 1, "q", null, true),
            new(DraftItemKinds.Value, "version.coverages", "[\"ACCIDENTAL_DEATH\"]", DraftItemFlags.Found, Guid.NewGuid(), 1, "q", null, true),
            new(DraftItemKinds.RateTable, "rating.cfg_rating_tables", "[[\"Age band\", \"Premium\"], [\"18-40\", \"45.50\"]]", DraftItemFlags.Found, Guid.NewGuid(), 2, "q", null, false),
            new(DraftItemKinds.Value, "version.reinstatementArrears", "\"Required\"", DraftItemFlags.Found, null, null, null, "NO_CITATION", false),
        ];

        var score = Ai1Evaluation.Score(expected, actual);
        (score.Found, score.FoundExact, score.Cells, score.CellsRight, score.MissingUnflagged, score.Cited, score.Proposed).ShouldBe((2, 1, 4, 3, 1, 3, 4));
        var measured = Ai1Evaluation.Measure([score], bypasses: 0).ToDictionary(m => m.Code);
        (measured["FOUND_EXACT"].Value, measured["FOUND_EXACT"].Outcome).ShouldBe((0.5m, "NOT_MET"));
        (measured["RATE_TABLE_CELLS"].Value, measured["RATE_TABLE_CELLS"].Outcome).ShouldBe((0.75m, "NOT_MET"));
        measured["MISSING_UNFLAGGED"].Value.ShouldBe(1);
        measured["VALID_CITATIONS"].Value.ShouldBe(0.75m);
        measured["BYPASSES"].Outcome.ShouldBe("MET");
    }
}
