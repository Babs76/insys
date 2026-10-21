using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Rules.Domain.Decisions;
using Insys.Rules.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Rules.Infrastructure.Persistence;

/// <summary>The Rules module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class RulesDbContext(DbContextOptions<RulesDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "rules";

    public override string Schema => SchemaName;

    public DbSet<RuleDecision> Decisions => Set<RuleDecision>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        RulesStaticData.Configure(modelBuilder);

        // decision_log is partitioned by evaluated_at, which is part of its key; rows are never updated.
        modelBuilder.Entity<RuleDecision>(decision =>
        {
            decision.ToTable("decision_log");
            decision.HasKey(d => new { d.TenantId, d.Id, d.EvaluatedAt });
            decision.HasAuditColumns(updatable: false);
            decision.Ignore(d => d.DomainEvents);
            decision.Ignore(d => d.HasChanges);
            decision.Ignore(d => d.RowVersion);
            decision.Property(d => d.RulesetCode).HasColumnName("ruleset_code").HasMaxLength(20);
            decision.Property(d => d.RulesetVersion).HasColumnName("ruleset_version");
            decision.Property(d => d.SubjectType).HasMaxLength(30);
            decision.Property(d => d.Inputs).HasColumnType("jsonb");
            decision.Property(d => d.Outcome).HasMaxLength(15);
        });
    }
}
