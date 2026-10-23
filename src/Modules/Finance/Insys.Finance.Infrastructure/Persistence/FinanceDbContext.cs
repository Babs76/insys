/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using Insys.Finance.Domain.Ledger;
using Insys.Finance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Finance.Infrastructure.Persistence;

/// <summary>The Finance module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "finance";

    public override string Schema => SchemaName;

    public DbSet<Journal> Journals => Set<Journal>();

    public DbSet<AccountingPeriod> Periods => Set<AccountingPeriod>();

    public DbSet<PostingException> PostingExceptions => Set<PostingException>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        FinanceStaticData.Configure(modelBuilder);

        // journal and subledger_entry are partitioned by accounting_date, part of their keys. Neither is ever updated
        // (V0014: no UPDATE or DELETE grant, and a trigger), and the database checks each journal balances at commit.
        modelBuilder.Entity<Journal>(journal =>
        {
            journal.ToTable("journal");
            journal.HasKey(j => new { j.TenantId, j.Id, j.AccountingDate });
            // Posted: inserted only, never updated (FIN-002; V0014), so no row_version or updated_* columns.
            journal.HasAuditColumns(updatable: false);
            journal.Ignore(j => j.RowVersion);
            journal.Ignore(j => j.DomainEvents);
            journal.Ignore(j => j.HasChanges);
            journal.Property(j => j.JournalNumber).HasMaxLength(20);
            journal.Property(j => j.PeriodCode).HasColumnType("char(7)");
            journal.Property(j => j.SourceDomain).HasMaxLength(10);
            journal.Property(j => j.Status).HasMaxLength(10);
            journal.HasMany(j => j.Lines).WithOne().HasForeignKey(l => new { l.TenantId, l.JournalId, l.AccountingDate });
            journal.Navigation(j => j.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SubledgerEntry>(entry =>
        {
            entry.ToTable("subledger_entry");
            entry.HasKey(e => new { e.TenantId, e.Id, e.AccountingDate });
            entry.HasAuditColumns(updatable: false);
            entry.Property(e => e.AccountCode).HasMaxLength(20);
            entry.Property(e => e.Debit).HasPrecision(18, 2);
            entry.Property(e => e.Credit).HasPrecision(18, 2);
            entry.Property(e => e.Currency).HasColumnType("char(3)");
            entry.Property(e => e.BaseAmount).HasPrecision(18, 2);
            entry.Property(e => e.FxRate).HasPrecision(18, 8);
            entry.Property(e => e.BusinessRefType).HasMaxLength(30);
            entry.Property(e => e.PostingRuleCode).HasMaxLength(20);
        });

        modelBuilder.Entity<AccountingPeriod>(period =>
        {
            period.ToTable("accounting_period");
            period.HasKey(p => new { p.TenantId, p.Id });
            period.HasAuditColumns();
            period.Ignore(p => p.DomainEvents);
            period.Ignore(p => p.HasChanges);
            period.Property(p => p.PeriodCode).HasMaxLength(7);
            period.Property(p => p.Status).HasConversion(new UpperCaseEnumConverter<PeriodStatus>()).HasMaxLength(12);
        });

        modelBuilder.Entity<PostingException>(exception =>
        {
            exception.ToTable("posting_exception");
            exception.HasKey(e => new { e.TenantId, e.Id });
            exception.HasAuditColumns();
            exception.Ignore(e => e.DomainEvents);
            exception.Ignore(e => e.HasChanges);
            exception.Property(e => e.EventType).HasMaxLength(80);
            exception.Property(e => e.Status).HasMaxLength(10);
            exception.Property(e => e.BusinessEvent).HasColumnType("jsonb");
        });
    }
}
