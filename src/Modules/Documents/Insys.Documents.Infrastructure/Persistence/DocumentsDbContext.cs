/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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
using Insys.Documents.Domain.Documents;
using Insys.Documents.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Documents.Infrastructure.Persistence;

/// <summary>The Documents module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "document";

    public override string Schema => SchemaName;

    public DbSet<Document> Documents => Set<Document>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        DocumentsStaticData.Configure(modelBuilder);

        modelBuilder.Entity<Document>(document =>
        {
            document.ToTable("document");
            document.HasKey(d => new { d.TenantId, d.Id });
            document.HasAuditColumns();
            document.Ignore(d => d.DomainEvents);
            document.Ignore(d => d.HasChanges);
            document.Property(d => d.CategoryCode).HasMaxLength(20);
            document.Property(d => d.Classification).HasMaxLength(20);
            document.Property(d => d.StorageUri).HasMaxLength(300);
            document.Property(d => d.MimeType).HasMaxLength(80);
            document.Property(d => d.Sha256).HasColumnName("sha256").HasColumnType("char(64)");
            document.Property(d => d.TemplateCode).HasMaxLength(20);
            document.Property(d => d.ProductVersionRef).HasMaxLength(30);
            document.Property(d => d.BusinessRefType).HasMaxLength(30);
            document.Property(d => d.Status).HasConversion(new UpperCaseEnumConverter<DocumentStatus>()).HasMaxLength(12);
            document.HasMany(d => d.Deliveries).WithOne().HasForeignKey(x => new { x.TenantId, x.DocumentId });
            document.Navigation(d => d.Deliveries).UsePropertyAccessMode(PropertyAccessMode.Field);
            document.HasMany(d => d.Holds).WithOne().HasForeignKey(h => new { h.TenantId, h.ScopeId });
            document.Navigation(d => d.Holds).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Delivery>(delivery =>
        {
            delivery.ToTable("delivery");
            delivery.HasKey(d => new { d.TenantId, d.Id });
            delivery.HasAuditColumns();
            delivery.Property(d => d.Channel).HasMaxLength(10);
            delivery.Property(d => d.Recipient).HasMaxLength(200);
            delivery.Property(d => d.Status).HasMaxLength(12);
            delivery.Property(d => d.ProviderRef).HasMaxLength(80);
        });

        modelBuilder.Entity<LegalHold>(hold =>
        {
            hold.ToTable("legal_hold");
            hold.HasKey(h => new { h.TenantId, h.Id });
            hold.HasAuditColumns();
            hold.Ignore(h => h.IsActive);
            hold.Ignore(h => h.ReleasePending);
            hold.Property(h => h.ReleaseReason).HasMaxLength(200);
            hold.Property(h => h.ScopeType).HasMaxLength(30);
            hold.Property(h => h.ReasonCode).HasMaxLength(15);
        });

        modelBuilder.Entity<RecordHold>(hold =>
        {
            hold.ToTable("record_hold");
            hold.HasKey(h => new { h.TenantId, h.Id });
            hold.HasAuditColumns();
            hold.Ignore(h => h.DomainEvents);
            hold.Ignore(h => h.HasChanges);
            hold.Ignore(h => h.IsActive);
            hold.Ignore(h => h.ReleasePending);
            hold.Property(h => h.ScopeType).HasMaxLength(30);
            hold.Property(h => h.ReasonCode).HasMaxLength(15);
            hold.Property(h => h.ReleaseReason).HasMaxLength(200);
        });

        // document_access_log is partitioned by accessed_at, which is part of its key; rows are never updated.
        modelBuilder.Entity<AccessRecord>(access =>
        {
            access.ToTable("document_access_log");
            access.HasKey(a => new { a.TenantId, a.Id, a.AccessedAt });
            access.HasAuditColumns(updatable: false);
            access.Property(a => a.Action).HasMaxLength(10);
            access.Property(a => a.Purpose).HasMaxLength(60);
        });

        modelBuilder.Entity<GenerationRequest>(request =>
        {
            request.ToTable("generation_request");
            request.HasKey(r => new { r.TenantId, r.Id });
            request.HasAuditColumns();
            request.Property(r => r.TemplateCode).HasMaxLength(20);
            request.Property(r => r.Language).HasColumnType("char(2)");
            request.Property(r => r.DataRef).HasColumnType("jsonb");
            request.Property(r => r.Status).HasMaxLength(12);
        });
    }
}
