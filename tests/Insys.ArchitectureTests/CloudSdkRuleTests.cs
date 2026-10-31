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
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>ADR-027: only the Cloud adapter project knows which cloud it is on, so a new deployment target touches nothing else.</summary>
public sealed class CloudSdkRuleTests
{
    private const string AdapterProject = "Insys.BuildingBlocks.Cloud";

    private static readonly string[] Packages = ["AWSSDK.", "HuaweiCloud.", "Azure.", "Microsoft.Azure.", "AlibabaCloud.", "Aliyun.", "Google.Cloud.", "VaultSharp", "Minio"];

    private static readonly string[] Namespaces = ["using Amazon", "using HuaweiCloud", "using Azure.", "using AlibabaCloud", "using Aliyun", "using Google.Cloud", "using VaultSharp"];

    private static IEnumerable<string> Files(string pattern) =>
        new[] { "src", "tools" }.SelectMany(top => Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root, top), pattern, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}{AdapterProject}{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void OnlyTheCloudAdapterProject_ReferencesACloudSdk()
    {
        var found = Files("*.csproj").Where(f => File.ReadLines(f).Any(l => l.Contains("PackageReference", StringComparison.Ordinal) && Packages.Any(p => l.Contains($"\"{p}", StringComparison.Ordinal))))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Root, f)).ToList();
        found.ShouldBeEmpty();
    }

    [Fact]
    public void OnlyTheCloudAdapterProject_UsesACloudSdksNamespace()
    {
        var found = Files("*.cs").SelectMany(f => File.ReadLines(f).Where(l => Namespaces.Any(n => l.StartsWith(n, StringComparison.Ordinal))).Select(l => $"{Path.GetRelativePath(RepositoryPaths.Root, f)}: {l}")).ToList();
        found.ShouldBeEmpty();
    }
}
