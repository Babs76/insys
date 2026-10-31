/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.Party.Domain.Parties;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>The Party module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class PartyDbContext(DbContextOptions<PartyDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "party";

    public override string Schema => SchemaName;

    public DbSet<Domain.Parties.Party> Parties => Set<Domain.Parties.Party>();

    public DbSet<ConsentRecord> Consents => Set<ConsentRecord>();

    public DbSet<DataSubjectRequest> DataSubjectRequests => Set<DataSubjectRequest>();

    public DbSet<DuplicateCandidate> DuplicateCandidates => Set<DuplicateCandidate>();

    public DbSet<DueDiligenceCheck> DueDiligenceChecks => Set<DueDiligenceCheck>();

    public DbSet<DueDiligence> DueDiligences => Set<DueDiligence>();

    public DbSet<BeneficialOwner> BeneficialOwners => Set<BeneficialOwner>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        PartyStaticData.Configure(modelBuilder);

        modelBuilder.Entity<DataSubjectRequest>(request =>
        {
            request.ToTable("data_subject_request"); // Wave 6 §4.5, F059
            request.HasKey(r => new { r.TenantId, r.Id });
            request.HasAuditColumns();
            request.Property(r => r.RequestNumber).HasMaxLength(25);
            request.Property(r => r.RequestType).HasMaxLength(12);
            request.Property(r => r.Status).HasMaxLength(20);
            request.Property(r => r.Outcome).HasMaxLength(12);
            request.Property(r => r.OutcomeReason).HasMaxLength(1000);
            request.Property(r => r.RowVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<SearchLogHold>(hold =>
        {
            hold.ToTable("search_log_hold");
            hold.HasKey(h => new { h.TenantId, h.Id });
            hold.HasAuditColumns();
            hold.Ignore(h => h.DomainEvents);
            hold.Ignore(h => h.HasChanges);
            hold.Property(h => h.SearchedBy).HasMaxLength(80);
            hold.Property(h => h.Reason).HasMaxLength(200);
            hold.Property(h => h.PlacedBy).HasMaxLength(80);
            hold.Property(h => h.ReleasedBy).HasMaxLength(80);
            hold.Property(h => h.ReleaseReason).HasMaxLength(200);
            hold.Ignore(h => h.IsActive);
            hold.Ignore(h => h.ReleasePending);
        });

        modelBuilder.Entity<PartySearchLog>(log =>
        {
            log.ToTable("party_search_log");
            log.HasKey(l => new { l.TenantId, l.Id });
            log.HasAuditColumns(updatable: false);
            log.Property(l => l.SearchedBy).HasMaxLength(80);
            log.Property(l => l.NameFilter).HasMaxLength(200);
            log.Property(l => l.IdentifierDocType).HasMaxLength(15);
            log.Property(l => l.IdentifierCountry).HasColumnType("char(2)");
            log.Property(l => l.IdentifierHash).HasColumnType("char(64)");
            log.Property(l => l.RoleFilter).HasMaxLength(20);
            log.Property(l => l.StatusFilter).HasMaxLength(12);
        });

        modelBuilder.Entity<Domain.Parties.Party>(party =>
        {
            party.ToTable("party");
            party.Property(p => p.ReviewStatus).HasMaxLength(15);
            party.Property(p => p.RegisteredBy).HasMaxLength(40);
            party.Property(p => p.PartyNumber).HasMaxLength(10).ValueGeneratedOnAdd(); // BR-029: the database's trigger assigns it (V0071)
            party.Property(p => p.MatchResolution).HasMaxLength(4);
            party.HasKey(p => new { p.TenantId, p.Id });
            party.HasAuditColumns();
            party.Property(p => p.RetentionReviewRaisedOn);
            party.Ignore(p => p.DomainEvents);
            party.Ignore(p => p.HasChanges);
            party.Property(p => p.Kind).HasColumnName("party_kind").HasConversion(new UpperCaseEnumConverter<PartyKind>()).HasMaxLength(12);
            party.Property(p => p.DisplayName).HasMaxLength(200);
            party.Property(p => p.Status).HasColumnName("status_code").HasConversion(new UpperCaseEnumConverter<PartyStatus>()).HasMaxLength(12);
            party.HasOne(p => p.Individual).WithOne().HasForeignKey<Individual>(i => new { i.TenantId, i.PartyId });
            party.HasOne(p => p.Organisation).WithOne().HasForeignKey<Organisation>(o => new { o.TenantId, o.PartyId });
            Children(party, p => p.Addresses, nameof(Domain.Parties.Party.Addresses));
            Children(party, p => p.ContactPoints, nameof(Domain.Parties.Party.ContactPoints));
            Children(party, p => p.Identifiers, nameof(Domain.Parties.Party.Identifiers));
            Children(party, p => p.BankAccounts, nameof(Domain.Parties.Party.BankAccounts));
            Children(party, p => p.Roles, nameof(Domain.Parties.Party.Roles));
            party.HasMany(p => p.Relationships).WithOne().HasForeignKey(r => new { r.TenantId, r.FromPartyId }).OnDelete(DeleteBehavior.Cascade);
            party.Navigation(p => p.Relationships).UsePropertyAccessMode(PropertyAccessMode.Field);
            party.HasMany(p => p.Merges).WithOne().HasForeignKey(m => new { m.TenantId, m.MergedId });
            party.Navigation(p => p.Merges).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Individual>(individual =>
        {
            individual.ToTable("individual");
            individual.HasKey(i => new { i.TenantId, i.PartyId });
            individual.Ignore(i => i.Id);
            individual.HasAuditColumns();
            individual.Property(i => i.TitleCode).HasMaxLength(10);
            individual.Property(i => i.FirstNames).HasMaxLength(100);
            individual.Property(i => i.Surname).HasMaxLength(100);
            individual.Property(i => i.GenderCode).HasMaxLength(10);
            individual.Property(i => i.MaritalStatusCode).HasMaxLength(15);
            individual.Property(i => i.Nationality).HasColumnType("char(2)");
            individual.Property(i => i.PreferredLanguage).HasColumnType("char(2)");
        });

        modelBuilder.Entity<Organisation>(organisation =>
        {
            organisation.ToTable("organisation");
            organisation.HasKey(o => new { o.TenantId, o.PartyId });
            organisation.Ignore(o => o.Id);
            organisation.HasAuditColumns();
            organisation.Property(o => o.LegalName).HasMaxLength(150);
            organisation.Property(o => o.TradingName).HasMaxLength(150);
            organisation.Property(o => o.LegalFormCode).HasMaxLength(10);
            organisation.Property(o => o.RegistrationNumber).HasMaxLength(40);
            organisation.Property(o => o.IndustryCode).HasMaxLength(6);
        });

        modelBuilder.Entity<Address>(address =>
        {
            address.ToTable("address");
            address.HasKey(a => new { a.TenantId, a.Id });
            address.HasAuditColumns();
            address.Property(a => a.Country).HasColumnType("char(2)");
        });

        modelBuilder.Entity<ContactPoint>(contact =>
        {
            contact.ToTable("contact_point");
            contact.HasKey(c => new { c.TenantId, c.Id });
            contact.HasAuditColumns();
        });

        // A change is a new row (V0018); the account number is encrypted, its hash keyed.
        modelBuilder.Entity<BankAccount>(account =>
        {
            account.ToTable("bank_account");
            account.HasKey(a => new { a.TenantId, a.Id });
            account.HasAuditColumns();
            account.Property(a => a.AccountNumberHash).HasColumnType("char(64)");
            account.Property(a => a.AccountNumberLast4).HasColumnType("char(4)");
            account.Property(a => a.BankCode).HasMaxLength(20);
            account.Property(a => a.BranchCode).HasMaxLength(20);
            account.Property(a => a.AccountHolderName).HasMaxLength(140);
            account.Property(a => a.AccountType).HasMaxLength(15);
            account.Property(a => a.Currency).HasColumnType("char(3)");
            account.Property(a => a.VerificationRef).HasMaxLength(60);
            account.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<BankAccountStatus>()).HasMaxLength(25);
            account.HasOne<BankAccount>().WithMany().HasForeignKey(a => new { a.TenantId, a.SupersededById });
        });

        modelBuilder.Entity<PartyIdentifier>(identifier =>
        {
            identifier.ToTable("party_identifier");
            identifier.HasKey(i => new { i.TenantId, i.Id });
            identifier.HasAuditColumns();
            identifier.Property(i => i.Country).HasColumnType("char(2)");
            identifier.Property(i => i.NumberHash).HasColumnType("char(64)");
        });

        modelBuilder.Entity<PartyRelationship>(relationship =>
        {
            relationship.ToTable("party_relationship");
            relationship.HasKey(r => new { r.TenantId, r.Id });
            relationship.HasAuditColumns();
        });

        modelBuilder.Entity<PartyRole>(role =>
        {
            role.ToTable("party_role");
            role.HasKey(r => new { r.TenantId, r.Id });
            role.HasAuditColumns();
        });

        modelBuilder.Entity<MergeHistory>(merge =>
        {
            merge.ToTable("merge_history");
            merge.HasKey(m => new { m.TenantId, m.Id });
            merge.HasAuditColumns(updatable: false);
            merge.Property(m => m.Snapshot).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ConsentRecord>(consent =>
        {
            consent.ToTable("consent_record");
            consent.HasKey(c => new { c.TenantId, c.Id });
            consent.HasAuditColumns();
            consent.Ignore(c => c.DomainEvents);
            consent.Ignore(c => c.HasChanges);
        });

        modelBuilder.Entity<DuplicateCandidate>(candidate =>
        {
            candidate.ToTable("duplicate_candidate");
            candidate.HasKey(c => new { c.TenantId, c.Id });
            candidate.HasAuditColumns();
            candidate.Ignore(c => c.DomainEvents);
            candidate.Ignore(c => c.HasChanges);
            candidate.HasOne<Domain.Parties.Party>().WithMany().HasForeignKey(c => new { c.TenantId, c.PartyAId });
            candidate.HasOne<Domain.Parties.Party>().WithMany().HasForeignKey(c => new { c.TenantId, c.PartyBId });
        });

        modelBuilder.Entity<DueDiligenceCheck>(check =>
        {
            check.ToTable("due_diligence_check");
            check.HasKey(c => new { c.TenantId, c.Id });
            check.HasAuditColumns();
            check.Ignore(c => c.DomainEvents);
            check.Ignore(c => c.HasChanges);
            check.HasOne<Domain.Parties.Party>().WithMany().HasForeignKey(c => new { c.TenantId, c.PartyId });
        });

        // F002, F003, F077 (addenda/due-diligence.md §3).
        modelBuilder.Entity<DueDiligence>(dd =>
        {
            dd.ToTable("due_diligence");
            dd.HasKey(d => new { d.TenantId, d.Id });
            dd.HasAuditColumns();
            dd.Ignore(d => d.DomainEvents);
            dd.Ignore(d => d.HasChanges);
            dd.Ignore(d => d.HasOpenHit);
            dd.Ignore(d => d.IsPep);
            dd.Property(d => d.Level).HasColumnName("dd_level");
            dd.HasOne<Domain.Parties.Party>().WithOne().HasForeignKey<DueDiligence>(d => new { d.TenantId, d.PartyId });
            dd.HasMany(d => d.Hits).WithOne().HasForeignKey(h => new { h.TenantId, h.DueDiligenceId }).OnDelete(DeleteBehavior.Cascade);
            dd.Navigation(d => d.Hits).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ScreeningHit>(hit =>
        {
            hit.ToTable("screening_hit");
            hit.HasKey(h => new { h.TenantId, h.Id });
            hit.HasAuditColumns();
            hit.Ignore(h => h.Open);
            hit.Property(h => h.Score).HasPrecision(5, 2);
        });

        modelBuilder.Entity<BeneficialOwner>(owner =>
        {
            owner.ToTable("beneficial_owner");
            owner.HasKey(o => new { o.TenantId, o.Id });
            owner.HasAuditColumns();
            owner.Ignore(o => o.DomainEvents);
            owner.Ignore(o => o.HasChanges);
            owner.Property(o => o.Established).HasColumnName("established_by");
            owner.Property(o => o.SharePercent).HasPrecision(5, 2);
        });
    }

    private static void Children<TChild>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Domain.Parties.Party> party,
        System.Linq.Expressions.Expression<Func<Domain.Parties.Party, IEnumerable<TChild>?>> navigation, string name)
        where TChild : class
    {
        party.HasMany(navigation).WithOne().HasForeignKey("TenantId", "PartyId").OnDelete(DeleteBehavior.Cascade);
        party.Navigation(name).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
