/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using System.Text;
using Insys.BuildingBlocks.Domain;

namespace Insys.Testing;

/// <summary>
/// Generates db/migrations/R__error_code_catalogue.sql from every [ErrorCode] constant in the solution, so the
/// constants in code are the single source of the error code catalogue (item 8). Each code is inserted as version 1
/// of a global ACTIVE row if it is missing; existing rows are never changed (approved versions are immutable, SDM-002).
/// </summary>
public static class ErrorCodeSeed
{
    public static string FilePath => Path.Combine(RepositoryPaths.Migrations, "R__error_code_catalogue.sql");

    public static IReadOnlyList<ErrorCodeEntry> Entries()
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "Insys.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && !n.Contains(' ', StringComparison.Ordinal) && !n.EndsWith("Tests", StringComparison.Ordinal) && n != "Insys.Testing")
            .Select(n => Assembly.Load(n!));
        var types = assemblies.SelectMany(a => a.GetTypes()).Where(t => t is { IsAbstract: true, IsSealed: true } || t.IsClass);
        var entries = ErrorCodes.Describe(types);
        var duplicates = entries.GroupBy(e => e.Code).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        return duplicates.Count > 0 ? throw new InvalidOperationException($"Duplicate error codes: {string.Join(", ", duplicates)}") : entries;
    }

    public static string Generate()
    {
        var s = new StringBuilder(SourceHeader.Render("Migrations"));
        s.Append("""
            -- =====================================================================
            -- GENERATED from the [ErrorCode] constants in the source code; do not edit.
            -- Regenerate with: INSYS_UPDATE_SNAPSHOTS=1 dotnet test --filter-class "*ErrorCodeCatalogueTests"
            -- Repeatable script: the migration runner re-runs it when its content changes.
            -- Missing codes are added as version 1, global (tenant_id NULL) and ACTIVE. Existing rows are not
            -- changed; a changed status or message for an existing code needs a new version (SDM-002).
            -- Runs as the owner; global rows are platform data (V0002 policies).
            -- =====================================================================
            INSERT INTO integration.ref_error_code_catalogue
              (tenant_id, code, version, effective_from, record_status, description, record_source, record_class, created_by,
               approved_at, approved_by, change_reason, error_code, http_status, message_key, retryable)
            VALUES

            """);
        var entries = Entries();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            s.Append($"  (NULL, '{e.Code}', 1, DATE '2026-10-01', 'ACTIVE', '{e.Description}', 'insys-core', 'PUBLIC', 'migration', now(), 'migration', "
                + $"'Seeded from code', '{e.Code}', {e.HttpStatus}, '{e.MessageKey}', {(e.Retryable ? "true" : "false")}){(i < entries.Count - 1 ? "," : "")}\n");
        }

        s.Append("ON CONFLICT ON CONSTRAINT ref_error_code_catalogue_bkey DO NOTHING;\n");
        return s.ToString();
    }
}
