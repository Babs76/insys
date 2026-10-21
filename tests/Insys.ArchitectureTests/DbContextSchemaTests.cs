using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ArchitectureTests;

/// <summary>No DbContext maps tables outside its own module schema (DBA-004, DBA-005, SRS §21.3).</summary>
public sealed class DbContextSchemaTests
{
    public static TheoryData<string> ModuleContexts
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var type in ContextTypes)
            {
                data.Add(type.FullName!);
            }

            return data;
        }
    }

    private static IEnumerable<Type> ContextTypes => Solution.Assemblies.SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(ModuleDbContext)));

    [Fact]
    public void EveryModule_HasADbContext() => ContextTypes.Count().ShouldBe(22);

    [Fact]
    public void OwnedSchemas_MatchTheModuleRoles_AndNoSchemaHasTwoOwners_DBA004()
    {
        var roles = File.ReadAllLines(Path.Combine(Insys.Testing.RepositoryPaths.Roles, "module_roles.csv")).Skip(1)
            .Select(l => l.Split(','))
            .ToDictionary(p => p[2], p => p[1].Split(' ').OrderBy(s => s).ToArray());
        foreach (var type in ContextTypes)
        {
            using var context = Create(type);
            var module = type.Namespace!.Split('.')[1];
            string.Join(",", context.OwnedSchemas.OrderBy(s => s)).ShouldBe(string.Join(",", roles[module]), $"{module} owns different schemas from its database role.");
        }
    }

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void DbContext_MapsOnlyItsOwnSchema_DBA004(string contextType)
    {
        using var context = Create(ContextTypes.Single(t => t.FullName == contextType));

        var foreign = context.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && !context.OwnedSchemas.Contains(e.GetSchema()!))
            .Select(e => $"{e.GetSchema()}.{e.GetTableName()}")
            .ToList();

        foreign.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void DbContext_MapsTheModuleOutbox_DBA006(string contextType)
    {
        using var context = Create(ContextTypes.Single(t => t.FullName == contextType));

        context.Model.FindEntityType(typeof(OutboxMessage))!.GetTableName().ShouldBe("outbox_event");
    }

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void DbContext_TenantScopedEntities_HaveTenantFilter_DBA001(string contextType)
    {
        using var context = Create(ContextTypes.Single(t => t.FullName == contextType));

        var unfiltered = context.Model.GetEntityTypes()
            .Where(e => e.FindProperty("TenantId") is not null && e.FindDeclaredQueryFilter(ModuleDbContext.TenantFilterKey) is null)
            .Select(e => e.Name)
            .ToList();

        unfiltered.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void DbContext_RowVersion_IsConcurrencyToken_DBA011(string contextType)
    {
        using var context = Create(ContextTypes.Single(t => t.FullName == contextType));

        context.Model.GetEntityTypes().Select(e => e.FindProperty("RowVersion")).Where(p => p is not null)
            .ShouldAllBe(p => p!.IsConcurrencyToken);
    }

    private static ModuleDbContext Create(Type contextType)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scope = new ExecutionScope();
        var dependencies = new ModuleDbContextDependencies(scope, scope, new SystemClock(TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new PlatformOptions()), services);
        var optionsType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsType)!;
        builder.UseNpgsql("Host=model-only", npgsql => npgsql.UseNodaTime());
        return (ModuleDbContext)Activator.CreateInstance(contextType, builder.Options, dependencies)!;
    }
}
