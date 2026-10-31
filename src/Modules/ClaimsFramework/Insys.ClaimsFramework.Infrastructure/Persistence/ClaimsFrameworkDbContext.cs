/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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
using Insys.ClaimsFramework.Domain.Claims;
using Insys.ClaimsFramework.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.ClaimsFramework.Infrastructure.Persistence;

/// <summary>The ClaimsFramework module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ClaimsFrameworkDbContext(DbContextOptions<ClaimsFrameworkDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "claims";

    public override string Schema => SchemaName;

    public DbSet<ClaimRegistration> Claims => Set<ClaimRegistration>();

    public DbSet<FinancialMovement> Movements => Set<FinancialMovement>();

    public DbSet<ClaimPaymentInstruction> Instructions => Set<ClaimPaymentInstruction>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ClaimsFrameworkStaticData.Configure(modelBuilder);

        // Children reference the claim by its number (V0001's foreign keys), the register's alternate key.
        modelBuilder.Entity<FraudCase>(fraud =>
        {
            fraud.ToTable("fraud_case"); // Wave 6 §4.8, F058
            fraud.HasKey(c => new { c.TenantId, c.Id });
            fraud.HasAuditColumns();
            fraud.Property(c => c.CaseNumber).HasMaxLength(25);
            fraud.Property(c => c.Domain).HasMaxLength(10);
            fraud.Property(c => c.ClaimNumber).HasMaxLength(25);
            fraud.Property(c => c.Status).HasMaxLength(15);
            fraud.Property(c => c.Outcome).HasMaxLength(15);
            fraud.Property(c => c.OutcomeReason).HasMaxLength(1000);
            fraud.Property(c => c.RowVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<ClaimRegistration>(claim =>
        {
            claim.ToTable("claim_register");
            claim.Property(c => c.ProductCode).HasMaxLength(15);
            claim.Property(c => c.OrgNodeCode).HasMaxLength(15);
            claim.HasKey(c => new { c.TenantId, c.Id });
            claim.HasAlternateKey(c => new { c.TenantId, c.ClaimNumber });
            claim.HasAuditColumns();
            claim.Ignore(c => c.DomainEvents);
            claim.Ignore(c => c.HasChanges);
            claim.Property(c => c.ClaimNumber).HasMaxLength(25);
            claim.Property(c => c.Domain).HasMaxLength(10);
            claim.HasMany(c => c.Evidence).WithOne().HasForeignKey(e => new { e.TenantId, e.ClaimNumber }).HasPrincipalKey(c => new { c.TenantId, c.ClaimNumber });
            claim.Navigation(c => c.Evidence).UsePropertyAccessMode(PropertyAccessMode.Field);
            claim.HasMany(c => c.Communications).WithOne().HasForeignKey(e => new { e.TenantId, e.ClaimNumber }).HasPrincipalKey(c => new { c.TenantId, c.ClaimNumber });
            claim.Navigation(c => c.Communications).UsePropertyAccessMode(PropertyAccessMode.Field);
            claim.HasMany(c => c.FraudHits).WithOne().HasForeignKey(e => new { e.TenantId, e.ClaimNumber }).HasPrincipalKey(c => new { c.TenantId, c.ClaimNumber });
            claim.Navigation(c => c.FraudHits).UsePropertyAccessMode(PropertyAccessMode.Field);
            claim.HasMany(c => c.FraudEvaluations).WithOne().HasForeignKey(e => new { e.TenantId, e.ClaimNumber }).HasPrincipalKey(c => new { c.TenantId, c.ClaimNumber });
            claim.Navigation(c => c.FraudEvaluations).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<EvidenceItem>(evidence =>
        {
            evidence.ToTable("claim_evidence");
            evidence.HasKey(e => new { e.TenantId, e.Id });
            evidence.HasAuditColumns();
            evidence.Property(e => e.ClaimNumber).HasMaxLength(25);
            evidence.Property(e => e.EvidenceType).HasMaxLength(20);
            evidence.Property(e => e.Status).HasMaxLength(12);
        });

        modelBuilder.Entity<Communication>(communication =>
        {
            communication.ToTable("claim_communication");
            communication.HasKey(c => new { c.TenantId, c.Id });
            communication.HasAuditColumns(updatable: false);
            communication.Property(c => c.ClaimNumber).HasMaxLength(25);
            communication.Property(c => c.Direction).HasMaxLength(8);
            communication.Property(c => c.Channel).HasMaxLength(10);
        });

        modelBuilder.Entity<FraudEvaluationRecord>(evaluation =>
        {
            evaluation.ToTable("fraud_evaluation");
            evaluation.HasKey(e => new { e.TenantId, e.Id });
            evaluation.HasAuditColumns(updatable: false);
            evaluation.Property(e => e.ClaimNumber).HasMaxLength(25);
            evaluation.Property(e => e.Skipped).HasColumnType("jsonb").HasConversion(
                new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IReadOnlyList<SkippedIndicator>, string>(
                    v => SkippedJson.Write(v), v => SkippedJson.Read(v)),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<SkippedIndicator>>(
                    (a, b) => SkippedJson.Write(a!) == SkippedJson.Write(b!), v => v.Count, v => SkippedJson.Read(SkippedJson.Write(v))));
        });

        modelBuilder.Entity<FraudIndicatorHit>(hit =>
        {
            hit.ToTable("fraud_indicator_hit");
            hit.HasKey(h => new { h.TenantId, h.Id });
            hit.HasAuditColumns(updatable: false);
            hit.Property(h => h.ClaimNumber).HasMaxLength(25);
            hit.Property(h => h.IndicatorCode).HasMaxLength(20);
        });

        // Append-only: inserted, never updated or deleted (CLM-004; V0014 refuses both). No row_version.
        modelBuilder.Entity<FinancialMovement>(movement =>
        {
            movement.ToTable("claim_financial_movement");
            movement.HasKey(m => new { m.TenantId, m.Id });
            movement.HasAuditColumns(updatable: false);
            movement.Ignore(m => m.DomainEvents);
            movement.Ignore(m => m.HasChanges);
            movement.Ignore(m => m.RowVersion);
            movement.Property(m => m.ClaimNumber).HasMaxLength(25);
            movement.Property(m => m.MovementType).HasConversion(new UpperCaseEnumConverter<MovementType>()).HasMaxLength(15);
            movement.Property(m => m.ReserveType).HasMaxLength(15);
            movement.Property(m => m.Amount).HasPrecision(18, 2);
            movement.Property(m => m.Currency).HasColumnType("char(3)");
        });

        modelBuilder.Entity<ClaimPaymentInstruction>(instruction =>
        {
            instruction.ToTable("claim_payment_instruction");
            instruction.HasKey(i => new { i.TenantId, i.Id });
            instruction.HasAuditColumns();
            instruction.Ignore(i => i.DomainEvents);
            instruction.Ignore(i => i.HasChanges);
            instruction.Property(i => i.ClaimNumber).HasMaxLength(25);
            instruction.Property(i => i.InstructionRef).HasMaxLength(60);
            instruction.Property(i => i.Amount).HasPrecision(18, 2);
            instruction.Property(i => i.ApprovedAmount).HasPrecision(18, 2);
            instruction.Property(i => i.Currency).HasColumnType("char(3)");
            instruction.Property(i => i.ReserveType).HasMaxLength(15);
            instruction.Property(i => i.Status).HasConversion(new UpperCaseEnumConverter<InstructionStatus>()).HasMaxLength(30);
            instruction.Property(i => i.Attempt).HasColumnType("smallint");
            instruction.Property(i => i.HeldReason).HasMaxLength(30);
            instruction.Property(i => i.HeldDetail).HasMaxLength(200);
            instruction.Property(i => i.PayeeBankHash).HasMaxLength(64);
            instruction.Property(i => i.PendingPayeeBankHash).HasMaxLength(64);
            instruction.Ignore(i => i.SubjectHash);
            instruction.Ignore(i => i.PendingSubjectHash);
            instruction.Ignore(i => i.Payee);
        });
    }
}

/// <summary>The skipped indicators of a fraud evaluation as jsonb (F009).</summary>
internal static class SkippedJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Write(IReadOnlyList<SkippedIndicator> skipped) => System.Text.Json.JsonSerializer.Serialize(skipped, Options);

    public static IReadOnlyList<SkippedIndicator> Read(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<List<SkippedIndicator>>(json, Options) ?? [];
}
