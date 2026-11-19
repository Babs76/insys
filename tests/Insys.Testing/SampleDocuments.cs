/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Insys.Testing;

/// <summary>Sample product documents for AI-1's tests (AIA-011), made in code: invented wording and rates, never a real insurer's.</summary>
public static class SampleDocuments
{
    /// <summary>A PDF whose pages each hold lines of text; a line's cells (split by tabs) are placed in columns, so a run of them reads as a table.
    /// A null page is left empty, with no text layer.</summary>
    public static byte[] Pdf(params string[]?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 780.0;
            foreach (var line in lines ?? [])
            {
                var x = 40.0;
                foreach (var cell in line.Split('\t'))
                {
                    page.AddText(cell, 10, new PdfPoint(x, y), font);
                    x += 120;
                }

                y -= 16;
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// The AI-1 test document the front end uploads (docs/frontend/ai1-sample-product.pdf), recognised by the stand-in model by its first line. Invented
    /// wording and rates; no identifier, so the identifier check passes it.
    /// </summary>
    public static byte[] Ai1Sample() => Pdf(
        ["INSYS AI-1 SAMPLE PRODUCT DOCUMENT (sample data only, not a real product)", "Product name: Sample Funeral Plan",
            "Cover starts when the first premium is paid", "The monthly premium is at least R45.00", "Cover ends at age 75"],
        ["Monthly premium by age band", "Age band\tMonthly premium", "18-40\t45.00", "41-55\t62.50", "56-65\t88.00"]);

    /// <summary>
    /// The AI-3 test document the front end uploads to a setup (docs/frontend/ai3-sample-tenant-documents.pdf), recognised by the stand-in model by its first
    /// line: an invented chart of accounts, approval matrix and role list (role titles, not people's names, ai-assist.md §5.5). No identifier: account codes
    /// are four digits and nothing is labelled an account number.
    /// </summary>
    public static byte[] Ai3Sample() => Pdf(
        ["INSYS AI-3 SAMPLE TENANT DOCUMENT (sample data only, not a real insurer)", "Chart of accounts", "Account code\tAccount name\tType\tGL code",
            "4100\tPremium income\tINCOME\tGL 4100", "2100\tClaims payable\tLIABILITY\tGL 2100"],
        ["Approval matrix", "Claims managers approve claim payments up to ZAR 250,000.", "Finance managers approve premium refunds up to ZAR 50,000."],
        ["Role list", "Claims Officer", "Underwriter", "Compliance Officer"]);

    /// <summary>A DOCX of paragraphs; "[page]" starts a new page and "[table]a|b;c|d" is a table of rows split by ';' and cells by '|'.</summary>
    public static byte[] Docx(params string[] blocks)
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var body = new W.Body();
            foreach (var block in blocks)
            {
                if (block == "[page]")
                {
                    body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
                }
                else if (block.StartsWith("[table]", StringComparison.Ordinal))
                {
                    var table = new W.Table();
                    foreach (var row in block["[table]".Length..].Split(';'))
                    {
                        table.Append(new W.TableRow(row.Split('|').Select(c => new W.TableCell(new W.Paragraph(new W.Run(new W.Text(c)))))));
                    }

                    body.Append(table);
                }
                else
                {
                    body.Append(new W.Paragraph(new W.Run(new W.Text(block) { Space = SpaceProcessingModeValues.Preserve })));
                }
            }

            main.Document = new W.Document(body);
        }

        return stream.ToArray();
    }

    /// <summary>An XLSX with a sheet per entry, each row's cells split by tabs; an empty cell is left out of the sheet, as Excel does.</summary>
    public static byte[] Xlsx(params (string Name, string[] Rows)[] sheets)
    {
        using var stream = new MemoryStream();
        using (var excel = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = excel.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            uint id = 1;
            foreach (var (name, rows) in sheets)
            {
                var part = workbook.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                uint r = 1;
                foreach (var row in rows)
                {
                    var xmlRow = new Row { RowIndex = r };
                    var cells = row.Split('\t');
                    for (var c = 0; c < cells.Length; c++)
                    {
                        if (cells[c].Length == 0)
                        {
                            continue;
                        }

                        var reference = $"{(char)('A' + c)}{r}";
                        xmlRow.Append(decimal.TryParse(cells[c], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _)
                            ? new Cell { CellReference = reference, CellValue = new CellValue(cells[c]) }
                            : new Cell { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(cells[c])) });
                    }

                    data.Append(xmlRow);
                    r++;
                }

                part.Worksheet = new Worksheet(data);
                workbook.Workbook.Sheets!.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = id++, Name = name });
            }
        }

        return stream.ToArray();
    }
}
