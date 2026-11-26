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

namespace Insys.ArchitectureTests;

/// <summary>
/// BR-170: every process is started through ProcessRecordTypes.Start, which refuses a record type the contract doesn't list. The domain's
/// ProcessInstance.StartProcess is called from nowhere else in src.
/// </summary>
public sealed class ProcessStartRuleTests
{
    private const string Guard = "ProcessRecordTypes.cs";

    [Fact]
    public void EveryProcessStart_GoesThroughTheRecordTypeGuard_BR170()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Insys.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var callers = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bProcessInstance\s*\.\s*StartProcess\s*\("))
            .Select(Path.GetFileName).ToList();
        callers.ShouldBe([Guard], "start a process with ProcessRecordTypes.Start, so its record type is checked");
    }
}
