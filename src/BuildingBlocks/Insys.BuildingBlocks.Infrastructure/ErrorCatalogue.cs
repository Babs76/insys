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
using System.Reflection;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Infrastructure;

/// <summary>
/// The error catalogue read from the [ErrorCode] constants of every INSYS assembly next to the host, as db/migrations/R__error_code_catalogue.sql is
/// generated from them (the constants are the single source). Built once, on first use.
/// </summary>
public sealed class ErrorCatalogue : IErrorCatalogue
{
    private readonly Lazy<IReadOnlyDictionary<string, ErrorCodeEntry>> _entries = new(Load);

    public ErrorCodeEntry? Find(string code) => _entries.Value.GetValueOrDefault(code);

    public IReadOnlyList<ErrorCodeEntry> All => [.. _entries.Value.Values.OrderBy(e => e.Code, StringComparer.Ordinal)];

    private static IReadOnlyDictionary<string, ErrorCodeEntry> Load()
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "Insys.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && !n.Contains(' ', StringComparison.Ordinal) && !n.EndsWith("Tests", StringComparison.Ordinal) && n != "Insys.Testing")
            .Select(n => Assembly.Load(n!));
        var types = assemblies.SelectMany(a => a.GetTypes()).Where(t => t is { IsAbstract: true, IsSealed: true } || t.IsClass);
        return ErrorCodes.Describe(types).GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }
}
