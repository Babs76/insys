/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
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
