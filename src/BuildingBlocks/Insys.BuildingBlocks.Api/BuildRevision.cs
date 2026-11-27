/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
using System.Text.RegularExpressions;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// The commit this API was built from (Ade, 27 November 2026), so any environment shows exactly what is running. The build
/// records it as the source revision: the SDK takes it from git, and the image build, which has no .git, passes it as
/// <c>-p:SourceRevisionId</c> (deploy/docker/api.Dockerfile). It is the part of the informational version after '+'; anything
/// other than a full 40-character commit is not shown.
/// </summary>
public static partial class BuildRevision
{
    /// <summary>
    /// The port /health/version answers on (Insys:ManagementPort): the image listens on it besides 8080, and no Service or ingress
    /// exposes it, so it is reachable only inside the pod's network, never from the internet.
    /// </summary>
    public const int ManagementPort = 8081;

    /// <summary>The full commit, lower case, or null when the build recorded none.</summary>
    public static string? Commit { get; } = From(typeof(BuildRevision).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The commit in an informational version such as 1.0.0+&lt;commit&gt;, or null.</summary>
    public static string? From(string? informationalVersion)
    {
        var plus = informationalVersion?.LastIndexOf('+') ?? -1;
        var revision = plus < 0 ? null : informationalVersion![(plus + 1)..];
        return revision is not null && Commit40().IsMatch(revision) ? revision : null;
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Commit40();
}
