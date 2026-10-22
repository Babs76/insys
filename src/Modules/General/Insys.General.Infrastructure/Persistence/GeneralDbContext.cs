using System.Text.Json;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.General.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>The General module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class GeneralDbContext(DbContextOptions<GeneralDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "general";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public override string Schema => SchemaName;

    public DbSet<RiskObject> RiskObjects => Set<RiskObject>();

    public DbSet<GeneralQuote> Quotes => Set<GeneralQuote>();

    public DbSet<GeneralProposal> Proposals => Set<GeneralProposal>();

    public DbSet<GeneralPolicy> Policies => Set<GeneralPolicy>();

    public DbSet<GeneralEndorsement> Endorsements => Set<GeneralEndorsement>();

    public DbSet<GeneralClaim> Claims => Set<GeneralClaim>();

    public DbSet<LossAssessment> Assessments => Set<LossAssessment>();

    public DbSet<RecoveryCase> Recoveries => Set<RecoveryCase>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        GeneralStaticData.Configure(modelBuilder);

        modelBuilder.Entity<RiskObject>(risk =>
        {
            risk.ToTable("risk_object");
            Root(risk);
            risk.Property(r => r.ObjectType).HasMaxLength(15);
            risk.Property(r => r.Attributes).HasColumnType("jsonb");
            risk.Property(r => r.Valuation).HasPrecision(18, 2);
            risk.Property(r => r.ValuationBasis).HasMaxLength(15);
            risk.Property(r => r.Subdivision).HasMaxLength(6);
            risk.Property(r => r.PostalCode).HasMaxLength(10);
            risk.Property(r => r.RiskZone).HasMaxLength(10);
            risk.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<RiskObjectStatus>()).HasMaxLength(10);
        });

        modelBuilder.Entity<GeneralQuote>(quote =>
        {
            quote.ToTable("quote");
            Root(quote);
            quote.Property(q => q.QuoteNumber).HasMaxLength(15);
            quote.Property(q => q.QuoteVersion).HasColumnType("smallint");
            quote.Property(q => q.ProductCode).HasMaxLength(15);
            quote.Property(q => q.IntermediaryCode).HasMaxLength(15);
            quote.Property(q => q.Status).HasConversion(new UpperCaseEnumConverter<QuoteStatus>()).HasMaxLength(12);
            quote.Property(q => q.Premium).HasPrecision(18, 2);
            quote.Property(q => q.Currency).HasColumnType("char(3)");
            quote.HasMany(q => q.Risks).WithOne().HasForeignKey(r => new { r.TenantId, r.QuoteId });
            quote.Navigation(q => q.Risks).UsePropertyAccessMode(PropertyAccessMode.Field);
            quote.HasMany(q => q.Referrals).WithOne().HasForeignKey(r => new { r.TenantId, r.QuoteId });
            quote.Navigation(q => q.Referrals).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        // A quote's risks and coverages never change once written (V0016).
        modelBuilder.Entity<QuotedRisk>(risk =>
        {
            risk.ToTable("quote_risk");
            risk.HasKey(r => new { r.TenantId, r.Id });
            risk.HasAuditColumns(updatable: false);
            risk.Property(r => r.SumInsured).HasPrecision(18, 2);
            risk.HasMany(r => r.Coverages).WithOne().HasForeignKey(c => new { c.TenantId, c.QuoteRiskId });
            risk.Navigation(r => r.Coverages).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<QuotedCoverage>(coverage =>
        {
            coverage.ToTable("quote_coverage");
            coverage.HasKey(c => new { c.TenantId, c.Id });
            coverage.HasAuditColumns(updatable: false);
            coverage.Property(c => c.CoverageCode).HasMaxLength(20);
            coverage.Property(c => c.SumInsured).HasPrecision(18, 2);
            coverage.Property(c => c.ExcessScheduleCode).HasMaxLength(20);
        });

        modelBuilder.Entity<UnderwritingReferral>(referral =>
        {
            referral.ToTable("underwriting_referral");
            referral.HasKey(r => new { r.TenantId, r.Id });
            referral.HasAuditColumns();
            referral.Property(r => r.ReasonCode).HasMaxLength(15);
            referral.Property(r => r.Status).HasMaxLength(10);
            referral.Property(r => r.DecisionCode).HasMaxLength(15);
        });

        modelBuilder.Entity<GeneralProposal>(proposal =>
        {
            proposal.ToTable("proposal");
            Root(proposal);
            proposal.Property(p => p.Status).HasConversion(new UpperCaseEnumConverter<ProposalStatus>()).HasMaxLength(12);
            proposal.Property(p => p.Declarations).HasColumnType("jsonb");
        });

        modelBuilder.Entity<GeneralPolicy>(policy =>
        {
            policy.ToTable("policy");
            Root(policy);
            policy.Ignore(p => p.Coverages);
            policy.Ignore(p => p.Billing);
            policy.Ignore(p => p.CancellationInEffect);
            policy.Property(p => p.PolicyNumber).HasMaxLength(15);
            policy.Property(p => p.ProductCode).HasMaxLength(15);
            policy.Property(p => p.Status).HasConversion(new UpperCaseEnumConverter<PolicyStatus>()).HasMaxLength(12);
            policy.Property(p => p.Currency).HasColumnType("char(3)");
            policy.Property(p => p.AnnualPremium).HasPrecision(18, 2);
            policy.Property(p => p.IntermediaryCode).HasMaxLength(15);
            policy.Property(p => p.BillingFrequency).HasMaxLength(15);
            policy.Property(p => p.CollectionMethod).HasMaxLength(15);
            policy.Property(p => p.CoverStartsOn).HasMaxLength(15);
            policy.Ignore(p => p.PeriodDays);
            policy.HasMany(p => p.Risks).WithOne().HasForeignKey(r => new { r.TenantId, r.PolicyId });
            policy.Navigation(p => p.Risks).UsePropertyAccessMode(PropertyAccessMode.Field);
            policy.HasMany(p => p.Cancellations).WithOne().HasForeignKey(c => new { c.TenantId, c.PolicyId });
            policy.Navigation(p => p.Cancellations).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PolicyRisk>(risk =>
        {
            risk.ToTable("policy_risk");
            risk.HasKey(r => new { r.TenantId, r.Id });
            risk.HasAuditColumns();
            risk.Property(r => r.SumInsured).HasPrecision(18, 2);
            risk.HasMany(r => r.Coverages).WithOne().HasForeignKey(c => new { c.TenantId, c.PolicyRiskId });
            risk.Navigation(r => r.Coverages).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<GeneralCoverage>(coverage =>
        {
            coverage.ToTable("coverage");
            coverage.HasKey(c => new { c.TenantId, c.Id });
            coverage.HasAuditColumns();
            coverage.Property(c => c.CoverageCode).HasMaxLength(20);
            coverage.Property(c => c.SumInsured).HasPrecision(18, 2);
            coverage.Property(c => c.ExcessScheduleCode).HasMaxLength(20);
            coverage.Property(c => c.Premium).HasPrecision(18, 2);
        });

        // V0001 gives cancellations no row_version or updated_* columns.
        modelBuilder.Entity<Cancellation>(cancellation =>
        {
            cancellation.ToTable("cancellation");
            cancellation.HasKey(c => new { c.TenantId, c.Id });
            cancellation.HasAuditColumns(updatable: false);
            cancellation.Ignore(c => c.RowVersion);
            cancellation.Property(c => c.ReasonCode).HasMaxLength(15);
            cancellation.Property(c => c.RefundAmount).HasPrecision(18, 2);
            cancellation.Property(c => c.RefundBasis).HasMaxLength(12);
            cancellation.Property(c => c.Fee).HasPrecision(18, 2);
        });

        modelBuilder.Entity<GeneralEndorsement>(endorsement =>
        {
            endorsement.ToTable("endorsement");
            Root(endorsement);
            endorsement.Property(e => e.EndorsementNumber).HasMaxLength(20);
            endorsement.Property(e => e.EndorsementType).HasMaxLength(15);
            endorsement.Property(e => e.PremiumDelta).HasPrecision(18, 2);
            endorsement.Property(e => e.NewAnnualPremium).HasPrecision(18, 2);
            endorsement.Property(e => e.Status).HasConversion(new UpperCaseEnumConverter<EndorsementStatus>()).HasMaxLength(30);
            endorsement.Property(e => e.Changes).HasColumnName("change_set").HasColumnType("jsonb")
                .HasConversion(
                    changes => JsonSerializer.Serialize(changes, Json),
                    json => JsonSerializer.Deserialize<List<EndorsementChange>>(json, Json) ?? new List<EndorsementChange>(),
                    new ValueComparer<IReadOnlyList<EndorsementChange>>((a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                        v => JsonSerializer.Serialize(v, Json).GetHashCode(), v => v.ToList()));
        });

        modelBuilder.Entity<GeneralClaim>(claim =>
        {
            claim.ToTable("claim");
            Root(claim);
            claim.Ignore(c => c.SettlementPending);
            claim.Ignore(c => c.SettlementSubjectHash);
            claim.Property(c => c.SettlementPayeeBankHash).HasMaxLength(64);
            claim.Property(c => c.ClaimNumber).HasMaxLength(25);
            claim.Property(c => c.CoverageCode).HasMaxLength(20);
            claim.Property(c => c.CauseCode).HasMaxLength(15);
            claim.Property(c => c.PerilCode).HasMaxLength(10);
            claim.Property(c => c.CatEventCode).HasMaxLength(15);
            claim.Property(c => c.Status).HasConversion(new UpperCaseEnumConverter<ClaimStatus>()).HasMaxLength(30);
            claim.Property(c => c.SettlementAmount).HasPrecision(18, 2);
            claim.Property(c => c.SettlementCurrency).HasColumnType("char(3)");
            claim.Property(c => c.SettlementReserveType).HasMaxLength(15);
            claim.Property(c => c.DecisionCode).HasMaxLength(15);
            claim.HasMany(c => c.Parties).WithOne().HasForeignKey(p => new { p.TenantId, p.ClaimId });
            claim.Navigation(c => c.Parties).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ClaimParty>(party =>
        {
            party.ToTable("claim_party");
            party.HasKey(p => new { p.TenantId, p.Id });
            party.HasAuditColumns();
            party.Property(p => p.Role).HasMaxLength(15);
            party.Property(p => p.InsurerRef).HasMaxLength(60);
        });

        modelBuilder.Entity<LossAssessment>(assessment =>
        {
            assessment.ToTable("loss_assessment");
            Root(assessment);
            assessment.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<AssessmentStatus>()).HasMaxLength(30);
            assessment.Property(a => a.AssessedAmount).HasPrecision(18, 2);
            assessment.Property(a => a.ExcessApplied).HasPrecision(18, 2);
            assessment.HasMany(a => a.Estimates).WithOne().HasForeignKey(e => new { e.TenantId, e.LossAssessmentId });
            assessment.Navigation(a => a.Estimates).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<RepairEstimate>(estimate =>
        {
            estimate.ToTable("repair_estimate");
            estimate.HasKey(e => new { e.TenantId, e.Id });
            estimate.HasAuditColumns();
            estimate.Property(e => e.Amount).HasPrecision(18, 2);
            estimate.Property(e => e.Status).HasMaxLength(10);
        });

        modelBuilder.Entity<RecoveryCase>(recovery =>
        {
            recovery.ToTable("recovery_case");
            Root(recovery);
            recovery.Property(r => r.RecoveryType).HasMaxLength(15);
            recovery.Property(r => r.ExpectedAmount).HasPrecision(18, 2);
            recovery.Property(r => r.RecoveredAmount).HasPrecision(18, 2);
            recovery.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<RecoveryStatus>()).HasMaxLength(30);
            recovery.HasMany(r => r.Salvage).WithOne().HasForeignKey(s => new { s.TenantId, s.RecoveryCaseId });
            recovery.Navigation(r => r.Salvage).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SalvageItem>(item =>
        {
            item.ToTable("salvage_item");
            item.HasKey(s => new { s.TenantId, s.Id });
            item.HasAuditColumns();
            item.Property(s => s.Description).HasMaxLength(200);
            item.Property(s => s.DisposalMethod).HasMaxLength(10);
            item.Property(s => s.Proceeds).HasPrecision(18, 2);
        });

        // Exposure by zone and peril, for reinsurance (Phase 6). Mapped, not yet written.
        modelBuilder.Entity<ExposureSnapshot>(snapshot =>
        {
            snapshot.ToTable("exposure_snapshot");
            snapshot.HasKey(s => new { s.TenantId, s.AsAt, s.Zone, s.PerilCode });
            snapshot.Property(s => s.Zone).HasMaxLength(15);
            snapshot.Property(s => s.PerilCode).HasMaxLength(10);
            snapshot.Property(s => s.SumInsured).HasPrecision(20, 2);
            snapshot.Property(s => s.Currency).HasColumnType("char(3)");
        });
    }

    private static void Root<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> root) where T : BuildingBlocks.Domain.AggregateRoot<Guid>
    {
        root.HasKey(r => new { r.TenantId, r.Id });
        root.HasAuditColumns();
        root.Ignore(r => r.DomainEvents);
        root.Ignore(r => r.HasChanges);
    }
}

/// <summary>Exposure by zone and peril (general.exposure_snapshot), refreshed for reinsurance aggregation. TODO(spec): refreshed in Phase 6 (OQ-059).</summary>
public sealed class ExposureSnapshot : BuildingBlocks.Domain.ITenantScoped
{
    public Guid TenantId { get; set; }

    public DateTimeOffset RefreshedAt { get; set; }

    public DateOnly AsAt { get; set; }

    public string Zone { get; set; } = "";

    public string PerilCode { get; set; } = "";

    public decimal SumInsured { get; set; }

    public string Currency { get; set; } = "";
}
