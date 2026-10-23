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
using NetArchTest.Rules;

namespace Insys.ArchitectureTests;

/// <summary>The dependency rules in PROMPT §2, so any module can later be extracted into its own service.</summary>
public sealed class DependencyRuleTests
{
    public static TheoryData<string> DomainAssemblies => Solution.ModuleAssemblyNames("Domain");
    public static TheoryData<string> ApplicationAssemblies => Solution.ModuleAssemblyNames("Application");
    public static TheoryData<string> InfrastructureAssemblies => Solution.ModuleAssemblyNames("Infrastructure");
    public static TheoryData<string> ApiAssemblies => Solution.ModuleAssemblyNames("Api");
    public static TheoryData<string> ContractsAssemblies => Solution.ModuleAssemblyNames("Contracts");

    [Fact]
    public void Solution_HasAtLeastOneModule() => Solution.ModuleAssemblies.ShouldNotBeEmpty();

    [Fact]
    public void BuildingBlocksDomain_ReferencesNoOtherInsysAssembly() =>
        Solution.InsysReferences(Load("Insys.BuildingBlocks.Domain")).ShouldBeEmpty();

    [Fact]
    public void BuildingBlocks_DoNotReferenceModules() =>
        Solution.Assemblies.Where(a => a.GetName().Name!.StartsWith("Insys.BuildingBlocks.", StringComparison.Ordinal))
            .SelectMany(Solution.InsysReferences)
            .ShouldAllBe(name => name.StartsWith("Insys.BuildingBlocks.", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(DomainAssemblies))]
    public void Domain_ReferencesOnlyBuildingBlocksDomain(string assembly) =>
        Solution.InsysReferences(Load(assembly)).ShouldAllBe(name => name == "Insys.BuildingBlocks.Domain");

    [Theory]
    [MemberData(nameof(DomainAssemblies))]
    public void Domain_DoesNotUsePersistenceOrWebFrameworks(string assembly) =>
        Types.InAssembly(Load(assembly)).ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "FluentValidation", "System.Text.Json")
            .GetResult().FailingTypeNames.ShouldBeNull();

    [Theory]
    [MemberData(nameof(ApplicationAssemblies))]
    public void Application_ReferencesOwnDomain_BuildingBlocks_AndOnlyOtherModulesContracts(string assembly)
    {
        var module = ModuleOf(assembly);
        Solution.InsysReferences(Load(assembly)).ShouldAllBe(name =>
            name == $"Insys.{module}.Domain" || name == $"Insys.{module}.Contracts"
            || name == "Insys.BuildingBlocks.Domain" || name == "Insys.BuildingBlocks.Application" || name == "Insys.BuildingBlocks.Contracts"
            || name.EndsWith(".Contracts", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ApplicationAssemblies))]
    public void Application_DoesNotUsePersistenceOrWebFrameworks(string assembly) =>
        Types.InAssembly(Load(assembly)).ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore")
            .GetResult().FailingTypeNames.ShouldBeNull();

    [Theory]
    [MemberData(nameof(ContractsAssemblies))]
    public void Contracts_ReferenceOnlyBuildingBlocksContracts(string assembly) =>
        Solution.InsysReferences(Load(assembly)).ShouldAllBe(name => name == "Insys.BuildingBlocks.Contracts");

    [Theory]
    [MemberData(nameof(InfrastructureAssemblies))]
    public void Infrastructure_ReferencesOwnModule_AndMatchingBuildingBlocks(string assembly)
    {
        var module = ModuleOf(assembly);
        Solution.InsysReferences(Load(assembly)).ShouldAllBe(name =>
            (name.StartsWith($"Insys.{module}.", StringComparison.Ordinal) && name != $"Insys.{module}.Api")
            || (name.StartsWith("Insys.BuildingBlocks.", StringComparison.Ordinal) && name != "Insys.BuildingBlocks.Api")
            || name.EndsWith(".Contracts", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ApiAssemblies))]
    public void Api_ReferencesOwnModule_AndBuildingBlocks(string assembly)
    {
        var module = ModuleOf(assembly);
        Solution.InsysReferences(Load(assembly)).ShouldAllBe(name =>
            name.StartsWith($"Insys.{module}.", StringComparison.Ordinal)
            || name.StartsWith("Insys.BuildingBlocks.", StringComparison.Ordinal)
            || name.EndsWith(".Contracts", StringComparison.Ordinal));
    }

    [Fact]
    public void NoModule_ReferencesAnotherModulesDomainApplicationOrInfrastructure()
    {
        var violations = Solution.ModuleAssemblies
            .SelectMany(m => Solution.InsysReferences(m.Assembly).Select(reference => (m.Module, From: m.Assembly.GetName().Name, To: reference)))
            .Where(x => x.To.Split('.') is [_, var otherModule, var layer] && otherModule != "BuildingBlocks" && otherModule != x.Module
                && layer is "Domain" or "Application" or "Infrastructure" or "Api")
            .Select(x => $"{x.From} -> {x.To}")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(DomainAssemblies))]
    public void Aggregates_HaveNoPublicSetters(string assembly)
    {
        var aggregateRoot = typeof(BuildingBlocks.Domain.IAggregateRoot);
        var publicSetters = Load(assembly).GetTypes()
            .Where(t => aggregateRoot.IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod?.IsPublic == true)
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        publicSetters.ShouldBeEmpty();
    }

    private static Assembly Load(string name) => Solution.Assemblies.Single(a => a.GetName().Name == name);

    private static string ModuleOf(string assembly) => assembly.Split('.')[1];
}
