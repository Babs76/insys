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
/// BR-017 (D-28): every place that makes a dead letter, or the failed attempt a webhook dead letter is made from, gives it
/// an error from DeadLetterErrors or a fixed sentence: never an exception's message or text, which can quote the payload.
/// </summary>
public sealed partial class DeadLetterErrorRuleTests
{
    [Fact]
    public void NoDeadLetter_IsGivenAnExceptionsMessage_BR017()
    {
        var root = RepositoryPaths.Root;
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        var calls = 0;
        var found = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match call in DeadLetterCall().Matches(text))
            {
                var end = text.IndexOf(';', call.Index);
                var statement = text[call.Index..(end < 0 ? text.Length : end)];
                calls++;
                if (MessageText().IsMatch(statement))
                {
                    found.Add($"{Path.GetRelativePath(root, file)}: {statement.Split('\n')[0].Trim()}");
                }
            }
        }

        calls.ShouldBeGreaterThanOrEqualTo(4, "the outbox relay, the parking ledger, the webhook dead letter and its attempts");
        found.ShouldBeEmpty();
    }

    /// <summary>Where a dead letter's error is given: the sink, the dead letter itself, its error, and a webhook attempt's error.</summary>
    [GeneratedRegex(@"(?:\bsink\.DeadLetter\(|>\(\)\.DeadLetter\(|\bDeadLetter\.DeadLetterMessage\(|\bnew DeliveryError\(|\.RecordAttempt\()")]
    private static partial Regex DeadLetterCall();

    [GeneratedRegex(@"\b(?:ex|exception|e|inner|error)\.(?:Message\b|ToString\(\)|StackTrace\b|InnerException\b)")]
    private static partial Regex MessageText();
}
