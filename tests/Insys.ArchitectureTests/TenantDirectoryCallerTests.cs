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
using Shouldly;
using Xunit;

namespace Insys.ArchitectureTests;

/// <summary>
/// tenant.tenant's cross-tenant reads run in the platform operator's session (V0142), so they must be reachable only from platform jobs, health checks,
/// platform-only routes and the platform's own events (Ade, 18 November 2026). These tests pin who can reach them; a new caller fails until it is
/// reviewed and listed here with its reason.
/// </summary>
public sealed class TenantDirectoryCallerTests
{
    /// <summary>Every type that takes ITenantDirectory or ITenantCodes, or resolves one, and why each is the platform's.</summary>
    private static readonly IReadOnlyDictionary<string, string> Callers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OutboxProcessor"] = "the outbox relay, a hosted service",
        ["AnalyticsReleaseTasks"] = "release tasks at startup",
        ["ProductDraftJob"] = "a background job",
        ["SetupAiJob"] = "a background job (AI-3's passes, 7.8)",
        ["BillingJobs"] = "a background job",
        ["NotificationSender"] = "a background job",
        ["DocumentScanJob"] = "a background job",
        ["GeneralJobs"] = "a background job",
        ["HealthJobs"] = "a background job",
        ["LifeJobs"] = "a background job",
        ["PaymentJobs"] = "a background job",
        ["ProductJobs"] = "a background job",
        ["WorkflowJobs"] = "a background job",
        ["CaseTimelineJobs"] = "a background job",
        ["WebhookJobs"] = "a background job",
        ["DueDiligenceJobs"] = "a background job",
        ["SearchLogRetentionJob"] = "a background job",
        ["PrivilegedAccessExpiry"] = "a background job",
        ["ParkingJob"] = "a background job",
        ["SimulatorGuard"] = "a check at startup",
        ["PlanProductsHealthCheck"] = "a /health check",
        ["JobFunctionsHealthCheck"] = "a /health check",
        ["ParkedDeliveriesHealthCheck"] = "a /health check",
        ["ParkedDeliveryStore"] = "ByTenant, for GET /integration/parked-deliveries/by-tenant, which needs a platform permission",
        ["RetryParkedOnConfiguration"] = "its global branch, taken only for a global row's activation, which only the platform's routes publish",
    };

    private static readonly Type[] Directory = Solution.Assemblies.SelectMany(Types)
        .Where(t => t.FullName is "Insys.BuildingBlocks.Application.Context.ITenantDirectory" or "Insys.Tenancy.Contracts.ITenantCodes").ToArray();

    [Fact]
    public void OnlyPlatformJobsChecksRoutesAndEvents_ReachTheTenantDirectory_V0142()
    {
        Directory.Length.ShouldBe(2, "both interfaces are found");
        var byConstructor = Solution.Assemblies.SelectMany(Types)
            .Where(t => !t.IsInterface && !Directory.Any(d => d.IsAssignableFrom(t)))
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => Directory.Contains(p.ParameterType))))
            .Select(t => t.Name);
        var byResolution = SourceFiles().Where(f => File.ReadAllText(f).Contains("<ITenantDirectory>", StringComparison.Ordinal)
                || File.ReadAllText(f).Contains("ITenantCodes>()", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"(?:class|record) ([A-Za-z]+)").Select(m => m.Groups[1].Value))
            .Where(Callers.ContainsKey);
        var found = byConstructor.Concat(byResolution).ToHashSet(StringComparer.Ordinal);

        found.Except(Callers.Keys).ShouldBeEmpty("a new caller of the cross-tenant directory: review it, and list it here only if it is the platform's");
        Callers.Keys.Except(found).ShouldBeEmpty("a listed caller no longer reaches the directory: take it off the list");
    }

    /// <summary>The Tenancy reads that run as the platform's directory role (V0142) are these four, and no other.</summary>
    [Fact]
    public void OnlyTheDirectoryReads_RunAsThePlatformsDirectoryRole_V0142()
    {
        var file = SourceFiles().Single(f => f.EndsWith("TenancyStores.cs", StringComparison.Ordinal));
        var settings = SourceFiles().Single(f => f.EndsWith("TenantFunctionSettingsQuery.cs", StringComparison.Ordinal));
        var calls = SourceFiles().Where(f => f.Contains($"{Path.DirectorySeparatorChar}Tenancy{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => l.Text.Contains("TenantRows.Directory(", StringComparison.Ordinal) || l.Text.Contains("insys_tenant_directory", StringComparison.Ordinal))
            .ToList();
        calls.Count.ShouldBe(5, string.Join("; ", calls.Select(c => $"{Path.GetFileName(c.File)}:{c.Line}")));
        calls.Count(c => c.File == file).ShouldBe(4,
            "the helper's SET LOCAL ROLE, the directory for jobs, the time zone health check, and whether a tenant code is taken (the platform's RegisterTenant)");
        calls.Count(c => c.File == settings).ShouldBe(1, "the code-to-id lookup for the retry consumer's global branch");
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Insys.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        return System.IO.Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
