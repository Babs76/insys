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
