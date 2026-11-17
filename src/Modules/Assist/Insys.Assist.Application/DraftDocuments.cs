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
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Insys.Assist.Application;

/// <summary>The files AI-1 reads (ai-assist.md §5.5): PDF, DOCX and XLSX only, the type read from the content, never from the file name.</summary>
public static class DraftDocumentKinds
{
    public const string Pdf = "PDF";
    public const string Docx = "DOCX";
    public const string Xlsx = "XLSX";

    /// <summary>At most 20 MB a file and 10 files a run (§5.5).</summary>
    public const long MaxBytes = 20L * 1024 * 1024;
    public const int MaxFiles = 10;

    /// <summary>The kind the content is, or null when it is none of the three. A DOCX or XLSX is an Open XML package: a zip holding
    /// <c>[Content_Types].xml</c> and the main part of its kind; a zip that holds both, or neither, is refused.</summary>
    public static string? Detect(byte[] content)
    {
        if (content.Length >= 5 && content.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            return Pdf;
        }

        if (content.Length < 4 || !content.AsSpan(0, 4).SequenceEqual("PK\u0003\u0004"u8))
        {
            return null;
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            if (!names.Contains("[Content_Types].xml"))
            {
                return null;
            }

            var word = names.Contains("word/document.xml");
            var excel = names.Contains("xl/workbook.xml");
            return word == excel ? null : word ? Docx : Xlsx;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>A document's text and tables, page by page (AIA-011). A page with no text layer is listed as unreadable, never guessed.</summary>
/// <param name="Pages">Each page's text: a PDF's pages, a DOCX's pages as its page breaks divide it, an XLSX's sheets in order.</param>
/// <param name="Tables">The tables found, each on its page.</param>
/// <param name="UnreadablePages">Pages with content but no text layer, such as a scanned image.</param>
public sealed record ExtractedDocument(string Kind, IReadOnlyList<ExtractedPage> Pages, IReadOnlyList<ExtractedTable> Tables, IReadOnlyList<int> UnreadablePages);

/// <param name="Number">From 1.</param>
/// <param name="Name">An XLSX sheet's name; null otherwise.</param>
public sealed record ExtractedPage(int Number, string Text, string? Name = null);

/// <summary>A table as the file holds it, row by row, each cell's text as written.</summary>
/// <param name="Index">From 1, in reading order on its page.</param>
public sealed record ExtractedTable(int Page, int Index, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Extracts a document AI-1 may read. Throws nothing for a malformed file: it is reported unreadable.</summary>
public interface IDraftDocumentExtractor
{
    ExtractedDocument? Extract(string kind, byte[] content);
}

/// <summary>An obvious identifier found (§5.5): its kind and page, never its value.</summary>
public sealed record IdentifierFound(string Kind, int Page);

/// <summary>
/// The identifier check (D-16, ai-assist.md §5.5): deterministic, on the extracted text. A document holding an obvious identifier is refused for the
/// run, not masked. English labels only (D-4).
/// </summary>
public static partial class IdentifierCheck
{
    public const string Email = "EMAIL";
    public const string SouthAfricanId = "SOUTH_AFRICAN_ID";
    public const string Iban = "IBAN";
    public const string LabelledNumber = "LABELLED_NUMBER";

    /// <summary>Each kind found on each page, once, in page order.</summary>
    public static IReadOnlyList<IdentifierFound> Find(ExtractedDocument document)
    {
        var found = new List<IdentifierFound>();
        var texts = document.Pages.Select(p => (p.Number, p.Text))
            .Concat(document.Tables.Select(t => (Number: t.Page, Text: string.Join("\n", t.Rows.Select(r => string.Join(" ", r))))));
        foreach (var (page, text) in texts)
        {
            foreach (var kind in Kinds(text))
            {
                if (!found.Contains(new IdentifierFound(kind, page)))
                {
                    found.Add(new IdentifierFound(kind, page));
                }
            }
        }

        return found.OrderBy(f => f.Page).ToList();
    }

    /// <summary>The kinds of identifier in one text.</summary>
    public static IEnumerable<string> Kinds(string text)
    {
        if (EmailPattern().IsMatch(text))
        {
            yield return Email;
        }

        if (ThirteenDigits().Matches(text).Any(m => IsSouthAfricanId(m.Value)))
        {
            yield return SouthAfricanId;
        }

        if (IbanPattern().Matches(text).Any(m => IsIban(m.Value)))
        {
            yield return Iban;
        }

        if (Labelled().IsMatch(text))
        {
            yield return LabelledNumber;
        }
    }

    /// <summary>13 digits: a valid date of birth (YYMMDD), then the Luhn check digit last.</summary>
    public static bool IsSouthAfricanId(string digits)
    {
        if (digits.Length != 13 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var month = int.Parse(digits.AsSpan(2, 2));
        var day = int.Parse(digits.AsSpan(4, 2));
        // Either century: the date only has to exist, so 29 February is checked against a leap year in either.
        if (month is < 1 or > 12 || day < 1 || (day > DateTime.DaysInMonth(2000 + int.Parse(digits.AsSpan(0, 2)), month)
                                                 && day > DateTime.DaysInMonth(1900 + int.Parse(digits.AsSpan(0, 2)), month)))
        {
            return false;
        }

        return Luhn(digits);
    }

    /// <summary>An IBAN: country letters, two check digits and up to 30 letters or digits, spaces allowed in groups of four, passing mod 97 (ISO 13616).</summary>
    public static bool IsIban(string candidate)
    {
        var iban = candidate.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (iban.Length is < 15 or > 34)
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetterUpper(c) ? c - 'A' + 10 : -1;
            if (value < 0)
            {
                return false;
            }

            remainder = value >= 10 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }

        return remainder == 1;
    }

    private static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<!\d)\d{13}(?!\d)")]
    private static partial Regex ThirteenDigits();

    [GeneratedRegex(@"\b[A-Za-z]{2}\d{2}(?: ?[A-Za-z0-9]{4}){2,7}(?: ?[A-Za-z0-9]{1,4})?\b")]
    private static partial Regex IbanPattern();

    // A label, then within a few characters (a colon, "is", a dash or a hash) a number of 6 or more digits, spaces or dashes between groups allowed.
    [GeneratedRegex(@"\b(ID number|identity number|national ID|NIN|BVN|passport number|account number|account no|acc no)\b\.?\s*(?:is\s+|[:#\-–]\s*)?\d(?:[\s\-]?\d){5,}", RegexOptions.IgnoreCase)]
    private static partial Regex Labelled();
}
