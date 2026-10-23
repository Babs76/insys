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
namespace Insys.Testing;

/// <summary>Static data rows for tests, written as the owner. A row already there (same code, overlapping dates) is left as it is.</summary>
public static class StaticSeed
{
    public static Task SeedStatic(this TestDatabase database, Guid tenantId, string table, string recordStatus, string code, DateOnly from,
        params (string Column, object Value)[] fields) =>
        database.SeedStatic(tenantId, table, recordStatus, code, 1, from, null, fields);

    /// <summary>A platform-global row (tenant_id NULL), for tables whose rows are global only (scope G).</summary>
    public static Task SeedGlobalStatic(this TestDatabase database, string table, string recordStatus, string code, DateOnly from, params (string Column, object Value)[] fields) =>
        database.SeedStatic(null, table, recordStatus, code, 1, from, null, fields);

    public static Task SeedStatic(this TestDatabase database, Guid? tenantId, string table, string recordStatus, string code, int version, DateOnly from, DateOnly? to,
        params (string Column, object Value)[] fields)
    {
        var columns = string.Join(", ", fields.Select(f => f.Column));
        var values = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        return database.ExecuteAsOwner($"""
            INSERT INTO {table} (tenant_id, code, version, effective_from, effective_to, record_status, created_by, change_reason{(fields.Length == 0 ? "" : ", " + columns)})
            VALUES (@t, @code, @version, @from, @to, @status, 'seed', 'test seed'{(fields.Length == 0 ? "" : ", " + values)})
            ON CONFLICT DO NOTHING
            """,
            [("t", (object?)tenantId ?? DBNull.Value), ("code", code), ("version", version), ("from", from), ("to", (object?)to ?? DBNull.Value), ("status", recordStatus),
             .. fields.Select((f, i) => ($"p{i}", f.Value))]);
    }
}
