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
using System.Reflection;
using Insys.Workflow.Domain.Processes;

namespace Insys.Workflow.Tests;

/// <summary>
/// A built-in process is declared as a field and listed by hand in Find. One left out of the list starts, and then its task can't be completed:
/// the definition is looked up again by code. Each must also be a well-formed one-way flow from its first state to a final state.
/// </summary>
public sealed class BuiltInProcessRuleTests
{
    public static TheoryData<string> Declared()
    {
        var data = new TheoryData<string>();
        foreach (var definition in All())
        {
            data.Add(definition.ProcessCode);
        }

        return data;
    }

    private static IEnumerable<ProcessDefinition> All() =>
        typeof(BuiltInProcesses).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ProcessDefinition))
            .Select(f => (ProcessDefinition)f.GetValue(null)!);

    [Theory]
    [MemberData(nameof(Declared))]
    public void EveryDeclaredProcess_CanBeFound_ByCodeAndVersion(string code)
    {
        var declared = All().Single(d => d.ProcessCode == code);
        BuiltInProcesses.Find(code, null).ShouldBe(declared);
        BuiltInProcesses.Find(code, declared.Version).ShouldBe(declared);
    }

    [Theory]
    [MemberData(nameof(Declared))]
    public void EveryDeclaredProcess_IsValid_AndEveryStateIsReachable_AndItCanFinish(string code)
    {
        var declared = All().Single(d => d.ProcessCode == code);
        declared.Validate().IsSuccess.ShouldBeTrue();
        declared.IsFinal(declared.States[0]).ShouldBeFalse("a process that starts finished never opens a task");

        var reached = new HashSet<string> { declared.States[0] };
        while (declared.Transitions.FirstOrDefault(t => reached.Contains(t.From) && !reached.Contains(t.To)) is { } next)
        {
            reached.Add(next.To);
        }

        reached.ShouldBe(declared.States, ignoreOrder: true);
        reached.Any(declared.IsFinal).ShouldBeTrue("a process with no final state never ends");
    }

    [Fact]
    public void ProcessCodes_AreUnique() => All().Select(d => d.ProcessCode).ShouldBeUnique();
}
