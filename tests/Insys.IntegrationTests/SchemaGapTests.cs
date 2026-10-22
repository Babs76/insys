using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>V0012: the schema gaps found by auditing V0001 against domain-model.json (Phase 2 review item 3).</summary>
public sealed class SchemaGapTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Theory]
    [InlineData("claims", "claim_payment_instruction", "PENDING_APPROVAL")]
    [InlineData("health", "preauthorisation", "CONDITIONALLY_APPROVED")]
    [InlineData("health", "medical_claim", "APPROVED_FOR_PAYMENT")]
    [InlineData("general", "claim", "FIRST_NOTIFICATION")]
    public async Task StatusColumns_HoldTheirLongestState(string schema, string table, string state)
    {
        (await fixture.Database.ScalarAsOwner<int>(
            "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND column_name = 'status'",
            ("s", schema), ("t", table))).ShouldBe(30);
        (await Check(schema, table)).ShouldContain(state);
    }

    [Fact]
    public async Task LossAssessment_HasItsFiguresFromReportedOnwards()
    {
        var definition = await Check("general", "loss_assessment", "ck_loss_assessment_reported");
        definition.ShouldContain("assessed_amount IS NOT NULL");
        (await fixture.Database.ScalarAsOwner<string>(
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'general' AND table_name = 'loss_assessment' AND column_name = 'assessed_at'"))
            .ShouldBe("YES");
    }

    [Fact]
    public async Task AccountingPeriods_AllowOneOpenPeriodPerDate()
    {
        await Period("2026-10", "2026-10-01", "2026-10-31", "OPEN");
        await Period("2026-09", "2026-09-01", "2026-09-30", "OPEN");
        var overlap = await Should.ThrowAsync<PostgresException>(() => Period("P10B", "2026-10-15", "2026-11-14", "OPEN"));
        overlap.SqlState.ShouldBe(PostgresErrorCodes.ExclusionViolation);
        await Period("P10C", "2026-10-15", "2026-11-14", "FUTURE");

        var closedWithoutCloser = await Should.ThrowAsync<PostgresException>(() => Period("2026-08", "2026-08-01", "2026-08-31", "CLOSED"));
        closedWithoutCloser.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task DeprecatedStatusColumns_AreNullable_ADR010()
    {
        foreach (var (schema, table) in new[]
                 {
                     ("product", "cfg_product_definitions_and_versions"), ("integration", "cfg_api_catalogue_entries"),
                     ("integration", "cfg_event_schema_versions"), ("finance", "cfg_accounting_calendar_and_periods"),
                 })
        {
            (await fixture.Database.ScalarAsOwner<string>(
                "SELECT is_nullable FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND column_name = 'status'",
                ("s", schema), ("t", table))).ShouldBe("YES", $"{schema}.{table}.status");
        }
    }

    private Task Period(string code, string start, string end, string status) =>
        fixture.Database.ExecuteAsOwner("""
            INSERT INTO finance.accounting_period (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, period_code, start_date, end_date, status)
            VALUES (@t, gen_random_uuid(), 1, now(), 'test', now(), 'test', @c, @s::date, @e::date, @st)
            """, ("t", DatabaseFixture.TenantA), ("c", code), ("s", start), ("e", end), ("st", status));

    private async Task<string> Check(string schema, string table, string? name = null) =>
        await fixture.Database.ScalarAsOwner<string>($"""
            SELECT string_agg(pg_get_constraintdef(c.oid), ' ') FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = @s AND t.relname = @t AND c.contype = 'c' {(name is null ? "AND c.conname LIKE '%status'" : "AND c.conname = @n")}
            """, ("s", schema), ("t", table), ("n", name ?? "")) ?? "";
}
