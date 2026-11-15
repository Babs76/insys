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
        NumberVerifier.Verify("Line 2 applies the rate for group G3, line 3 adds the fee and line 4 rounds the premium to ZAR 800.00.", source, strictCurrency: true)
            .Rejected.ShouldBeEmpty();
        NumberVerifier.Verify("Line 2 adds ZAR 2.00.", source, strictCurrency: true).Rejected.ShouldBe(["ZAR 2.00"]);
        NumberVerifier.Verify("Line 5 adds nothing.", source, strictCurrency: true).Rejected.ShouldBe(["5"], "there is no fifth step");
    }

    [Fact]
    public void ARefusalHasNoSteps_SoNoBareNumberPassesOnIt()
    {
        var facts = ExplanationFacts.Refusal(new("CURRENCY_MISMATCH", 422, "x", false, "", "The currencies differ.", "Use one currency."), 0);
        NumberVerifier.Verify("Step 2 is to use one currency.", facts.Values(), strictCurrency: true).Rejected.ShouldBe(["2"]);
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
        provider.Sent.ShouldAllBe(r => r.ModelVersion == "claude-opus-5-5");
        provider.Sent.Select(r => r.Instructions).Distinct().Order().ToList().ShouldBe(["Explain the premium.", "Explain the refusal."], customMessage: "the configured templates");

        var summary = EvaluationReport.Summary(results, Options(), "scripted", DateTimeOffset.UnixEpoch);
        summary.ShouldContain("| All | 3 | 1 | 1 | 1 |");
        summary.ShouldContain("`global` (1)");
        summary.ShouldContain("`not reported` (2)", customMessage: "the premium's answer said nothing, and the unavailable call gave nothing");
        summary.ShouldContain("USD 0.001080");
        var sheet = EvaluationReport.ReviewSheet(results).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        sheet.Length.ShouldBe(4, "a header and a row per case");
        sheet[1].ShouldStartWith("\"R-001\",\"REFUSAL\",\"VERIFIED\"");
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
                ? Task.FromResult(new AiModelResponse(text, 120, 30, "claude-opus-5-5", request.PromptVersion == "premium-v1" ? null : "global"))
                : throw new AiModelUnavailableException("Down.");
        }
    }
}
