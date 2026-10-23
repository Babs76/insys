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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>Every column a module's DbContext maps exists in the migrated schema, so a mapping never writes a column the migrations do not create.</summary>
public sealed class DbContextColumnsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task EveryMappedColumn_ExistsInTheDatabase()
    {
        await using var factory = new InsysApiFactory(fixture.Database);
        using var scope = factory.Services.CreateScope();
        var contexts = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Insys.", StringComparison.Ordinal) == true)
            .SelectMany(a => a.GetTypes()).Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(ModuleDbContext))).ToList();
        contexts.Count.ShouldBeGreaterThanOrEqualTo(22);
        var missing = new List<string>();
        foreach (var type in contexts)
        {
            // Test-only contexts are not registered in the host.
            if (scope.ServiceProvider.GetService(type) is not DbContext context)
            {
                continue;
            }

            foreach (var entity in context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
            {
                var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                var columns = await Columns(entity.GetSchema()!, entity.GetTableName()!);
                missing.AddRange(entity.GetProperties().Select(p => p.GetColumnName(table)).Where(c => c is not null && !columns.Contains(c))
                    .Select(c => $"{entity.GetSchema()}.{entity.GetTableName()}.{c} ({type.Name})"));
            }
        }

        missing.ShouldBeEmpty();
    }

    private async Task<HashSet<string>> Columns(string schema, string table) =>
        [.. (await fixture.Database.ScalarAsOwner<string>(
            "SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_schema = @s AND table_name = @t", ("s", schema), ("t", table)) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)];
}
