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
using Npgsql;

namespace Insys.Testing;

/// <summary>
/// Inserts a row as the owner with only the columns a test cares about: the given values, a filled parent row for each
/// required foreign key, and for every other required column with no default a value of its type. For records whose
/// module flow is not what the test is about (a policy that only needs to exist and be linked to a party, say).
/// </summary>
public sealed class RowFiller(TestDatabase database, DateTimeOffset when, IReadOnlyDictionary<string, (string Column, object Value)[]>? defaults = null)
{
    private readonly DateOnly _day = DateOnly.FromDateTime(when.UtcDateTime);

    /// <summary>Values tables' checks need where a type's default would not do, with <c>when</c> as the record's time.</summary>
    public static Dictionary<string, (string Column, object Value)[]> CommonDefaults(DateOnly day) => new(StringComparer.Ordinal)
    {
        ["general.policy"] = [("status", "IN_FORCE"), ("cover_starts_on", "ISSUE"), ("period_start", day.AddYears(-31))],
        ["life.proposal"] = [("status", "DRAFT")],
        ["document.document"] = [("status", "GENERATED")],
        ["general.claim"] = [("claim_number", "GC-")],
        ["payment.payment"] = [("direction", "OUT")],
    };

    /// <summary>Inserts the row and returns its id.</summary>
    public async Task<Guid> Insert(Guid tenant, string table, params (string Column, object Value)[] values)
    {
        var id = Guid.CreateVersion7();
        var given = new Dictionary<string, object>(StringComparer.Ordinal) { ["tenant_id"] = tenant, ["id"] = id };
        foreach (var (column, value) in (defaults ?? CommonDefaults(_day)).GetValueOrDefault(table, []).Concat(values))
        {
            given[column] = column == "claim_number" && value is string number && number.StartsWith("GC-", StringComparison.Ordinal)
                ? "GC-" + Guid.NewGuid().ToString("N")[..10] : value;
        }

        var parts = table.Split('.');
        var required = new List<(string Name, string Type, int? Length)>();
        var parents = new List<(string Column, string Table)>();
        await using (var connection = new NpgsqlConnection(database.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using (var columns = new NpgsqlCommand("""
                SELECT column_name, data_type, character_maximum_length FROM information_schema.columns
                WHERE table_schema = @s AND table_name = @n AND is_nullable = 'NO' AND column_default IS NULL
                """, connection))
            {
                columns.Parameters.AddWithValue("s", parts[0]);
                columns.Parameters.AddWithValue("n", parts[1]);
                await using var reader = await columns.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    required.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
                }
            }

            // Each foreign key on (tenant_id, column) to another table's (tenant_id, id).
            await using (var keys = new NpgsqlCommand("""
                SELECT a.attname, c.confrelid::regclass::text FROM pg_constraint c
                JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[2]
                WHERE c.contype = 'f' AND c.conrelid = (@s || '.' || @n)::regclass AND array_length(c.conkey, 1) = 2 AND c.confrelid <> c.conrelid
                """, connection))
            {
                keys.Parameters.AddWithValue("s", parts[0]);
                keys.Parameters.AddWithValue("n", parts[1]);
                await using var reader = await keys.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    parents.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        foreach (var (column, parent) in parents.Where(p => !given.ContainsKey(p.Column) && required.Any(r => r.Name == p.Column)))
        {
            given[column] = await Insert(tenant, parent);
        }

        foreach (var (name, type, length) in required.Where(c => !given.ContainsKey(c.Name)))
        {
            given[name] = type switch
            {
                "uuid" => Guid.CreateVersion7(),
                "date" => _day.AddYears(-30),
                "timestamp with time zone" => when.AddYears(-30),
                "boolean" => false,
                "integer" or "smallint" => 1,
                "bigint" => 1L,
                "numeric" => 1m,
                "character" when length == 3 => "ZAR",
                _ => ("T" + Guid.NewGuid().ToString("N"))[..Math.Min(length ?? 12, 12)],
            };
        }

        await using var insertConnection = new NpgsqlConnection(database.OwnerConnectionString);
        await insertConnection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO {table} ({string.Join(", ", given.Keys)}) VALUES ({string.Join(", ", given.Keys.Select((_, i) => $"@p{i}"))})", insertConnection);
        foreach (var (value, i) in given.Values.Select((v, i) => (v, i)))
        {
            insert.Parameters.AddWithValue($"p{i}", value);
        }

        await insert.ExecuteNonQueryAsync();
        return id;
    }
}
