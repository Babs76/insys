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

namespace Insys.ArchitectureTests;

/// <summary>The production assemblies, found by name in the test output. New modules are picked up automatically.</summary>
internal static class Solution
{
    public static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Api", "Contracts"];

    public static IReadOnlyList<Assembly> Assemblies { get; } = Directory.GetFiles(AppContext.BaseDirectory, "Insys.*.dll")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => name is not null && !name.Contains(' ', StringComparison.Ordinal) && !name.EndsWith("Tests", StringComparison.Ordinal)
            && name != "Insys.Testing" && name != "Insys.DbMigrator")
        .Select(name => Assembly.Load(name!))
        .ToList();

    /// <summary>Module assemblies: Insys.{Module}.{Layer}.</summary>
    public static IEnumerable<(Assembly Assembly, string Module, string Layer)> ModuleAssemblies =>
        Assemblies.Select(a => (Assembly: a, Parts: a.GetName().Name!.Split('.')))
            .Where(x => x.Parts.Length == 3 && x.Parts[1] != "BuildingBlocks" && x.Parts[1] != "Api" && Layers.Contains(x.Parts[2]))
            .Select(x => (x.Assembly, x.Parts[1], x.Parts[2]));

    public static IEnumerable<string> InsysReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("Insys.", StringComparison.Ordinal));

    public static TheoryData<string> ModuleAssemblyNames(string layer)
    {
        var data = new TheoryData<string>();
        foreach (var module in ModuleAssemblies.Where(m => m.Layer == layer))
        {
            data.Add(module.Assembly.GetName().Name!);
        }

        return data;
    }
}
