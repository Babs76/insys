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
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>INSYS's checks on AI-1's output (AIA-012 to AIA-016): nothing the model says is trusted. Sample wording only.</summary>
public sealed class DraftProposalCheckTests
{
    private static readonly ProductDraftTarget Target = new("SAMPLE", "LIFE", 1, "Sample", ["FUNERAL"], ["FUNERAL", "ACCIDENTAL_DEATH"], "Issue", "None");

    private static readonly Guid DocumentId = Guid.CreateVersion7();

    private static readonly SentDocument Document = new(1, DocumentId, "sample.pdf", new ExtractedDocument("PDF",
        [new ExtractedPage(1, "Sample Funeral Plan\nCover ends at age 75"), new ExtractedPage(2, "Rates\nAge band Monthly\n18-40 1,045.00")],
        [new ExtractedTable(2, 1, [["Age band", "Monthly"], ["18-40", "1,045.00"]])], []));

    private static IReadOnlyList<CheckedItem>? Check(object output) =>
        DraftProposalCheck.Check(JsonSerializer.Serialize(output), DraftFields.For(Target), [Document]);

    [Fact]
    public void TheFieldList_IsGeneratedFromTheModel_TheVersionsFields_TheProductsTables_AndRateTables_AIA012()
    {
        var fields = DraftFields.For(Target);
        fields.Where(f => f.InDraft).Select(f => f.Path).ShouldBe([DraftFields.Name, DraftFields.Coverages, DraftFields.EffectiveFrom, DraftFields.CoverStartsOn,
            DraftFields.ReinstatementArrears]);
        fields.Single(f => f.Path == DraftFields.Coverages).Allowed.ShouldBe(["FUNERAL", "ACCIDENTAL_DEATH"]);
        fields.Select(f => f.Path).ShouldContain("rating.cfg_premium_parameters");
        fields.Select(f => f.Path).ShouldContain("life.cfg_modal_factors");
        fields.Select(f => f.Path).ShouldNotContain("general.cfg_renewal_rules", "another line's table");
        fields.Select(f => f.Path).ShouldNotContain("channel.cfg_channel_transaction_permissions", "distribution, not the product's terms");
        fields.Single(f => f.Path == DraftFields.RateTables).Columns.ShouldBe(["tableCode", "dimensions", "values", "interpolation"]);
    }

    [Fact]
    public void ARateTable_MustMatchItsSourceCellForCell_NumbersComparedAsNumbers_AIA015()
    {
        var rows = new[] { new[] { "Age band", "Monthly" }, new[] { "18-40", "1045" } };
        Check(new { rateTables = new[] { new { citation = new { document = 1, page = 2, quote = "Age band" }, rows } } })!
            .Single(i => i.Kind == DraftItemKinds.RateTable).Dropped.ShouldBeNull("1045 is 1,045.00");

        var extraRow = new[] { new[] { "Age band", "Monthly" }, new[] { "18-40", "1045" }, new[] { "41-55", "1200" } };
        var wrongCell = new[] { new[] { "Age band", "Monthly" }, new[] { "18-40", "1045.01" } };
        var otherPage = new[] { new[] { "Age band", "Monthly" }, new[] { "18-40", "1045" } };
        var items = Check(new
        {
            rateTables = new object[]
            {
                new { citation = new { document = 1, page = 2, quote = "Age band" }, rows = extraRow },
                new { citation = new { document = 1, page = 2, quote = "Age band" }, rows = wrongCell },
                new { citation = new { document = 1, page = 1, quote = "Sample Funeral Plan" }, rows = otherPage },
            },
        })!;
        items.Where(i => i.Kind == DraftItemKinds.RateTable).Select(i => i.Dropped).ShouldBe(["TABLE_MISMATCH", "TABLE_MISMATCH", "TABLE_MISMATCH"]);
    }

    [Theory]
    [InlineData("Cover ends at age 75", null)]
    [InlineData("Cover ends at the age (next birthday) of 75", null)]
    [InlineData("age >= 75", "RULE_EXPRESSION")]
    [InlineData("age == 75 && smoker", "RULE_EXPRESSION")]
    [InlineData("MAX(age, 75)", "RULE_EXPRESSION")]
    [InlineData("when age > 75 { lapse }", "RULE_EXPRESSION")]
    public void ARule_IsKeptOnlyInPlainWords_AIA016(string text, string? dropped) =>
        Check(new { rules = new[] { new { text, citation = new { document = 1, page = 1, quote = "Cover ends at age 75" } } } })!.Single(i => i.Kind == DraftItemKinds.Rule).Dropped.ShouldBe(dropped);

    [Fact]
    public void Found_NeedsTheValueInItsQuote_AndMissing_IsInsysesToSay_AIA014()
    {
        var items = Check(new
        {
            values = new object[]
            {
                new { field = "version.name", value = "Sample Funeral Plan", flag = "FOUND", citation = new { document = 1, page = 1, quote = "Sample Funeral Plan" } },
                new { field = "version.effectiveFrom", value = "2027-01-01", flag = "FOUND", citation = new { document = 1, page = 1, quote = "Sample Funeral Plan" } },
                new { field = "version.coverStartsOn", value = "Issue", flag = "MISSING", citation = new { document = 1, page = 1, quote = "Sample Funeral Plan" } },
            },
        })!;
        items.Single(i => i.Field == "version.name").Flag.ShouldBe(DraftItemFlags.Found);
        items.Single(i => i.Field == "version.effectiveFrom").Flag.ShouldBe(DraftItemFlags.Interpreted, "the date isn't in the quote");
        items.Single(i => i.Field == "version.coverStartsOn").Flag.ShouldBe(DraftItemFlags.Interpreted, "the model can't say Missing");
        items.Single(i => i.Field == "version.coverages").Flag.ShouldBe(DraftItemFlags.Missing, "required, and no value kept");
    }

    [Fact]
    public void ACitation_MustResolveToTheDocumentAndPageSent_AIA013()
    {
        var items = Check(new
        {
            values = new object[]
            {
                new { field = "version.name", value = "Sample Funeral Plan", flag = "FOUND", citation = new { document = 2, page = 1, quote = "Sample Funeral Plan" } },
                new { field = "version.name", value = "Sample Funeral Plan", flag = "FOUND", citation = new { document = 1, page = 2, quote = "Sample Funeral Plan" } },
                new { field = "version.name", value = "Sample Funeral Plan", flag = "FOUND", citation = new { document = 1, page = 1, quote = "  sample   FUNERAL\nplan " } },
            },
        })!;
        items.Where(i => i.Field == "version.name").Select(i => i.Dropped).ShouldBe(["NO_CITATION", "CITATION_NOT_FOUND", null],
            "no such document; not on that page; spacing and case don't decide");
    }

    [Fact]
    public void OnlyOneJsonObject_IsRead_AFencedOneIncluded_AndNoMoreThanTheLimit()
    {
        DraftProposalCheck.Check("Sample Funeral Plan", DraftFields.For(Target), [Document]).ShouldBeNull();
        DraftProposalCheck.Check("[1, 2]", DraftFields.For(Target), [Document]).ShouldBeNull();
        DraftProposalCheck.Check("```json\n{\"values\": []}\n```", DraftFields.For(Target), [Document]).ShouldNotBeNull();

        var flood = Enumerable.Range(0, 1000).Select(_ => new { text = "Cover ends at age 75", citation = new { document = 1, page = 1, quote = "Cover ends at age 75" } });
        Check(new { rules = flood })!.Count(i => i.Kind == DraftItemKinds.Rule).ShouldBe(DraftProposalCheck.MaxItems);
    }
}
