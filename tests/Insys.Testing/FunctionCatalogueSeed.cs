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
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Testing;

/// <summary>
/// Generates db/migrations/R__function_catalogue.sql (ADR-026) from the code: the taxonomy (BusinessTaxonomy) and every
/// function the modules declare, with the permissions their operations require, as the router built them. Read it only
/// after a host has built its endpoints. A function that changed gets a new version from the day the script runs, one
/// removed is closed; nothing is updated in place (SDM-002). Then the roles, SoD rules and channel rows still holding
/// permissions are converted (iam.adopt_function_catalogue, idempotent).
/// </summary>
public static class FunctionCatalogueSeed
{
    private const string FirstDay = "DATE '2026-10-01'";

    public static string FilePath => Path.Combine(RepositoryPaths.Migrations, "R__function_catalogue.sql");

    public static string Generate()
    {
        var functions = FunctionRegistry.All();
        var unknown = functions.Select(f => f.Function.SubDomain).Distinct().Except(BusinessTaxonomy.SubDomains(functions.Select(f => f.Function.SubDomain))
            .Select(d => d.Code)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"Functions name sub-domains the taxonomy does not have: {string.Join(", ", unknown)}");
        }

        var s = new StringBuilder(SourceHeader.Render("Migrations"));
        s.Append("""
            -- =====================================================================
            -- GENERATED from the code's function declarations and BusinessTaxonomy; do not edit.
            -- Regenerate with: INSYS_UPDATE_SNAPSHOTS=1 dotnet test --filter-class "*FunctionCatalogueTests"
            -- Repeatable script: the migration runner re-runs it when its content changes.
            -- New codes are added as version 1 from 1 October 2026, global and ACTIVE. A row that changed gets a new
            -- version from today and the old one is closed; a code no longer declared is closed (SDM-002).
            -- Then iam.adopt_function_catalogue() converts what still holds permissions (ADR-026 §7).
            -- =====================================================================

            """);

        Table(s, "iam.ref_business_domains", ["domain_code", "icon"], BusinessTaxonomy.Domains.Select(d =>
            new Row(d.Code, d.Label, d.Order, [Text(d.Code), Text(d.Icon)])));
        Table(s, "iam.ref_business_sub_domains", ["sub_domain_code", "domain_code", "icon"],
            BusinessTaxonomy.SubDomains(functions.Select(f => f.Function.SubDomain)).Select(d =>
                new Row(d.Code, d.Label, d.Order, [Text(d.Code), Text(d.DomainCode), Text(d.Icon)])));
        Table(s, "iam.ref_function_catalogue",
            ["function_code", "sub_domain_code", "process_area", "interaction_type", "function_kind", "parent_function_code", "audiences", "route_key",
             "required_permissions", "required_feature", "privileged", "allowed_while_onboarding"],
            functions.Select(f => new Row(f.Function.Code, Label(f.Function.Code), f.Order,
            [
                Text(f.Function.Code), Text(f.Function.SubDomain), Text(f.Function.ProcessArea), Text(f.Function.InteractionType),
                Text(f.Function.Kind.ToString().ToUpperInvariant()), Text(f.Function.Parent), Array(f.Function.Audiences), Text(f.Function.RouteKey),
                Array(f.Permissions), Text(f.Function.Feature), Bool(f.Function.Privileged), Bool(f.AllowedWhileOnboarding),
            ])));

        s.Append("SELECT iam.adopt_function_catalogue();\n");
        return s.ToString();
    }

    private sealed record Row(string Code, string Label, int Order, IReadOnlyList<string> Values);

    private static void Table(StringBuilder s, string table, IReadOnlyList<string> columns, IEnumerable<Row> rows)
    {
        var temp = "release_" + table[(table.IndexOf('.') + 1)..];
        var all = string.Join(", ", columns);
        var compared = string.Join(", ", columns.Select(c => "r." + c));
        var current = string.Join(", ", columns.Select(c => "c." + c));
        s.Append($"-- {table}\n");
        s.Append($"DROP TABLE IF EXISTS pg_temp.{temp};\n");
        s.Append($"CREATE TEMP TABLE {temp} AS SELECT code, labels, sort_order, {all} FROM {table} WITH NO DATA;\n");
        s.Append($"INSERT INTO {temp} (code, labels, sort_order, {all}) VALUES\n");
        s.Append(string.Join(",\n", rows.Select(r =>
            $"  ({Text(r.Code)}, {Text(JsonSerializer.Serialize(new Dictionary<string, string> { ["en"] = r.Label }))}::jsonb, {r.Order}, {string.Join(", ", r.Values)})")));
        s.Append(";\n");
        s.Append($"""
            UPDATE {table} c
               SET effective_to = CASE WHEN c.effective_from < current_date THEN current_date END,
                   record_status = CASE WHEN c.effective_from < current_date THEN c.record_status ELSE 'RETIRED' END
             WHERE c.tenant_id IS NULL AND c.record_status = 'ACTIVE' AND c.effective_to IS NULL
               AND NOT EXISTS (SELECT 1 FROM {temp} r WHERE r.code = c.code
                               AND ROW(r.labels, r.sort_order, {compared}) IS NOT DISTINCT FROM ROW(c.labels, c.sort_order, {current}));
            INSERT INTO {table} (tenant_id, code, version, effective_from, record_status, record_source, record_class, created_by, approved_at,
                                 approved_by, change_reason, labels, sort_order, {all})
            SELECT NULL, r.code, COALESCE(v.latest, 0) + 1, CASE WHEN v.latest IS NULL THEN {FirstDay} ELSE current_date END, 'ACTIVE', 'insys-core',
                   'PUBLIC', 'migration', now(), 'migration', 'Generated from the code''s declarations (ADR-026)', r.labels, r.sort_order, {compared}
              FROM {temp} r LEFT JOIN (SELECT code, max(version) AS latest FROM {table} WHERE tenant_id IS NULL GROUP BY code) v ON v.code = r.code
             WHERE NOT EXISTS (SELECT 1 FROM {table} c WHERE c.tenant_id IS NULL AND c.code = r.code AND c.record_status = 'ACTIVE' AND c.effective_to IS NULL);
            DROP TABLE pg_temp.{temp};


            """);
    }

    /// <summary>The English label of a function, from its code (LIFE_POLICY_VIEW: "Life policy view"); other languages come through labels.</summary>
    public static string Label(string code)
    {
        var words = code.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
        words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(' ', words);
    }

    private static string Text(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Array(IReadOnlyList<string> values) =>
        values.Count == 0 ? "ARRAY[]::text[]" : $"ARRAY[{string.Join(", ", values.Select(Text))}]::text[]";

    private static string Bool(bool value) => value ? "true" : "false";
}
