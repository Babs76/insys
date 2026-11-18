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
using Insys.Assist.Application;
using Insys.Assist.Domain;
using Insys.Assist.Evaluation;
using Insys.BuildingBlocks.Application.Ai;
using Insys.Rating.Contracts;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>Batch 7.3: templates and prices as configuration (AIA-074, D-5), step numbers (AIA-033a), and the AI-2 evaluation's runner and reports.</summary>
public sealed class EvaluationTests
{
    private static readonly RecordedCalculation Motor = new(Guid.Empty, "GENERAL", "GENERAL_QUOTE", "OFT-MOTOR", 1, 1, 800m, "ZAR", DateTimeOffset.UnixEpoch, [],
    [
        new(1, "EXPOSURE", null, "sum_insured", 25_000m), new(2, "TABLE", "OFT-MOT-BASE", "G3", 750m), new(3, "FEE", "POLICY_FEE", "50", 800m), new(4, "ROUNDING", "ZAR-2DP", "0.01", 800m),
    ]);

    private static AssistOptions Options() => new()
    {
        ModelVersion = "claude-opus-5-5",
        InferenceRegions = ["US", "GLOBAL"],
        Prompts = new(StringComparer.Ordinal)
        {
            [AssistFeatures.ExplainRefusal] = new() { Version = "refusal-v1", Text = "Explain the refusal." },
            [AssistFeatures.ExplainPremium] = new() { Version = "premium-v1", Text = "Explain the premium." },
        },
        Prices = new(StringComparer.Ordinal) { ["claude-opus-5-5"] = new() { InputPerMillionTokens = 4m, OutputPerMillionTokens = 20m, Currency = "USD", Source = "A test price" } },
    };

    [Fact]
    public void AnExplanation_MayNameAStepByItsNumber_ButAStepNumberIsNeverAnAmount_AIA033a()
    {
        var source = ExplanationFacts.Premium(Motor).Values();
        var steps = ExplanationFacts.Premium(Motor).StepNumbers;
        NumberVerifier.Verify("Line 2 applies the rate for group G3, line 3 adds the fee and line 4 rounds the premium to ZAR 800.00.", source, strictCurrency: true, steps)
            .Rejected.ShouldBeEmpty();
        NumberVerifier.Verify("Lines 2 and 3 set the premium, lines 1, 2 and 3 come first, steps 2 to 4 follow and step 4 rounds it.", source, strictCurrency: true, steps)
            .Rejected.ShouldBeEmpty();
        NumberVerifier.Verify("Lines 2 and 7 set the premium.", source, strictCurrency: true, steps).Rejected.ShouldBe(["2", "7"], "a list with a step that isn't there is no step list");
        NumberVerifier.Verify("Line 2 adds ZAR 2.00.", source, strictCurrency: true, steps).Rejected.ShouldBe(["ZAR 2.00"]);
        NumberVerifier.Verify("Line 5 adds nothing.", source, strictCurrency: true, steps).Rejected.ShouldBe(["5"], "there is no fifth step");
    }

    [Theory]
    [InlineData("Line 2 multiplies the base by a factor of 3.", "3")]
    [InlineData("The plan loads the premium by 2.", "2")]
    [InlineData("Line 3 applies a loading of .50.", ".50")]
    [InlineData("Line 3 applies a loading of ,50.", ",50")]
    [InlineData("Line 3 applies a loading of '.5'.", ".5")]
    [InlineData("Line 2.5 applies the table.", "2.5")]
    public void AStepNumber_PassesOnlyRightAfterLineOrStep_AndAPointOrCommaIsNeverTrimmedOffAFigure_AIA033a(string output, string rejected) =>
        NumberVerifier.Verify(output, ExplanationFacts.Premium(Motor).Values(), strictCurrency: true, ExplanationFacts.Premium(Motor).StepNumbers).Rejected.ShouldContain(rejected);

    [Fact]
    public void SparseLineNumbers_AreTheOnlyStepsThatPass()
    {
        var sparse = Motor with { Lines = [new(10, "TABLE", "OFT-MOT-BASE", "G3", 750m), new(20, "FEE", "POLICY_FEE", "50", 800m)] };
        var facts = ExplanationFacts.Premium(sparse);
        NumberVerifier.Verify("Line 10 applies the table and line 20 adds the fee.", facts.Values(), strictCurrency: true, facts.StepNumbers).Rejected.ShouldBeEmpty();
        NumberVerifier.Verify("Line 15 adds nothing.", facts.Values(), strictCurrency: true, facts.StepNumbers).Rejected.ShouldBe(["15"]);
    }

    [Fact]
    public async Task ACallThatEndedUnavailableButWasBilled_IsStillMeteredAndCosted_AIA073_AIA075()
    {
        var exchange = await AssistExchange.Send(new Refusing(), "claude-opus-5-5", "premium-v1", "Explain.", ExplanationFacts.Premium(Motor), CancellationToken.None);
        (exchange.Status, exchange.InputTokens, exchange.OutputTokens).ShouldBe((AssistRunStatuses.Unavailable, 120L, 8192L));
        Options().CostOf("claude-opus-5-5", exchange.InputTokens, exchange.OutputTokens).ShouldBe((0.164320m, "USD"));
    }

    [Fact]
    public void ARefusalHasNoSteps_SoNoBareNumberPassesOnIt()
    {
        var facts = ExplanationFacts.Refusal(new("CURRENCY_MISMATCH", 422, "x", false, "", "The currencies differ.", "Use one currency."), 0);
        NumberVerifier.Verify("Step 2 is to use one currency.", facts.Values(), strictCurrency: true, facts.StepNumbers).Rejected.ShouldBe(["2"]);
    }

    [Fact]
    public void ARunsCost_IsAtTheConfiguredListPrice_AndAModelWithNoneHasNoCost_D5()
    {
        var options = Options();
        options.CostOf("claude-opus-5-5", 1_000_000, 500_000).ShouldBe((14.000000m, "USD"));
        options.CostOf("claude-opus-5-5", 120, 30).ShouldBe((0.001080m, "USD"), "4 x 120 + 20 x 30, per million");
        options.CostOf("another-model", 120, 30).ShouldBeNull();
    }

    [Fact]
    public void ATemplateWithoutAVersionOrText_IsNotATemplate_AIA074()
    {
        var options = Options();
        options.PromptFor(AssistFeatures.ExplainPremium)!.Version.ShouldBe("premium-v1");
        options.Prompts[AssistFeatures.ExplainPremium].Version = "";
        options.PromptFor(AssistFeatures.ExplainPremium).ShouldBeNull();
        options.PromptFor("UNKNOWN").ShouldBeNull();
    }

    [Fact]
    public void TheCommittedSet_HasAbout100Cases_OfBothKinds_AndCarriesNoInputsAsGiven_D15()
    {
        var set = EvaluationSet.Read(Path.Combine(Root(), "docs", "evaluation", "ai2-evaluation-set.json"));
        set.Count.ShouldBeGreaterThanOrEqualTo(100);
        set.Refusals.ShouldNotBeEmpty();
        set.Calculations.Select(c => c.SourceDomain).Distinct().Order().ToList().ShouldBe(["GENERAL", "HEALTH", "LIFE"]);
        foreach (var calculation in set.Calculations.Where(c => c.SourceDomain == "HEALTH"))
        {
            ExplanationFacts.Premium(calculation.Calculation).Render().ShouldNotContain("factor value", customMessage: "no clinical detail (AIA-038)");
        }
    }

    [Fact]
    public async Task TheRunner_SendsEachCaseThroughTheExchange_AndCountsWhatTheVerifierRejects_AndWhatWasUnavailable()
    {
        var set = new EvaluationSet("test",
            [new("R-001", "CURRENCY_MISMATCH", false, "The currencies differ.", "Use one currency.", 0), new("R-002", "NOT_FOUND", false, "It isn't there.", null, 0)],
            [new("C-001", Motor.SourceDomain, Motor.ProductCode, 1, 1, Motor.Premium, Motor.Currency, Motor.Lines)]);
        var provider = new Scripted(request => request.Data.Contains("NOT_FOUND", StringComparison.Ordinal) ? null
            : request.PromptVersion == "premium-v1" ? "The premium is ZAR 900.00." : "Use one currency.");
        var results = await new EvaluationRunner(provider, Options()).Run(set, CancellationToken.None);

        results.Select(r => (r.Id, r.Status)).ShouldBe([("R-001", AssistRunStatuses.Verified), ("R-002", AssistRunStatuses.Unavailable), ("C-001", AssistRunStatuses.Rejected)]);
        results[2].RejectedNumbers.ShouldBe(["ZAR 900.00"]);
        results[0].Cost.ShouldBe(0.001080m);
        results[0].InferenceRegion.ShouldBe("global");
        provider.Sent.ShouldAllBe(r => r.InferenceGeo == "global", "a GLOBAL tenant's region, asked on every call");
        provider.Sent.ShouldAllBe(r => r.ModelVersion == "claude-opus-5-5");
        provider.Sent.Select(r => r.Instructions).Distinct().Order().ToList().ShouldBe(["Explain the premium.", "Explain the refusal."], customMessage: "the configured templates");

        var summary = EvaluationReport.Summary(results, Options(), "scripted", DateTimeOffset.UnixEpoch);
        summary.ShouldContain("| All | 3 | 1 | 1 | 0 | 1 |");
        summary.ShouldContain("`global` (1), `not reported` (1), `us` (1)");
        summary.ShouldContain("USD 0.001080");
        var sheet = EvaluationReport.ReviewSheet(results).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        sheet.Length.ShouldBe(4, "a header and a row per case");
        sheet[1].ShouldStartWith("\"R-001\",\"REFUSAL\",\"VERIFIED\"");
    }

    [Fact]
    public void TheComparison_GivesEachModelsVerifiedRate_Rejections_Times_AndCostPerExplanation()
    {
        CaseResult Case(string id, string status, double seconds, decimal? cost, params string[] rejected) =>
            new(id, ExplanationKinds.Premium, status, null, rejected, 100, 50, seconds, cost, "m", "us", "");
        var opus = (new RunInfo("claude", "claude-opus-5-5", "medium", "us", "USD", DateTimeOffset.UnixEpoch), (IReadOnlyList<CaseResult>)
            [Case("C-001", AssistRunStatuses.Verified, 4, 0.002m), Case("C-002", AssistRunStatuses.Rejected, 12, 0.002m, "ZAR 900.00"), Case("C-003", AssistRunStatuses.Unavailable, 15, 0.001m)]);
        var haiku = (new RunInfo("claude", "claude-haiku-5-5", "medium", "us", "USD", DateTimeOffset.UnixEpoch), (IReadOnlyList<CaseResult>)
            [Case("C-001", AssistRunStatuses.Verified, 1, 0.0001m), Case("C-002", AssistRunStatuses.Verified, 2, 0.0001m), Case("C-003", AssistRunStatuses.Verified, 3, 0.0001m)]);
        var table = EvaluationReport.Comparison([opus, haiku]);

        table.ShouldContain("| `claude-haiku-5-5` | medium | us | `us` (3) | 3 | 3 (100.0%) | 0 (0.0%) | 0 (0.0%) | 0 (0.0%) | 2.00 | 3.00 | 3.00 | 0 | USD 0.000100 | USD 0.0003 |");
        table.ShouldContain("| `claude-opus-5-5` | medium | us | `us` (3) | 3 | 1 (33.3%) | 1 (33.3%) | 0 (0.0%) | 1 (33.3%) | 4.00 | 12.00 | 12.00 | 1 | USD 0.002500 | USD 0.0050 |",
            customMessage: "times are of the answered calls; the cost of every call, the unavailable one included, over the two explanations given");
        table.ShouldContain("- **`claude-opus-5-5`:** C-002 (ZAR 900.00).");
        table.IndexOf("claude-haiku-5-5", StringComparison.Ordinal).ShouldBeLessThan(table.IndexOf("claude-opus-5-5", StringComparison.Ordinal));
    }

    // Run 43862166993, claude-opus-5-5, copied from its results.json (sample data; Ade, 16 November). Each is sent through the exchange a run uses, from the committed set's R-ASSIST_PACK_CHANGED and C-OFT-MOTOR-G3-25000-NONE (R-005 and C-001 in that run).
    private const string R005Facts = "code: ASSIST_PACK_CHANGED\nworth retrying: no\nwhat it means: The approval was for different content from the version as it now stands.\nwhat to do next: Read the version again, check its review sheet, and approve the content it now has.";
    private const string R005Text = "INSYS refused your approval because the content you approved is no longer the content in this version. The version has changed since you reviewed it, so your approval no longer matches what it contains. INSYS can't accept it as it stands.\n\nTrying the same approval again won't help, because it would still be for the old content.\n\n**What to do next:**\n1. Open the version again and read it as it now stands.\n2. Check its review sheet so you know what has changed.\n3. If you're satisfied, approve the content the version has now.";
    private const string C001Facts = "product: OFT-MOTOR\nproduct version: 1\nrating version: 1\nsource: GENERAL\nline 1: EXPOSURE\nline 1 factor value: sum_insured\nline 1 amount: ZAR 25000.00\nline 2: TABLE\nline 2 factor: OFT-MOT-BASE\nline 2 factor value: G3\nline 2 amount: ZAR 750.00\nline 3: FEE\nline 3 factor: POLICY_FEE\nline 3 factor value: 50\nline 3 amount: ZAR 800.00\nline 4: ROUNDING\nline 4 factor: ZAR-2DP\nline 4 factor value: 0.01\nline 4 amount: ZAR 800.00\npremium: ZAR 800.00";
    private const string C001Text = "INSYS calculated this premium for product OFT-MOTOR (product version 1, rating version 1, source GENERAL) in four lines:\n\n1. **Exposure:** The calculation started from the sum insured of ZAR 25000.00.\n2. **Table:** INSYS looked up the base rate in table OFT-MOT-BASE using the value G3. This gave an amount of ZAR 750.00.\n3. **Fee:** INSYS applied the POLICY_FEE factor with a value of 50. After this line, the amount stood at ZAR 800.00.\n4. **Rounding:** INSYS applied the ZAR-2DP rounding rule with a value of 0.01. The amount stayed at ZAR 800.00.\n\nThe recorded premium is **ZAR 800.00**.";


    private static async Task<AssistExchangeResult> Answered(string caseId, string answer)
    {
        var set = EvaluationSet.Read(Path.Combine(Root(), "docs", "evaluation", "ai2-evaluation-set.json"));
        var facts = set.Refusals.FirstOrDefault(r => r.Id == caseId) is { } refusal
            ? ExplanationFacts.Refusal(refusal.Entry, refusal.FieldsInError)
            : ExplanationFacts.Premium(set.Calculations.Single(c => c.Id == caseId).Calculation);
        return await AssistExchange.Send(new Scripted(_ => answer), "claude-opus-5-5", "v", "Explain.", facts, CancellationToken.None);
    }

    /// <summary>AIA-033a, from the first evaluation run: R-ASSIST_PACK_CHANGED's answer, a numbered list of what to do next, verifies; a figure added to a listed line is still rejected.</summary>
    [Fact]
    public async Task R005sAnswer_Verifies_AndAFigureAddedToAListedLine_IsRejected_AIA033a()
    {
        var asGiven = await Answered("R-ASSIST_PACK_CHANGED", R005Text);
        asGiven.Sent.Text.ShouldBe(R005Facts, "the facts the run sent");
        asGiven.Rejected.ShouldBeEmpty();
        (await Answered("R-ASSIST_PACK_CHANGED", R005Text.Replace("1. Open", "1. Open within 5 days", StringComparison.Ordinal))).Rejected.ShouldBe(["5"]);
    }

    /// <summary>
    /// AIA-033a and P3, from the first evaluation run: C-001's numbered lines aren't refused, but "four" still is (a count is computed, so number words stay rejected);
    /// without it the answer verifies, and a wrong amount on a listed line is rejected.
    /// </summary>
    [Fact]
    public async Task C001sAnswer_IsRejectedOnlyForItsNumberWord_VerifiesWithoutIt_AndAWrongAmountIsRejected_AIA033a_P3()
    {
        var asGiven = await Answered("C-OFT-MOTOR-G3-25000-NONE", C001Text);
        asGiven.Sent.Text.ShouldBe(C001Facts, "the facts the run sent");
        asGiven.Rejected.ShouldBe(["four"]);
        (await Answered("C-OFT-MOTOR-G3-25000-NONE", C001Text.Replace(" in four lines", "", StringComparison.Ordinal))).Rejected.ShouldBeEmpty();
        (await Answered("C-OFT-MOTOR-G3-25000-NONE", C001Text.Replace(" in four lines", "", StringComparison.Ordinal).Replace("ZAR 750.00", "ZAR 760.00", StringComparison.Ordinal))).Rejected
            .ShouldHaveSingleItem().ShouldContain("760.00");
    }

    /// <summary>Ade, 16 November: the job's log carries each rejected case, what it explains, the facts sent, the answer and why each number was refused.</summary>
    [Fact]
    public void TheLog_CarriesEachRejectedCase_WithItsFacts_Answer_AndWhyEachNumberWasRefused()
    {
        var facts = ExplanationFacts.Premium(Motor).Values();
        var rejected = new CaseResult("C-002", ExplanationKinds.Premium, AssistRunStatuses.Rejected, "The premium is about ZAR 900.", ["ZAR 900"], 100, 50, 3.5, 0.002m, "m", "us",
            Minimiser.Minimise(facts).Text);
        var verified = rejected with { Id = "C-001", Status = AssistRunStatuses.Verified, RejectedNumbers = [] };
        var log = EvaluationReport.Log([verified, rejected], "# summary\n");

        log.ShouldStartWith("# summary");
        log.ShouldContain("## Rejected cases (1)");
        log.ShouldContain("### C-002 (PREMIUM)");
        log.ShouldContain("  - `ZAR 900`: a figure not in the facts");
        log.ShouldContain("    The premium is about ZAR 900.");
        log.ShouldContain("OFT-MOTOR", customMessage: "the facts sent, so the calculation is named");
        log.ShouldNotContain("### C-001", customMessage: "only rejected cases are listed");
    }

    [Theory]
    [InlineData("twice", "a number word")]
    [InlineData("G7", "a code or reference")]
    [InlineData("ZAR 1,234.56", "a figure not in the facts")]
    [InlineData("USD 800.00", "the digits are in the facts, but written differently")]
    [InlineData("80%", "a percentage not in the facts")]
    public void WhyANumberWasRefused_IsReadAgainstTheFactsSent(string number, string reason) =>
        EvaluationReport.WhyRejected(number, "premium: ZAR 800.00\nline 1: base rate ZAR 800.00").ShouldStartWith(reason);

    [Fact]
    public async Task ARunForAUsTenant_AsksForTheUs_AndAnAnswerReportedFromElsewhere_IsAResidencyMismatch_AIA050()
    {
        var set = new EvaluationSet("test", [new("R-001", "CURRENCY_MISMATCH", false, "The currencies differ.", "Use one currency.", 0)],
            [new("C-001", Motor.SourceDomain, Motor.ProductCode, 1, 1, Motor.Premium, Motor.Currency, Motor.Lines)]);
        var provider = new Scripted(request => request.PromptVersion == "premium-v1" ? "The premium is ZAR 800.00." : "Use one currency.");
        var results = await new EvaluationRunner(provider, Options(), "US").Run(set, CancellationToken.None);
        provider.Sent.ShouldAllBe(r => r.InferenceGeo == "us");
        results.Select(r => (r.Id, r.Status)).ShouldBe([("R-001", AssistRunStatuses.ResidencyMismatch), ("C-001", AssistRunStatuses.Verified)],
            "the refusal's answer was reported as global, the premium's as us");
    }

    [Fact]
    public async Task TheWholeSet_WithAStubReportingTheUs_VerifiesInTheComparison_NotAResidencyMismatch()
    {
        var set = EvaluationSet.Read(Path.Combine(Root(), "docs", "evaluation", "ai2-evaluation-set.json"));
        var results = await new EvaluationRunner(new EchoReportingAsked(), Options(), "US").Run(set, CancellationToken.None);
        results.ShouldAllBe(r => r.Status == AssistRunStatuses.Verified && r.InferenceRegion == "us");
        var table = EvaluationReport.Comparison([(new RunInfo("stub", "claude-opus-5-5", "medium", "us", "USD", DateTimeOffset.UnixEpoch), results)]);
        table.ShouldContain($"| `claude-opus-5-5` | medium | us | `us` ({set.Count}) | {set.Count} | {set.Count} (100.0%) | 0 (0.0%) | 0 (0.0%) | 0 (0.0%) |");
    }

    [Fact]
    public void ThePercentile_IsTheNearestRank() =>
        (EvaluationReport.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95), EvaluationReport.Percentile([1, 2, 3], 50), EvaluationReport.Percentile([], 95)).ShouldBe((10, 2, null));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Insys.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository's root wasn't found.");
    }

    /// <summary>Answers as told; a null answer is the provider failing.</summary>
    private sealed class Scripted(Func<AiModelRequest, string?> answer) : IAiModelProvider
    {
        public List<AiModelRequest> Sent { get; } = [];

        public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return answer(request) is { } text
                ? Task.FromResult(new AiModelResponse(text, 120, 30, "claude-opus-5-5", request.PromptVersion == "premium-v1" ? "us" : "global"))
                : throw new AiModelUnavailableException("Down.");
        }
    }

    /// <summary>A provider that was billed for a call it couldn't finish: an answer cut short at the token limit.</summary>
    private sealed class Refusing : IAiModelProvider
    {
        public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken) =>
            throw new AiModelUnavailableException("Cut short.", 120, 8192);
    }

    /// <summary>Answers with the facts it was sent, as the evaluation's echo does, and reports inference where it was asked to run, as Claude's API does.</summary>
    private sealed class EchoReportingAsked : IAiModelProvider
    {
        public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken)
        {
            var text = request.Data.Replace(PromptData.Open, "", StringComparison.Ordinal).Replace(PromptData.Close, "", StringComparison.Ordinal).Trim();
            return Task.FromResult(new AiModelResponse(text, 200, 80, "claude-opus-5-5", request.InferenceGeo));
        }
    }
}
