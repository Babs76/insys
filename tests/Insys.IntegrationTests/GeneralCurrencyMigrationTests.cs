/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using Insys.DbMigrator;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-033, migration V0072: assessments, recoveries and endorsements made before it take their policy's currency, so every
/// amount they hold names the currency it was written in; a risk object's valuation has none until one is given.
/// </summary>
public sealed class GeneralCurrencyMigrationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ExistingRecords_TakeTheirPolicysCurrency_V0072()
    {
        var connection = await fixture.Server.CreateEmptyDatabase();
        var directory = Path.Combine(Path.GetTempPath(), $"insys-v0072-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(RepositoryPaths.Root, "db", "migrations");
        foreach (var file in Directory.GetFiles(source, "V*.sql").Where(f => string.CompareOrdinal(Path.GetFileName(f), "V0072") < 0))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        var migrated = await new MigrationRunner(connection).Migrate(directory);
        var database = new TestDatabase("v0072", connection, connection, migrated);
        var rows = new RowFiller(database, DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        var records = new List<(string Table, Guid Id, string Currency)>();
        foreach (var currency in new[] { "ZAR", "USD" })
        {
            var policy = await rows.Insert(tenant, "general.policy", ("currency", currency));
            var claim = await rows.Insert(tenant, "general.claim", ("policy_id", policy), ("status", "REGISTERED"));
            records.Add(("general.loss_assessment", await rows.Insert(tenant, "general.loss_assessment", ("claim_id", claim), ("status", "ASSIGNED")), currency));
            records.Add(("general.recovery_case", await rows.Insert(tenant, "general.recovery_case", ("claim_id", claim), ("status", "OPEN")), currency));
            records.Add(("general.endorsement", await rows.Insert(tenant, "general.endorsement", ("policy_id", policy), ("status", "DRAFT"),
                ("change_set", System.Text.Json.JsonDocument.Parse("[]").RootElement)), currency));
        }

        var risk = await rows.Insert(tenant, "general.risk_object", ("valuation", 100_000m), ("valuation_basis", "MARKET"), ("status", "ACTIVE"),
            ("attributes", System.Text.Json.JsonDocument.Parse("{}").RootElement));
        File.Copy(Directory.GetFiles(source, "V0072__*.sql").Single(), Path.Combine(directory, "V0072__general_amount_currencies.sql"));
        await new MigrationRunner(connection).Migrate(directory);

        foreach (var (table, id, currency) in records)
        {
            (await database.ScalarAsOwner<string>($"SELECT currency FROM {table} WHERE id = @id", ("id", id))).ShouldBe(currency, table);
        }

        (await database.ScalarAsOwner<string?>("SELECT valuation_currency FROM general.risk_object WHERE id = @id", ("id", risk))).ShouldBeNull();
    }

    [Fact]
    public async Task ExistingValuations_TakeTheTenantsBaseCurrency_V0074()
    {
        var (connection, directory, database) = await MigratedBefore("V0074");
        var rows = new RowFiller(database, DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        await database.SeedStaticFilled(tenant, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE", DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1),
            ("base_currency", "USD"), ("languages", new[] { "en" }), ("default_language", "en"), ("time_zone", "UTC"), ("country", "US"));
        var valued = await rows.Insert(tenant, "general.risk_object", ("valuation", 100_000m), ("valuation_basis", "MARKET"), ("status", "ACTIVE"),
            ("attributes", System.Text.Json.JsonDocument.Parse("{}").RootElement));
        var unvalued = await rows.Insert(tenant, "general.risk_object", ("status", "ACTIVE"), ("attributes", System.Text.Json.JsonDocument.Parse("{}").RootElement));
        await new MigrationRunner(connection).Migrate(WithMigration(directory, "V0074"));

        (await database.ScalarAsOwner<string?>("SELECT valuation_currency FROM general.risk_object WHERE id = @id", ("id", valued))).ShouldBe("USD");
        (await database.ScalarAsOwner<string?>("SELECT valuation_currency FROM general.risk_object WHERE id = @id", ("id", unvalued))).ShouldBeNull();
        await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsOwner(
            "UPDATE general.risk_object SET valuation_currency = NULL WHERE id = @id", ("id", valued)));
    }

    [Fact]
    public async Task AValuationWithNoBaseCurrencyToTake_StopsTheMigration_V0074()
    {
        var (connection, directory, database) = await MigratedBefore("V0074");
        await new RowFiller(database, DateTimeOffset.UtcNow).Insert(Guid.NewGuid(), "general.risk_object", ("valuation", 100_000m), ("valuation_basis", "MARKET"),
            ("status", "ACTIVE"), ("attributes", System.Text.Json.JsonDocument.Parse("{}").RootElement));

        (await Should.ThrowAsync<Exception>(() => new MigrationRunner(connection).Migrate(WithMigration(directory, "V0074")))).ToString().ShouldContain("no locale defaults");
    }

    private async Task<(string Connection, string Directory, TestDatabase Database)> MigratedBefore(string version)
    {
        var connection = await fixture.Server.CreateEmptyDatabase();
        var directory = Path.Combine(Path.GetTempPath(), $"insys-{version.ToLowerInvariant()}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(Source, "V*.sql").Where(f => string.CompareOrdinal(Path.GetFileName(f), version) < 0))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        var migrated = await new MigrationRunner(connection).Migrate(directory);
        return (connection, directory, new TestDatabase(version.ToLowerInvariant(), connection, connection, migrated));
    }

    private static string WithMigration(string directory, string version)
    {
        var file = Directory.GetFiles(Source, $"{version}__*.sql").Single();
        File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return directory;
    }

    private static string Source => Path.Combine(RepositoryPaths.Root, "db", "migrations");
}
