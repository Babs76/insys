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
using Insys.Assist.Infrastructure;
using Insys.Assist.Infrastructure.Extraction;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>
/// Batch 7.8c: the AI-3 evaluation set (docs/evaluation/ai3-evaluation-set.json) and its runner. Every §5A.4 measure waits for the tenant sample documents; the
/// one invented case proves the runner and counts toward no target.
/// </summary>
public sealed class SetupEvaluationTests
{
    private static SetupEvaluationSet Set => SetupEvaluationSet.Read(Path.Combine(RepositoryPaths.Root, "docs", "evaluation", "ai3-evaluation-set.json"));

    [Fact]
    public void EveryMeasureOfSrs5A4_AndOq189sFigure_IsWaitingForTheTenantSampleDocuments_AndNoCaseYetCounts()
    {
        var set = Set;
        set.Feature.ShouldBe("TENANT_SETUP");
        set.Status.ShouldBe("WAITING");
        set.Measures.Select(m => m.Measure).ShouldBe(
        [
            "REGULATED_VALUES_NOT_FROM_APPROVED_OVERLAY", "MAPPED_VALUES_WITH_VALID_CITATION", "MAPPED_VALUES_CORRECT",
            "READINESS_BLOCKERS_LEFT_FOR_A_STANDARD_QUESTIONNAIRE", "DRAFTS_APPLIED_WITHOUT_A_SECOND_PERSONS_APPROVAL", "QUESTIONNAIRE_TO_COMPLETE_DRAFT_READY_FOR_APPROVAL",
            "ACCOUNTS_UNMAPPED_BY_PRIVACY_MASK",
        ], "the six measures of SRS §5A.4, and OQ-189's own figure");
        set.Measures.ShouldAllBe(m => m.Status == "WAITING" && m.WaitingFor != null);
        set.Cases.ShouldAllBe(c => !c.CountsTowardTargets, "only a tenant's real sample documents count");
    }

    [Fact]
    public async Task TheRunner_ScoresACaseAgainstTheExpertsRows_AndAnInventedCaseLeavesEveryMeasureWaiting()
    {
        var set = Set;
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Testing" };
        var options = new AssistOptions { ModelVersion = "development" };
        options.Prompts["TENANT_SETUP"] = new AssistPromptTemplate { Version = "tenant-setup-v1", Text = "instructions" };

        var results = await new SetupEvaluationRunner(new DevelopmentAiModelProvider(environment), options, new DraftDocumentExtractor(), set.RoleTemplates)
            .Run(set, RepositoryPaths.Root, CancellationToken.None);

        var sample = results.ShouldHaveSingleItem();
        sample.Status.ShouldBe("VERIFIED");
        (sample.Expected, sample.MappedCorrect, sample.MappedWrong, sample.RegulatedNotFromOverlay).ShouldBe((6, 4, 0, 0));
        sample.Missed.ShouldBe(["workflow.cfg_delegation_and_approval_authority:AUTH-PREMIUM_REFUND-FINANCE_MANAGER", "iam.cfg_roles_and_permission_sets:COMPLIANCE_OFFICER"],
            "the stand-in's wrong figure is dropped, and it leaves a title unmapped");
        SetupEvaluationRunner.Measures(set, results).ShouldAllBe(m => m.Status == "WAITING");
    }

    /// <summary>OQ-189 (decided (a), Ade 19 November 2026): an account the minimiser masked is counted on its own, so the decision can be revisited.</summary>
    [Theory]
    [InlineData("GL-4100", 1)]
    [InlineData("GL 4100", 0)]
    public async Task AnAccountLeftUnmappedBecauseThePrivacyFilterMaskedIt_IsCountedOnItsOwn_OQ189(string glCode, int masked)
    {
        var root = Directory.CreateTempSubdirectory("ai3-eval-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "chart.pdf"), SampleDocuments.Pdf(["Chart of accounts", $"4100\tPremium income\tINCOME\t{glCode}"]));
            var expected = System.Text.Json.JsonDocument.Parse($$"""{"account_code":"4100","name":"Premium income","type":"INCOME","gl_account":"{{glCode}}"}""").RootElement;
            var set = Set with
            {
                Cases = [new SetupEvaluationCase("OQ189", "chart.pdf", [], [new ExpectedMapping(SetupMappingTargets.ChartOfAccounts, "ACC-4100", expected)], [], CountsTowardTargets: true)],
            };
            var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Testing" };
            var options = new AssistOptions { ModelVersion = "development" };
            options.Prompts["TENANT_SETUP"] = new AssistPromptTemplate { Version = "tenant-setup-v1", Text = "instructions" };

            // The stand-in maps nothing from a document that isn't its sample, so the account is missed either way; only the mask decides the figure.
            var results = await new SetupEvaluationRunner(new DevelopmentAiModelProvider(environment), options, new DraftDocumentExtractor(), set.RoleTemplates)
                .Run(set, root, CancellationToken.None);

            results.ShouldHaveSingleItem().AccountsMaskedUnmapped.ShouldBe(masked);
            SetupEvaluationRunner.Measures(set, results).Single(m => m.Measure == "ACCOUNTS_UNMAPPED_BY_PRIVACY_MASK").Status
                .ShouldBe(masked.ToString(System.Globalization.CultureInfo.InvariantCulture), "a case that counts reports the figure");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
