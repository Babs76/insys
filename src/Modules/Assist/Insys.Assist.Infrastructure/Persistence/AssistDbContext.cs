/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Insys.Assist.Infrastructure.Persistence;

/// <summary>The Assist module's context: its own schema only, on its own database role (AIA-062, DBA-004).</summary>
public sealed class AssistDbContext(DbContextOptions<AssistDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "assist";

    public override string Schema => SchemaName;

    public DbSet<AssistTenantSetting> Settings => Set<AssistTenantSetting>();

    public DbSet<AssistRun> Runs => Set<AssistRun>();

    public DbSet<AssistRunPayload> Payloads => Set<AssistRunPayload>();

    public DbSet<AssistExplanation> Explanations => Set<AssistExplanation>();

    public DbSet<AssistExplanationFeedback> Feedback => Set<AssistExplanationFeedback>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssistTenantSetting>(setting =>
        {
            setting.ToTable("tenant_setting");
            setting.HasKey(s => new { s.TenantId, s.Id });
            setting.HasAuditColumns();
            setting.Ignore(s => s.DomainEvents);
            setting.Ignore(s => s.HasChanges);
            setting.Ignore(s => s.ResidencyAllowsInference);
            setting.Property(s => s.Jurisdiction).HasColumnType("char(2)");
            setting.Property(s => s.InferenceRegion).HasMaxLength(20);
            setting.Property(s => s.LegalBasis).HasMaxLength(500);
        });
        modelBuilder.Entity<AssistRun>(run =>
        {
            run.ToTable("run");
            run.HasKey(r => new { r.TenantId, r.Id });
            run.HasAuditColumns(updatable: false);
            run.Ignore(r => r.DomainEvents);
            run.Ignore(r => r.HasChanges);
            run.Ignore(r => r.RowVersion);
            run.Property(r => r.ServedModel).HasMaxLength(80);
            run.Property(r => r.InferenceRegion).HasMaxLength(40);
            run.Property(r => r.Cost).HasPrecision(18, 6);
            run.Property(r => r.CostCurrency).HasColumnType("char(3)");
        });
        modelBuilder.Entity<AssistRunPayload>(payload =>
        {
            payload.ToTable("run_payload");
            payload.HasKey(p => new { p.TenantId, p.Id });
            payload.HasAuditColumns(updatable: false);
            payload.Ignore(p => p.DomainEvents);
            payload.Ignore(p => p.HasChanges);
            payload.Ignore(p => p.RowVersion);
            payload.Property(p => p.Masked).HasColumnType("jsonb");
            payload.HasOne<AssistRun>().WithMany().HasForeignKey(p => new { p.TenantId, p.RunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });

        modelBuilder.Entity<AssistExplanation>(explanation =>
        {
            explanation.ToTable("explanation");
            explanation.HasKey(e => new { e.TenantId, e.Id });
            explanation.HasAuditColumns(updatable: false);
            explanation.Ignore(e => e.DomainEvents);
            explanation.Ignore(e => e.HasChanges);
            explanation.Ignore(e => e.RowVersion);
            explanation.Property(e => e.Facts).HasColumnType("jsonb");
            // So that the run is written before the explanation that names it (fk_explanation_run).
            explanation.HasOne<AssistRun>().WithMany().HasForeignKey(e => new { e.TenantId, e.RunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });

        modelBuilder.Entity<AssistExplanationFeedback>(feedback =>
        {
            feedback.ToTable("explanation_feedback");
            feedback.HasKey(f => new { f.TenantId, f.Id });
            feedback.HasAuditColumns(updatable: false);
            feedback.Ignore(f => f.DomainEvents);
            feedback.Ignore(f => f.HasChanges);
            feedback.Ignore(f => f.RowVersion);
        });
    }
}

internal sealed class AssistRepository(AssistDbContext db) : Application.IAssistRepository
{
    public Task<AssistTenantSetting?> Setting(CancellationToken cancellationToken) => db.Settings.SingleOrDefaultAsync(cancellationToken);

    public void Add(AssistTenantSetting setting) => db.Settings.Add(setting);

    public void Add(AssistRun run) => db.Runs.Add(run);

    public void Add(AssistRunPayload payload) => db.Payloads.Add(payload);

    public void Add(AssistExplanation explanation) => db.Explanations.Add(explanation);

    public void Add(AssistExplanationFeedback feedback) => db.Feedback.Add(feedback);

    public Task<AssistExplanation?> Explained(string cacheKey, CancellationToken cancellationToken) =>
        db.Explanations.AsNoTracking().Where(e => e.CacheKey == cacheKey && e.Outcome == ExplanationOutcomes.Explained && e.ReusedFrom == null)
            .OrderByDescending(e => e.Id).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> ExplanationShownTo(Guid id, string actorId, CancellationToken cancellationToken) =>
        db.Explanations.AnyAsync(e => e.Id == id && EF.Property<string>(e, ModuleDbContext.CreatedBy) == actorId, cancellationToken);

    public async Task<Application.AssistUsage> Usage(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var runs = await db.Runs.AsNoTracking().Where(r => r.StartedAt >= from && r.StartedAt < to).Select(r => new { r.Status, r.InputTokens, r.OutputTokens }).ToListAsync(cancellationToken);
        return new Application.AssistUsage(runs.Sum(r => r.InputTokens + r.OutputTokens), runs.Count, runs.Count(r => r.Status == AssistRunStatuses.Rejected),
            runs.Count(r => r.Status == AssistRunStatuses.Unavailable));
    }
}
