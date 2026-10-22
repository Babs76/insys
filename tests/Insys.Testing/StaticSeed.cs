namespace Insys.Testing;

/// <summary>Static data rows for tests, written as the owner. A row already there (same code, overlapping dates) is left as it is.</summary>
public static class StaticSeed
{
    public static Task SeedStatic(this TestDatabase database, Guid tenantId, string table, string recordStatus, string code, DateOnly from,
        params (string Column, object Value)[] fields) =>
        database.SeedStatic(tenantId, table, recordStatus, code, 1, from, null, fields);

    public static Task SeedStatic(this TestDatabase database, Guid tenantId, string table, string recordStatus, string code, int version, DateOnly from, DateOnly? to,
        params (string Column, object Value)[] fields)
    {
        var columns = string.Join(", ", fields.Select(f => f.Column));
        var values = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        return database.ExecuteAsOwner($"""
            INSERT INTO {table} (tenant_id, code, version, effective_from, effective_to, record_status, created_by, change_reason{(fields.Length == 0 ? "" : ", " + columns)})
            VALUES (@t, @code, @version, @from, @to, @status, 'seed', 'test seed'{(fields.Length == 0 ? "" : ", " + values)})
            ON CONFLICT DO NOTHING
            """,
            [("t", tenantId), ("code", code), ("version", version), ("from", from), ("to", (object?)to ?? DBNull.Value), ("status", recordStatus),
             .. fields.Select((f, i) => ($"p{i}", f.Value))]);
    }
}
