/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ArchitectureTests
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
using System.Text.RegularExpressions;
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>
/// HLT-008, DOC-003, IAM-006: a query runs in a read-only transaction and its audit entries are never written, so a read
/// that must be audited is a command. No query's Handle method may record an audit entry: one that did would look audited
/// and not be (the clinical read did until Wave 3 batch 4).
/// </summary>
public sealed partial class QueryAuditRuleTests
{
    [Fact]
    public void NoQueryHandler_RecordsAnAuditEntry_HLT008()
    {
        var root = RepositoryPaths.Root;
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .ToList();
        var queries = files.SelectMany(f => QueryRecord().Matches(f.Text).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

        var handled = 0;
        var found = new List<string>();
        foreach (var (file, text) in files)
        {
            foreach (Match handle in HandleMethod().Matches(text))
            {
                if (!queries.Contains(handle.Groups[1].Value))
                {
                    continue;
                }

                handled++;
                if (Audits().IsMatch(Body(text, handle.Index + handle.Length)))
                {
                    found.Add($"{Path.GetRelativePath(root, file)}: Handle({handle.Groups[1].Value})");
                }
            }
        }

        handled.ShouldBeGreaterThan(100);
        found.ShouldBeEmpty("An audited read must be a command (ICommand), or its audit is dropped.");
    }

    /// <summary>The method's body: an expression body to its semicolon, or a block to its closing brace.</summary>
    private static string Body(string text, int from)
    {
        var arrow = text.IndexOf("=>", from, StringComparison.Ordinal);
        var brace = text.IndexOf('{', from);
        if (arrow >= 0 && (brace < 0 || arrow < brace))
        {
            return text[arrow..text.IndexOf(';', arrow)];
        }

        var depth = 0;
        for (var i = brace; i < text.Length; i++)
        {
            depth += text[i] == '{' ? 1 : text[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return text[brace..(i + 1)];
            }
        }

        return text[brace..];
    }

    [GeneratedRegex(@"record (\w+)\([^;{]*?\)\s*:\s*IQuery<", RegexOptions.Singleline)]
    private static partial Regex QueryRecord();

    [GeneratedRegex(@"Task<Result<[^\n]*?>> Handle\((\w+) \w+, CancellationToken \w+\)")]
    private static partial Regex HandleMethod();

    [GeneratedRegex(@"\baudit\w*\.Record\(|\bIAuditSink\b|\bsink\.Record\(")]
    private static partial Regex Audits();
}
