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

    public DbSet<StarterPack> Packs => Set<StarterPack>();

    public DbSet<StarterPackValue> PackValues => Set<StarterPackValue>();

    public DbSet<SetupRun> SetupRuns => Set<SetupRun>();

    public DbSet<SetupAnswer> SetupAnswers => Set<SetupAnswer>();

    public DbSet<SetupItem> SetupItems => Set<SetupItem>();

    public DbSet<ProductDraftRun> ProductDraftRuns => Set<ProductDraftRun>();

    public DbSet<ProductDraftDocument> ProductDraftDocuments => Set<ProductDraftDocument>();

    public DbSet<DraftItem> DraftItems => Set<DraftItem>();

    public DbSet<SetupAiRun> SetupAiRuns => Set<SetupAiRun>();

    public DbSet<SetupDocument> SetupDocuments => Set<SetupDocument>();

    public DbSet<SetupUnmapped> SetupUnmapped => Set<SetupUnmapped>();

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

        // D-18: the starter packs. A platform version is held by the operator tenant and read by all (row-level security: read_platform_and_own).
        modelBuilder.Entity<StarterPack>(pack =>
        {
            pack.ToTable("starter_pack");
            pack.HasKey(p => new { p.TenantId, p.Id });
            pack.HasAuditColumns();
            pack.Ignore(p => p.DomainEvents);
            pack.Ignore(p => p.HasChanges);
            pack.Property(p => p.Country).HasColumnType("char(2)");
            pack.Property(p => p.ContentHash).HasColumnType("char(64)");
        });
        modelBuilder.Entity<StarterPackValue>(value =>
        {
            value.ToTable("starter_pack_value");
            value.HasKey(v => new { v.TenantId, v.Id });
            value.HasAuditColumns(updatable: false);
            value.Ignore(v => v.DomainEvents);
            value.Ignore(v => v.HasChanges);
            value.Ignore(v => v.RowVersion);
            value.Ignore(v => v.MayBeTailored);
            value.Property(v => v.Values).HasColumnType("jsonb");
            // So that the version is written before its rows (fk_starter_pack_value_pack).
            value.HasOne<StarterPack>().WithMany().HasForeignKey(v => new { v.TenantId, v.PackId }).HasPrincipalKey(p => new { p.TenantId, p.Id });
        });

        // AI-3's setup runs (§5A.5): a run, its answers by version, its draft's items.
        modelBuilder.Entity<SetupRun>(run =>
        {
            run.ToTable("setup_run");
            run.HasKey(r => new { r.TenantId, r.Id });
            run.HasAuditColumns();
            run.Ignore(r => r.DomainEvents);
            run.Ignore(r => r.HasChanges);
        });
        modelBuilder.Entity<SetupAnswer>(answer =>
        {
            answer.ToTable("setup_answer");
            answer.HasKey(a => new { a.TenantId, a.Id });
            answer.HasAuditColumns(updatable: false);
            answer.Ignore(a => a.DomainEvents);
            answer.Ignore(a => a.HasChanges);
            answer.Ignore(a => a.RowVersion);
            answer.Property(a => a.Answers).HasColumnType("jsonb");
            answer.HasOne<SetupRun>().WithMany().HasForeignKey(a => new { a.TenantId, a.RunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });
        modelBuilder.Entity<SetupItem>(item =>
        {
            item.ToTable("setup_item");
            item.HasKey(i => new { i.TenantId, i.Id });
            item.HasAuditColumns();
            item.Ignore(i => i.DomainEvents);
            item.Ignore(i => i.HasChanges);
            item.Property(i => i.Values).HasColumnType("jsonb");
            item.HasOne<SetupRun>().WithMany().HasForeignKey(i => new { i.TenantId, i.RunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });
        modelBuilder.Entity<ProductDraftRun>(run =>
        {
            run.ToTable("product_draft_run");
            run.HasKey(r => new { r.TenantId, r.Id });
            run.HasAuditColumns();
            run.Ignore(r => r.DomainEvents);
            run.Ignore(r => r.HasChanges);
        });
        modelBuilder.Entity<ProductDraftDocument>(document =>
        {
            document.ToTable("product_draft_document");
            document.HasKey(d => new { d.TenantId, d.Id });
            document.HasAuditColumns();
            document.Ignore(d => d.DomainEvents);
            document.Ignore(d => d.HasChanges);
            document.Property(d => d.Sha256).HasColumnType("char(64)");
            document.Property(d => d.Identifiers).HasColumnType("jsonb");
            document.HasOne<ProductDraftRun>().WithMany().HasForeignKey(d => new { d.TenantId, d.RunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });
        modelBuilder.Entity<DraftItem>(item =>
        {
            item.ToTable("draft_item");
            item.HasKey(i => new { i.TenantId, i.Id });
            item.HasAuditColumns();
            item.Ignore(i => i.DomainEvents);
            item.Ignore(i => i.HasChanges);
            item.Property(i => i.Value).HasColumnType("jsonb");
            item.HasOne<ProductDraftRun>().WithMany().HasForeignKey(i => new { i.TenantId, i.RunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });

        // AI-3's passes (7.8): a pass over a setup run, the documents the run reads with their confirmations (D-16), and what a pass couldn't map (AIA-083).
        modelBuilder.Entity<SetupAiRun>(pass =>
        {
            pass.ToTable("setup_ai_run");
            pass.HasKey(p => new { p.TenantId, p.Id });
            pass.HasAuditColumns();
            pass.Ignore(p => p.DomainEvents);
            pass.Ignore(p => p.HasChanges);
            pass.Ignore(p => p.Open);
            pass.HasOne<SetupRun>().WithMany().HasForeignKey(p => new { p.TenantId, p.SetupRunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });
        modelBuilder.Entity<SetupDocument>(document =>
        {
            document.ToTable("setup_document");
            document.HasKey(d => new { d.TenantId, d.Id });
            document.HasAuditColumns();
            document.Ignore(d => d.DomainEvents);
            document.Ignore(d => d.HasChanges);
            document.Property(d => d.Sha256).HasColumnType("char(64)");
            document.Property(d => d.Identifiers).HasColumnType("jsonb");
            document.HasOne<SetupRun>().WithMany().HasForeignKey(d => new { d.TenantId, d.SetupRunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });
        modelBuilder.Entity<SetupUnmapped>(line =>
        {
            line.ToTable("setup_unmapped");
            line.HasKey(l => new { l.TenantId, l.Id });
            line.HasAuditColumns();
            line.Ignore(l => l.DomainEvents);
            line.Ignore(l => l.HasChanges);
            line.HasOne<SetupAiRun>().WithMany().HasForeignKey(l => new { l.TenantId, l.AiRunId }).HasPrincipalKey(r => new { r.TenantId, r.Id });
        });
    }
}

internal sealed class ProductDraftRuns(AssistDbContext db) : Application.IProductDraftRuns
{
    public void Add(ProductDraftRun run) => db.ProductDraftRuns.Add(run);

    public void Add(ProductDraftDocument document) => db.ProductDraftDocuments.Add(document);

    public void Add(DraftItem item) => db.DraftItems.Add(item);

    public Task<ProductDraftRun?> Run(Guid id, CancellationToken cancellationToken) => db.ProductDraftRuns.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ProductDraftDocument>> Documents(Guid runId, CancellationToken cancellationToken) =>
        await db.ProductDraftDocuments.Where(d => d.RunId == runId).OrderBy(d => d.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DraftItem>> Items(Guid runId, CancellationToken cancellationToken) =>
        await db.DraftItems.AsNoTracking().Where(i => i.RunId == runId).OrderBy(i => i.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductDraftRun>> Open(int limit, CancellationToken cancellationToken) =>
        await db.ProductDraftRuns.AsNoTracking().Where(r => r.Status == ProductDraftStatuses.Queued || r.Status == ProductDraftStatuses.Running)
            .OrderBy(r => r.RequestedAt).Take(limit).ToListAsync(cancellationToken);
}

internal sealed class SetupRuns(AssistDbContext db) : Application.ISetupRuns
{
    public void Add(SetupRun run) => db.SetupRuns.Add(run);

    public void Add(SetupAnswer answer) => db.SetupAnswers.Add(answer);

    public void Add(SetupItem item) => db.SetupItems.Add(item);

    public Task<SetupRun?> Run(Guid id, CancellationToken cancellationToken) => db.SetupRuns.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<SetupAnswer?> Answers(Guid runId, int version, CancellationToken cancellationToken) =>
        db.SetupAnswers.AsNoTracking().SingleOrDefaultAsync(a => a.RunId == runId && a.Version == version, cancellationToken);

    public async Task<IReadOnlyList<SetupItem>> Items(Guid runId, CancellationToken cancellationToken) =>
        await db.SetupItems.Where(i => i.RunId == runId).ToListAsync(cancellationToken);

    public Task<SetupItem?> Item(Guid runId, Guid itemId, CancellationToken cancellationToken) =>
        db.SetupItems.SingleOrDefaultAsync(i => i.RunId == runId && i.Id == itemId, cancellationToken);

    public void Add(SetupDocument document) => db.SetupDocuments.Add(document);

    public void Add(SetupAiRun pass) => db.SetupAiRuns.Add(pass);

    public void Add(SetupUnmapped line) => db.SetupUnmapped.Add(line);

    public async Task<IReadOnlyList<SetupDocument>> Documents(Guid runId, CancellationToken cancellationToken) =>
        await db.SetupDocuments.Where(d => d.SetupRunId == runId).OrderBy(d => d.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SetupAiRun>> AiRuns(Guid runId, CancellationToken cancellationToken) =>
        await db.SetupAiRuns.Where(p => p.SetupRunId == runId).OrderByDescending(p => p.Id).ToListAsync(cancellationToken);

    public Task<SetupAiRun?> AiRun(Guid passId, CancellationToken cancellationToken) => db.SetupAiRuns.SingleOrDefaultAsync(p => p.Id == passId, cancellationToken);

    public async Task<IReadOnlyList<SetupAiRun>> OpenAiRuns(int limit, CancellationToken cancellationToken) =>
        await db.SetupAiRuns.AsNoTracking().Where(p => p.Status == SetupAiStatuses.Queued || p.Status == SetupAiStatuses.Running)
            .OrderBy(p => p.RequestedAt).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SetupUnmapped>> Unmapped(Guid runId, CancellationToken cancellationToken) =>
        await db.SetupUnmapped.AsNoTracking().Where(u => u.SetupRunId == runId).OrderBy(u => u.Id).ToListAsync(cancellationToken);
}

/// <summary>
/// The starter packs a tenant may read (D-18): platform versions and its own. The reads lift the tenant filter only to say exactly that; row-level security
/// enforces the same rule underneath.
/// </summary>
internal sealed class StarterPacks(AssistDbContext db, BuildingBlocks.Application.Context.ITenantContext tenant) : Application.IStarterPacks
{
    private IQueryable<StarterPack> Readable()
    {
        var current = tenant.RequireTenantId();
        return db.Packs.IgnoreQueryFilters([ModuleDbContext.TenantFilterKey]).Where(p => p.Scope == PackScopes.AllTenants || p.TenantId == current);
    }

    public Task<StarterPack?> Get(Guid id, CancellationToken cancellationToken) => Readable().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<StarterPackValue>> Rows(Guid packId, CancellationToken cancellationToken)
    {
        var current = tenant.RequireTenantId();
        return await db.PackValues.IgnoreQueryFilters([ModuleDbContext.TenantFilterKey]).AsNoTracking()
            .Where(v => v.PackId == packId && (v.Scope == PackScopes.AllTenants || v.TenantId == current)).OrderBy(v => v.LineNo).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StarterPack>> List(string? packCode, CancellationToken cancellationToken) =>
        await Readable().AsNoTracking().Where(p => packCode == null || p.PackCode == packCode).OrderBy(p => p.PackCode).ThenByDescending(p => p.Version).ToListAsync(cancellationToken);

    public async Task<int> LastVersion(string packCode, CancellationToken cancellationToken) =>
        await db.Packs.Where(p => p.PackCode == packCode).Select(p => (int?)p.Version).MaxAsync(cancellationToken) ?? 0;

    public Task<bool> PreparedOrReviewedAny(string packCode, string actor, CancellationToken cancellationToken) =>
        db.Packs.AnyAsync(p => p.PackCode == packCode && (p.PreparedBy == actor || p.ReviewedBy == actor), cancellationToken);

    public Task<bool> UsedInSetupBy(Guid packId, string actor, CancellationToken cancellationToken) =>
        db.SetupRuns.AnyAsync(r => r.StartedBy == actor && r.PackIds.Contains(packId), cancellationToken);

    public void Add(StarterPack pack, IReadOnlyList<StarterPackValue> rows)
    {
        db.Packs.Add(pack);
        db.PackValues.AddRange(rows);
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
