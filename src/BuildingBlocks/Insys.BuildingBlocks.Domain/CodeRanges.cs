namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// Code lists whose entries are single codes (A09) or inclusive ranges (A00-A09), as static data writes diagnosis,
/// procedure and tariff codes (ADR-012). Codes compare as ordinal text; a range's upper bound covers its subcodes, so
/// A00-A09 contains A09.1.
/// </summary>
public static class CodeRanges
{
    public static bool Contains(IEnumerable<string>? entries, string? code) =>
        code is not null && entries is not null && entries.Any(e => Contains(e, code));

    public static bool Contains(string entry, string code)
    {
        var dash = entry.IndexOf('-', 1);
        var (from, to) = dash < 0 ? (entry.Trim(), entry.Trim()) : (entry[..dash].Trim(), entry[(dash + 1)..].Trim());
        return string.CompareOrdinal(code, from) >= 0 && string.CompareOrdinal(code.Length > to.Length ? code[..to.Length] : code, to) <= 0;
    }

    /// <summary>How narrow a range is, so a more specific mapping wins: an exact code before a range, a shorter range before a longer one.</summary>
    public static (int Exact, int Width) Specificity(string from, string to) =>
        (from == to ? 0 : 1, CommonPrefix(from, to) is var prefix ? -prefix : 0);

    private static int CommonPrefix(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        return i;
    }
}
