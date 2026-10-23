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

/// <summary>
/// GAP-031: no two types share an OpenAPI schema ID. The generator keeps one schema per ID, so a shared ID documents one
/// module's responses with another module's shape. Checked over every type that can be in a document, not only the
/// types endpoints use today, so a new same-named record fails here before it reaches a document; SchemaNames.For also
/// refuses a shared ID while a document is generated.
/// </summary>
public sealed class SchemaNameTests
{
    [Fact]
    public void EveryModuleType_HasItsOwnSchemaId_GAP031()
    {
        var id = Solution.Assemblies.Single(a => a.GetName().Name == "Insys.BuildingBlocks.Api").GetType("Insys.BuildingBlocks.Api.SchemaNames", throwOnError: true)!
            .GetMethod("Id", BindingFlags.Public | BindingFlags.Static)!;
        var named = Solution.ModuleAssemblies
            .SelectMany(m => m.Assembly.GetExportedTypes().Select(t => (m.Layer, Type: t)))
            .Where(x => CanBeApiType(x.Layer, x.Type))
            .Select(x => x.Type)
            .Select(t => (Type: t, Id: (string?)id.Invoke(null, [t])))
            .Where(x => x.Id is not null)
            .ToList();

        named.ShouldNotBeEmpty();
        named.GroupBy(x => x.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Type.FullName))}")
            .ShouldBeEmpty();
    }

    /// <summary>
    /// Requests and responses (Application, Api, Contracts), generated static data records, and the domain enums views
    /// expose. Domain and integration events are never in a document; a domain event and its contract may share a name.
    /// </summary>
    private static bool CanBeApiType(string layer, Type type) =>
        !type.IsGenericTypeDefinition && !type.IsInterface && !type.IsAbstract && !type.IsNested
        && !type.GetInterfaces().Any(i => i.Name is "IDomainEvent" or "IIntegrationEvent")
        && (layer is "Application" or "Api" or "Contracts"
            || (layer == "Domain" && type.IsEnum)
            || (layer == "Infrastructure" && type.Namespace?.EndsWith(".StaticData", StringComparison.Ordinal) == true));

    [Theory]
    [InlineData("Insys.General.Application.PolicyView", "GeneralPolicyView")]
    [InlineData("Insys.Life.Application.PolicyView", "LifePolicyView")]
    [InlineData("Insys.Party.Application.PartyView", "PartyView")]
    public void ModuleTypes_AreNamedWithTheirModule(string typeName, string expected)
    {
        var type = Solution.ModuleAssemblies.Select(m => m.Assembly.GetType(typeName)).FirstOrDefault(t => t is not null);
        type.ShouldNotBeNull(typeName);
        var id = Solution.Assemblies.Single(a => a.GetName().Name == "Insys.BuildingBlocks.Api").GetType("Insys.BuildingBlocks.Api.SchemaNames")!
            .GetMethod("Id")!.Invoke(null, [type]);
        id.ShouldBe(expected);
    }
}
