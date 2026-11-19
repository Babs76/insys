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
using Insys.Assist.Infrastructure.Extraction;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>The AI-1 test document published for the front end (docs/frontend/ai1-sample-product.pdf): it extracts as the stand-in model expects.</summary>
public sealed class PublishedSampleTests
{
    private static string FilePath => Path.Combine(RepositoryPaths.Root, "docs", "frontend", "ai1-sample-product.pdf");

    [Fact]
    public async Task TheAi1TestDocument_IsPublished_AndExtractsAsTheStandInExpects_BR147()
    {
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllBytesAsync(FilePath, SampleDocuments.Ai1Sample());
        }

        File.Exists(FilePath).ShouldBeTrue("Run with INSYS_UPDATE_SNAPSHOTS=1 to publish docs/frontend/ai1-sample-product.pdf.");
        var content = await File.ReadAllBytesAsync(FilePath);
        DraftDocumentKinds.Detect(content).ShouldBe(DraftDocumentKinds.Pdf);
        var extracted = new DraftDocumentExtractor().Extract(DraftDocumentKinds.Pdf, content).ShouldNotBeNull();
        extracted.Pages[0].Text.ShouldContain(Insys.Assist.Infrastructure.DevelopmentAiModelProvider.SampleMarker);
        extracted.Pages[0].Text.ShouldContain("Product name: Sample Funeral Plan");
        extracted.Tables.ShouldContain(t => t.Page == 2 && t.Rows.Count == 4);
        IdentifierCheck.Find(extracted).ShouldBeEmpty("the test document holds no identifier");
    }

    private static string Ai3Path => Path.Combine(RepositoryPaths.Root, "docs", "frontend", "ai3-sample-tenant-documents.pdf");

    /// <summary>
    /// Batch 7.8: the AI-3 test document (docs/frontend/ai3-sample-tenant-documents.pdf) extracts and minimises as the stand-in model expects, holds no identifier,
    /// and the stand-in's fixed answer passes INSYS's checks except where it is meant not to: a wrong figure dropped, one title it couldn't map (BR-147, AIA-083).
    /// </summary>
    [Fact]
    public async Task TheAi3TestDocument_IsPublished_AndTheStandInsAnswerPassesTheChecks_ExceptItsWrongFigure_BR147_AIA083_AIA087()
    {
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ai3Path)!);
            await File.WriteAllBytesAsync(Ai3Path, SampleDocuments.Ai3Sample());
        }

        File.Exists(Ai3Path).ShouldBeTrue("Run with INSYS_UPDATE_SNAPSHOTS=1 to publish docs/frontend/ai3-sample-tenant-documents.pdf.");
        var content = await File.ReadAllBytesAsync(Ai3Path);
        var extracted = new DraftDocumentExtractor().Extract(DraftDocumentKinds.Pdf, content).ShouldNotBeNull();
        extracted.Pages[0].Text.ShouldContain(Insys.Assist.Infrastructure.DevelopmentAiModelProvider.SetupSampleMarker);
        IdentifierCheck.Find(extracted).ShouldBeEmpty("the test document holds no identifier");
        var minimised = extracted with { Pages = extracted.Pages.Select(p => p with { Text = Minimiser.Minimise(p.Text).Text }).ToList() };
        var sent = new SentDocument(1, Guid.CreateVersion7(), "ai3-sample-tenant-documents.pdf", minimised);
        var targets = SetupMappingTargets.For(["CLAIMS_HANDLER", "UNDERWRITER"]);
        var row = new TailorableRow("workflow.ref_business_calendars", "CAL", """{"calendar_code":"CAL","time_zone":null}""", ["time_zone"],
            new Dictionary<string, IReadOnlyList<string>> { ["timeZone"] = ["Africa/Johannesburg"] });

        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Testing" };
        var answer = await new Insys.Assist.Infrastructure.DevelopmentAiModelProvider(environment).Complete(
            new Insys.BuildingBlocks.Application.Ai.AiModelRequest("development", "tenant-setup-v1", "instructions", PromptData.Delimit(SetupAiPayload.Render([row], targets, [sent])), null),
            CancellationToken.None);
        var result = SetupAiCheck.Check(answer.Text, [row], targets, [sent], new HashSet<(string, string)>()).ShouldNotBeNull();

        result.Tailored.ShouldHaveSingleItem().AnswerValue.ShouldBe("Africa/Johannesburg");
        result.Mapped.Select(m => m.Code).ShouldBe(["ACC-4100", "ACC-2100", "AUTH-CLAIM_PAYMENT-CLAIMS_MANAGER", "CLAIMS_HANDLER"]);
        result.Unmapped.Select(u => u.Reason).ShouldBe([SetupAiCheck.NotInQuote, Insys.Assist.Domain.UnmappedReasons.NotMapped]);
    }
}
