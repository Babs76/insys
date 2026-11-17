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

/// <summary>AI-1's documents (AIA-011, D-16, ai-assist.md §5.5): the type from the content, extraction page by page, and the identifier check. Sample data only.</summary>
public sealed class DraftDocumentTests
{
    private readonly DraftDocumentExtractor _extractor = new();

    [Fact]
    public void TheType_IsReadFromTheContent_PdfDocxAndXlsxOnly_D16()
    {
        DraftDocumentKinds.Detect(SampleDocuments.Pdf(["Funeral cover"])).ShouldBe(DraftDocumentKinds.Pdf);
        DraftDocumentKinds.Detect(SampleDocuments.Docx("Funeral cover")).ShouldBe(DraftDocumentKinds.Docx);
        DraftDocumentKinds.Detect(SampleDocuments.Xlsx(("Rates", ["Age\tRate"]))).ShouldBe(DraftDocumentKinds.Xlsx);

        DraftDocumentKinds.Detect("Funeral cover"u8.ToArray()).ShouldBeNull("plain text");
        DraftDocumentKinds.Detect([]).ShouldBeNull();
        DraftDocumentKinds.Detect([0x50, 0x4B, 0x03, 0x04, 0, 0, 0]).ShouldBeNull("a broken zip");
        DraftDocumentKinds.Detect(Zip(("readme.txt", "not a document"))).ShouldBeNull("a zip that isn't an Open XML package");
        DraftDocumentKinds.Detect(Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w/>"), ("xl/workbook.xml", "<x/>")))
            .ShouldBeNull("a package claiming to be both");
    }

    [Fact]
    public void APdf_IsReadPageByPage_ItsAlignedLinesAsATable_AndAPageWithNoTextIsUnreadable_AIA011()
    {
        var pdf = SampleDocuments.Pdf(["Sample funeral plan", "Waiting period: 6 months"], null, ["Age band\tMonthly rate", "18-40\t45.00", "41-55\t62.50"]);

        var extracted = _extractor.Extract(DraftDocumentKinds.Pdf, pdf).ShouldNotBeNull();

        extracted.Pages.Count.ShouldBe(3);
        extracted.Pages[0].Text.ShouldContain("Waiting period: 6 months");
        extracted.UnreadablePages.ShouldBe([2]);
        var table = extracted.Tables.ShouldHaveSingleItem();
        table.Page.ShouldBe(3);
        table.Rows.Select(r => string.Join("|", r)).ShouldBe(["Age band|Monthly rate", "18-40|45.00", "41-55|62.50"]);
    }

    [Fact]
    public void ADocx_IsSplitAtItsPageBreaks_WithItsTables_AIA011()
    {
        var docx = SampleDocuments.Docx("Sample funeral plan", "[page]", "Rates", "[table]Age band|Monthly rate;18-40|45.00;41-55|62.50");

        var extracted = _extractor.Extract(DraftDocumentKinds.Docx, docx).ShouldNotBeNull();

        extracted.Pages.Select(p => p.Number).ShouldBe([1, 2]);
        extracted.Pages[0].Text.ShouldBe("Sample funeral plan");
        extracted.Pages[1].Text.ShouldContain("Rates");
        var table = extracted.Tables.ShouldHaveSingleItem();
        table.Page.ShouldBe(2);
        table.Rows.Select(r => string.Join("|", r)).ShouldBe(["Age band|Monthly rate", "18-40|45.00", "41-55|62.50"]);
    }

    [Fact]
    public void AnXlsx_HasASheetPerPage_AndAnEmptyCellKeepsItsPlace_AIA011()
    {
        var xlsx = SampleDocuments.Xlsx(("Notes", ["Sample plan"]), ("Rates", ["Age band\tMonthly\tAnnual", "18-40\t45\t", "41-55\t62.5\t750"]));

        var extracted = _extractor.Extract(DraftDocumentKinds.Xlsx, xlsx).ShouldNotBeNull();

        extracted.Pages.Select(p => (p.Number, p.Name)).ShouldBe([(1, "Notes"), (2, "Rates")]);
        var rates = extracted.Tables.Single(t => t.Page == 2);
        rates.Rows.Select(r => string.Join("|", r)).ShouldBe(["Age band|Monthly|Annual", "18-40|45|", "41-55|62.5|750"]);
    }

    [Fact]
    public void AMalformedFile_IsReportedUnreadable_NeverGuessed_AIA011()
    {
        _extractor.Extract(DraftDocumentKinds.Pdf, "%PDF-1.7 broken"u8.ToArray()).ShouldBeNull();
        _extractor.Extract(DraftDocumentKinds.Docx, Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", "not xml"))).ShouldBeNull();
        _extractor.Extract("TXT", "text"u8.ToArray()).ShouldBeNull();
    }

    [Theory]
    [InlineData("Write to claims@example.com for help.", IdentifierCheck.Email)]
    [InlineData("Life assured 8001015009087 aged 46", IdentifierCheck.SouthAfricanId)]
    [InlineData("Pay into GB82 WEST 1234 5698 7654 32 monthly", IdentifierCheck.Iban)]
    [InlineData("Pay into GB82WEST12345698765432 monthly", IdentifierCheck.Iban)]
    [InlineData("ID number: 7501015800085", IdentifierCheck.LabelledNumber)]
    [InlineData("Passport number A1234567 is not digits, but national ID 12345678 is", IdentifierCheck.LabelledNumber)]
    [InlineData("BVN 22212345678", IdentifierCheck.LabelledNumber)]
    [InlineData("Account no. 62 1234 5678", IdentifierCheck.LabelledNumber)]
    [InlineData("acc no 1234567", IdentifierCheck.LabelledNumber)]
    public void AnObviousIdentifier_IsFound_D16(string text, string kind) => IdentifierCheck.Kinds(text).ShouldContain(kind);

    [Theory]
    [InlineData("The sum assured is 8001015009088, an invented figure")] // 13 digits, wrong check digit
    [InlineData("Product code 9913015009087")] // month 13: no date
    [InlineData("GB82 WEST 1234 5698 7654 33")] // IBAN check digits fail
    [InlineData("Account number 12345")] // fewer than 6 digits
    [InlineData("The waiting period is 6 months; cover to age 75; rate 45.00 per R1,000")]
    [InlineData("See the account numbering scheme 123456")] // not the label
    [InlineData("user at example dot com")]
    public void OrdinaryProductWording_HoldsNoIdentifier_D16(string text) => IdentifierCheck.Kinds(text).ShouldBeEmpty();

    [Fact]
    public void TheCheck_NamesTheKindAndPage_NeverTheValue_IncludingInTables_D16()
    {
        var extracted = _extractor.Extract(DraftDocumentKinds.Docx,
            SampleDocuments.Docx("Sample plan", "[page]", "Contact claims@example.com", "[page]", "[table]Name|ID number;Sample|ID number 8001015009087")).ShouldNotBeNull();

        var found = IdentifierCheck.Find(extracted);

        found.ShouldContain(new IdentifierFound(IdentifierCheck.Email, 2));
        found.ShouldContain(new IdentifierFound(IdentifierCheck.SouthAfricanId, 3));
        found.ShouldContain(new IdentifierFound(IdentifierCheck.LabelledNumber, 3));
        found.ShouldNotContain(f => f.Page == 1);
        string.Join(" ", found).ShouldNotContain("8001015009087");
        string.Join(" ", found).ShouldNotContain("claims@example.com");
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }
}
