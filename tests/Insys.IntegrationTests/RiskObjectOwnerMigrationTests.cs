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

namespace Insys.IntegrationTests;

/// <summary>
/// BR-092, migration V0077: a risk object belongs to a client. Existing items get an owner only where it is certain:
/// every policy the item is on has the same policyholder, and no quote for it names anyone else. Nothing is guessed.
/// </summary>
public sealed class RiskObjectOwnerMigrationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task AnItemOnlyOnOnePolicyholdersPolicies_IsTheirs_AndAnyOtherKeepsNoOwner_BR092()
    {
        var connection = await fixture.Server.CreateEmptyDatabase();
        var directory = Path.Combine(Path.GetTempPath(), $"insys-v0077-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(RepositoryPaths.Root, "db", "migrations");
        foreach (var file in Directory.GetFiles(source, "V*.sql").Where(f => string.CompareOrdinal(Path.GetFileName(f), "V0077") < 0))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        var database = new TestDatabase("v0077", connection, connection, await new MigrationRunner(connection).Migrate(directory));
        var rows = new RowFiller(database, DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        var (holder, other) = (Guid.NewGuid(), Guid.NewGuid());

        async Task<Guid> Item() => await rows.Insert(tenant, "general.risk_object", ("status", "ACTIVE"), ("attributes", System.Text.Json.JsonDocument.Parse("{}").RootElement));

        async Task OnPolicy(Guid item, Guid policyholder)
        {
            var policy = await rows.Insert(tenant, "general.policy", ("policyholder_party_id", policyholder), ("currency", "ZAR"));
            await rows.Insert(tenant, "general.policy_risk", ("policy_id", policy), ("risk_object_id", item), ("sum_insured", 100_000m));
        }

        async Task OnQuote(Guid item, Guid proposer)
        {
            var quote = await rows.Insert(tenant, "general.quote", ("proposer_party_id", proposer), ("currency", "ZAR"), ("status", "DRAFT"));
            await rows.Insert(tenant, "general.quote_risk", ("quote_id", quote), ("risk_object_id", item), ("sum_insured", 100_000m));
        }

        var renewed = await Item();
        await OnPolicy(renewed, holder);
        await OnPolicy(renewed, holder);
        await OnQuote(renewed, holder);
        var shared = await Item();
        await OnPolicy(shared, holder);
        await OnPolicy(shared, other);
        var quotedForAnother = await Item();
        await OnPolicy(quotedForAnother, holder);
        await OnQuote(quotedForAnother, other);
        var neverInsured = await Item();
        await OnQuote(neverInsured, holder);

        File.Copy(Directory.GetFiles(source, "V0077__*.sql").Single(), Path.Combine(directory, "V0077__risk_object_owner.sql"));
        await new MigrationRunner(connection).Migrate(directory);

        async Task<Guid?> Owner(Guid item) => await database.ScalarAsOwner<Guid?>("SELECT owner_party_id FROM general.risk_object WHERE id = @id", ("id", item));
        (await Owner(renewed)).ShouldBe(holder);
        (await Owner(shared)).ShouldBeNull("two policyholders");
        (await Owner(quotedForAnother)).ShouldBeNull("quoted for someone else");
        (await Owner(neverInsured)).ShouldBeNull("on no policy");
    }
}
