/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Insys.Assist.Application;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Insys.Assist.Infrastructure.Extraction;

/// <summary>
/// AI-1's extraction (AIA-011): PdfPig (Apache-2.0) for PDF, Open XML SDK (MIT) for DOCX and XLSX. Text and tables page by page, and pages
/// with no text layer listed as unreadable. Nothing is guessed: no OCR. A file that can't be parsed returns null, reported as unreadable.
/// </summary>
public sealed class DraftDocumentExtractor : IDraftDocumentExtractor
{
    /// <summary>A PDF line's words are a table's cells when separated by more than this many times the line's average character width.</summary>
    private const double CellGap = 2.0;

    public ExtractedDocument? Extract(string kind, byte[] content)
    {
        try
        {
            if (kind is DraftDocumentKinds.Docx or DraftDocumentKinds.Xlsx && !WithinExpandedSize(content))
            {
                return ExtractedDocument.TooLarge(kind);
            }

            var extracted = kind switch
            {
                DraftDocumentKinds.Pdf => Pdf(content),
                DraftDocumentKinds.Docx => Docx(content),
                DraftDocumentKinds.Xlsx => Xlsx(content),
                _ => null,
            };
            return extracted is { OverLimit: false } && extracted.Characters > ExtractionLimits.MaxDocumentCharacters ? ExtractedDocument.TooLarge(kind) : extracted;
        }
        catch (OverLimitException)
        {
            return ExtractedDocument.TooLarge(kind);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private sealed class OverLimitException : Exception;

    /// <summary>
    /// A zip bomb is refused before Open XML expands it: the entries' declared sizes, and each entry read through a counting stream that stops at the limit,
    /// since a declared size can lie.
    /// </summary>
    private static bool WithinExpandedSize(byte[] content)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(content, writable: false), System.IO.Compression.ZipArchiveMode.Read);
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > ExtractionLimits.MaxExpandedBytes || (total += entry.Length) > ExtractionLimits.MaxExpandedBytes)
            {
                return false;
            }
        }

        total = 0;
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if ((total += read) > ExtractionLimits.MaxExpandedBytes)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static ExtractedDocument Pdf(byte[] content)
    {
        using var pdf = PdfDocument.Open(content);
        var pages = new List<ExtractedPage>();
        var tables = new List<ExtractedTable>();
        var unreadable = new List<int>();
        foreach (var page in pdf.GetPages())
        {
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).Take(ExtractionLimits.MaxPdfWordsPerPage + 1).ToList();
            if (words.Count > ExtractionLimits.MaxPdfWordsPerPage)
            {
                throw new OverLimitException();
            }

            if (words.Count == 0)
            {
                // Content but no text: a scanned image, or an empty page. Either way nothing is read from it.
                unreadable.Add(page.Number);
                pages.Add(new ExtractedPage(page.Number, ""));
                continue;
            }

            var lines = Lines(words);
            pages.Add(new ExtractedPage(page.Number, string.Join("\n", lines.Select(l => string.Join(" ", l.Select(w => w.Text))))));
            tables.AddRange(PdfTables(page.Number, lines));
        }

        return new ExtractedDocument(DraftDocumentKinds.Pdf, pages, tables, unreadable);
    }

    /// <summary>Words grouped into lines by their baseline, top to bottom, each line left to right: sorted once, then one pass, so a page costs n log n.</summary>
    private static List<List<Word>> Lines(List<Word> words)
    {
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var height = Math.Max(word.BoundingBox.Height, 1);
            if (lines.Count > 0 && Math.Abs(lines[^1][0].BoundingBox.Bottom - word.BoundingBox.Bottom) < height / 2)
            {
                lines[^1].Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        foreach (var line in lines)
        {
            line.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        }

        return lines;
    }

    /// <summary>
    /// A PDF holds no tables, only placed words, so a table is read as consecutive lines (at least two) that each split into the same number
    /// (at least two) of cells, where a cell is a run of words with no wide gap. A stricter reading than a person's, never a looser one.
    /// </summary>
    private static IEnumerable<ExtractedTable> PdfTables(int page, List<List<Word>> lines)
    {
        var index = 0;
        var run = new List<IReadOnlyList<string>>();
        foreach (var cells in lines.Select(Cells).Append([]))
        {
            if (cells.Count >= 2 && (run.Count == 0 || run[0].Count == cells.Count))
            {
                run.Add(cells);
                continue;
            }

            if (run.Count >= 2)
            {
                yield return new ExtractedTable(page, ++index, run);
            }

            run = cells.Count >= 2 ? [cells] : [];
        }
    }

    private static List<string> Cells(List<Word> line)
    {
        var cells = new List<string>();
        var current = new List<string>();
        for (var i = 0; i < line.Count; i++)
        {
            if (i > 0)
            {
                var previous = line[i - 1];
                var charWidth = previous.BoundingBox.Width / Math.Max(previous.Text.Length, 1);
                if (line[i].BoundingBox.Left - previous.BoundingBox.Right > CellGap * Math.Max(charWidth, 1))
                {
                    cells.Add(string.Join(" ", current));
                    current = [];
                }
            }

            current.Add(line[i].Text);
        }

        cells.Add(string.Join(" ", current));
        return cells;
    }

    private static ExtractedDocument Docx(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var word = WordprocessingDocument.Open(stream, false);
        var body = word.MainDocumentPart?.Document?.Body;
        var pages = new List<ExtractedPage>();
        var tables = new List<ExtractedTable>();
        var page = 1;
        var text = new System.Text.StringBuilder();
        var tableIndex = 0;
        foreach (var element in body?.ChildElements ?? Enumerable.Empty<DocumentFormat.OpenXml.OpenXmlElement>())
        {
            switch (element)
            {
                case W.Paragraph paragraph:
                    foreach (var run in paragraph.Descendants())
                    {
                        if (run is W.Text t)
                        {
                            text.Append(t.Text);
                        }
                        else if (run is W.TabChar)
                        {
                            text.Append('\t');
                        }
                        else if (run is W.Break { Type.Value: var type } && type == W.BreakValues.Page)
                        {
                            Flush();
                        }
                    }

                    text.Append('\n');
                    break;
                case W.Table table:
                    var rows = table.Elements<W.TableRow>()
                        .Select(r => (IReadOnlyList<string>)r.Elements<W.TableCell>().Select(c => string.Join(" ", c.Elements<W.Paragraph>().Select(p => p.InnerText)).Trim()).ToList())
                        .ToList();
                    tables.Add(new ExtractedTable(page, ++tableIndex, rows));
                    foreach (var row in rows)
                    {
                        text.Append(string.Join("\t", row)).Append('\n');
                    }

                    break;
            }
        }

        Flush();
        return new ExtractedDocument(DraftDocumentKinds.Docx, pages, tables, []);

        void Flush()
        {
            pages.Add(new ExtractedPage(page, text.ToString().TrimEnd()));
            text.Clear();
            page++;
            tableIndex = 0;
        }
    }

    private static ExtractedDocument Xlsx(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var excel = SpreadsheetDocument.Open(stream, false);
        var workbook = excel.WorkbookPart ?? throw new InvalidDataException("No workbook.");
        var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        var pages = new List<ExtractedPage>();
        var tables = new List<ExtractedTable>();
        var number = 0;
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            number++;
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
            {
                continue;
            }

            // Each row as its cells by column, so an empty cell keeps its place.
            var rows = new List<IReadOnlyList<string>>();
            var cellCount = 0;
            long slots = 0;
            foreach (var row in part.Worksheet?.Descendants<Row>() ?? [])
            {
                if (rows.Count >= ExtractionLimits.MaxRows)
                {
                    throw new OverLimitException();
                }

                var cells = new SortedDictionary<int, string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    if (++cellCount > ExtractionLimits.MaxCells)
                    {
                        throw new OverLimitException();
                    }

                    var value = cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, out var s) && s < strings.Count ? strings[s]
                        : cell.DataType?.Value == CellValues.InlineString ? cell.InlineString?.InnerText ?? ""
                        : cell.CellValue?.Text ?? "";
                    var column = Column(cell.CellReference?.Value) ?? cells.Count;
                    if (column is < 0 or >= ExtractionLimits.MaxColumns)
                    {
                        throw new OverLimitException();
                    }

                    cells[column] = value;
                }

                if (cells.Count == 0)
                {
                    continue;
                }

                var width = cells.Keys.Max() + 1;
                // Empty cells are filled in as their places, so the slots, not the cells, are what's bounded.
                if ((slots += width) > ExtractionLimits.MaxCells * 2L)
                {
                    throw new OverLimitException();
                }

                rows.Add(Enumerable.Range(0, width).Select(c => cells.GetValueOrDefault(c, "")).ToList());
            }

            var widest = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            if ((long)widest * rows.Count > ExtractionLimits.MaxCells * 2L)
            {
                throw new OverLimitException();
            }

            var grid = rows.Select(r => (IReadOnlyList<string>)r.Concat(Enumerable.Repeat("", widest - r.Count)).ToList()).ToList();
            pages.Add(new ExtractedPage(number, string.Join("\n", grid.Select(r => string.Join("\t", r))), sheet.Name?.Value));
            if (grid.Count > 0)
            {
                tables.Add(new ExtractedTable(number, 1, grid));
            }
        }

        return new ExtractedDocument(DraftDocumentKinds.Xlsx, pages, tables, []);
    }

    /// <summary>A cell reference's column from 0 (A1 → 0, AB7 → 27).</summary>
    private static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        foreach (var c in reference.TakeWhile(char.IsAsciiLetter))
        {
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            if (column > ExtractionLimits.MaxColumns)
            {
                return ExtractionLimits.MaxColumns;
            }
        }

        return column - 1;
    }
}
