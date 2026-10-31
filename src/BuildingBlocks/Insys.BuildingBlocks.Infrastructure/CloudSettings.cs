/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using Microsoft.Extensions.Configuration;

namespace Insys.BuildingBlocks.Infrastructure;

/// <summary>
/// The neutral names for what a deployment runs on (ADR-027, phase 1): Kms:Provider and Kms:*, ObjectStore:Provider and ObjectStore:*. The names they replace work for one release, with a warning at
/// start-up; where both are set, the neutral name wins.
/// </summary>
public static class CloudSettings
{
    public const string LocalKms = "local";
    public const string Dew = "dew";
    public const string S3Compatible = "s3-compatible";

    /// <summary>Named by ADR-027 and built in its phase 3, when a customer chooses the cloud. Naming one now fails at start-up rather than running without it.</summary>
    private static readonly string[] Unbuilt = ["aws", "azure", "alibaba", "vault", "azure-blob"];

    /// <summary>The key service for envelope encryption.</summary>
    public static string KmsProvider(IConfiguration configuration) =>
        Known((configuration["Kms:Provider"] ?? configuration["FieldProtection:Envelope:Kms"] ?? LocalKms).ToLowerInvariant(), "Kms:Provider", LocalKms, Dew);

    /// <summary>The object store documents are kept in; null when they stay on the file system.</summary>
    public static string? ObjectStoreProvider(IConfiguration configuration)
    {
        if (configuration["ObjectStore:Provider"] is { Length: > 0 } provider)
        {
            return Known(provider.ToLowerInvariant(), "ObjectStore:Provider", S3Compatible);
        }

        return string.Equals(configuration["Documents:Storage:Provider"], "object-store", StringComparison.OrdinalIgnoreCase) ? S3Compatible : null;
    }

    /// <summary>The replaced names this configuration still uses.</summary>
    public static IReadOnlyList<string> OldNamesUsed(IConfiguration configuration)
    {
        var used = new List<string>();
        if (configuration["FieldProtection:Envelope:Kms"] is not null)
        {
            used.Add("FieldProtection:Envelope:Kms, now Kms:Provider");
        }

        if (string.Equals(configuration["Documents:Storage:Provider"], "object-store", StringComparison.OrdinalIgnoreCase))
        {
            used.Add("Documents:Storage:Provider, now ObjectStore:Provider");
        }

        if (configuration.GetSection("Huawei:Kms").Exists())
        {
            used.Add("Huawei:Kms, now Kms");
        }

        if (configuration.GetSection("Huawei:Obs").Exists())
        {
            used.Add("Huawei:Obs, now ObjectStore");
        }

        return used;
    }

    private static string Known(string provider, string setting, params string[] built)
    {
        if (built.Contains(provider, StringComparer.Ordinal))
        {
            return provider;
        }

        throw new InvalidOperationException(Unbuilt.Contains(provider, StringComparer.Ordinal)
            ? $"{setting} \"{provider}\" has no adapter yet: ADR-027 builds it when a customer chooses that cloud."
            : $"{setting} \"{provider}\" isn't known. It can be: {string.Join(", ", built)}.");
    }
}
