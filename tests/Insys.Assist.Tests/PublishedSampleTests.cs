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
}
