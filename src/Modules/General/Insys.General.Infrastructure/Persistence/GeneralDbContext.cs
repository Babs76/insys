/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using System.Text.Json;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.General.Application;
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

    public DbSet<AggregationAlert> AggregationAlerts => Set<AggregationAlert>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        GeneralStaticData.Configure(modelBuilder);

        modelBuilder.Entity<RiskObject>(risk =>
        {
            risk.ToTable("risk_object");
            Root(risk);
            risk.Property(x => x.ValuationCurrency).HasColumnType("char(3)"); // BR-033
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
            quote.Property(x => x.OrgNodeCode).HasMaxLength(15);
            quote.Property(x => x.ChannelCode).HasMaxLength(12);
            quote.ToTable("quote");
            Root(quote);
            quote.Property(q => q.QuoteNumber).HasMaxLength(15);
            quote.Property(q => q.QuoteVersion).HasColumnType("smallint");
            quote.Property(q => q.ProductCode).HasMaxLength(15);
            quote.Property(q => q.IntermediaryCode).HasMaxLength(15);
            quote.Property(q => q.Status).HasConversion(new UpperCaseEnumConverter<QuoteStatus>()).HasMaxLength(12);
            quote.Property(q => q.Premium).HasPrecision(18, 2);
            quote.Property(q => q.Currency).HasColumnType("char(3)");
            quote.Property(q => q.RatingIssueCode).HasMaxLength(50);
            quote.Property(q => q.RatingIssue).HasMaxLength(500);
            quote.Property(q => q.PremiumFrequency).HasMaxLength(20);
            quote.Property(q => q.PremiumPeriods).HasColumnType("smallint");
            quote.Property(q => q.PremiumLoadingPct).HasPrecision(7, 4);
            quote.Ignore(q => q.InstalmentPremium);
            quote.Ignore(q => q.TotalPayable);
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
            policy.Property(x => x.OrgNodeCode).HasMaxLength(15);
            policy.Property(x => x.ChannelCode).HasMaxLength(12);
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
            policy.Property(p => p.InstalmentLoadingPct).HasPrecision(7, 4);
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
        modelBuilder.Entity<PolicyEffect>(effect =>
        {
            effect.ToTable("policy_effect"); // BR-038
            effect.HasKey(e => new { e.TenantId, e.Id });
            effect.HasAuditColumns(updatable: false);
            effect.Property(e => e.EffectType).HasMaxLength(20);
        });

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
            endorsement.Property(x => x.Currency).HasColumnType("char(3)"); // BR-033
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
            claim.Property(x => x.OrgNodeCode).HasMaxLength(15);
            claim.Property(x => x.ChannelCode).HasMaxLength(12);
            claim.Property(x => x.ProductCode).HasMaxLength(15);
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
            claim.Ignore(c => c.PartlyApproved);
            claim.Property(c => c.ClaimedAmount).HasPrecision(18, 2);
            claim.Property(c => c.DecisionOutcome).HasMaxLength(20);
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
            assessment.Property(x => x.Currency).HasColumnType("char(3)"); // BR-033
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
            recovery.Property(x => x.Currency).HasColumnType("char(3)"); // BR-033
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

        // Exposure by zone and peril, for reinsurance; refreshed daily by the General jobs (ADR-015).
        modelBuilder.Entity<ExposureSnapshot>(snapshot =>
        {
            snapshot.ToTable("exposure_snapshot");
            snapshot.HasKey(s => new { s.TenantId, s.AsAt, s.Zone, s.PerilCode });
            snapshot.Property(s => s.Zone).HasMaxLength(15);
            snapshot.Property(s => s.PerilCode).HasMaxLength(10);
            snapshot.Property(s => s.SumInsured).HasPrecision(20, 2);
            snapshot.Property(s => s.Currency).HasColumnType("char(3)");
        });

        // A zone and peril at or above its alert level on a day (GAP-051).
        modelBuilder.Entity<AggregationAlert>(alert =>
        {
            alert.ToTable("aggregation_alert");
            alert.HasKey(a => new { a.TenantId, a.Id });
            alert.Property(a => a.Zone).HasMaxLength(15);
            alert.Property(a => a.PerilCode).HasMaxLength(10);
            alert.Property(a => a.Currency).HasColumnType("char(3)");
            alert.Property(a => a.Exposure).HasPrecision(20, 2);
            alert.Property(a => a.MaxExposure).HasPrecision(20, 2);
            alert.Property(a => a.AlertAtPct).HasPrecision(5, 2);
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

/// <summary>Exposure by zone and peril (general.exposure_snapshot), refreshed daily for reinsurance aggregation (ADR-015).</summary>
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

/// <summary>Table general.aggregation_alert (GAP-051): recorded once a day per zone, peril and currency.</summary>
public sealed class AggregationAlert : BuildingBlocks.Domain.ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Zone { get; set; } = "";

    public string PerilCode { get; set; } = "";

    public string Currency { get; set; } = "";

    public decimal Exposure { get; set; }

    public decimal MaxExposure { get; set; }

    public decimal AlertAtPct { get; set; }

    public DateOnly AsAt { get; set; }

    public DateTimeOffset RaisedAt { get; set; }

    public AggregationAlertView View() => new(Id, Zone, PerilCode, Currency, Exposure, MaxExposure, AlertAtPct,
        Math.Round(Exposure / MaxExposure * 100m, 2), AsAt, RaisedAt, Contracts.AggregationZones.Reference(Zone, PerilCode));
}

/// <summary>The recorded aggregation alerts (GAP-051), across the tenant whatever the reader's scope: a capacity limit counts every policy.</summary>
internal sealed class AggregationAlerts(GeneralDbContext db) : IAggregationAlerts
{
    public async Task<IReadOnlyList<AggregationAlertView>> List(ListAggregationAlerts query, CancellationToken cancellationToken)
    {
        var alerts = db.AggregationAlerts.AsNoTracking();
        if (query.Zone is { } zone)
        {
            alerts = alerts.Where(a => a.Zone == zone);
        }

        if (query.Peril is { } peril)
        {
            alerts = alerts.Where(a => a.PerilCode == peril);
        }

        if (query.ZoneReference is { } reference)
        {
            // The reference is a hash of the zone and peril: find which of the tenant's alerted pairs it is.
            var pairs = await db.AggregationAlerts.AsNoTracking().Select(a => new { a.Zone, a.PerilCode }).Distinct().ToListAsync(cancellationToken);
            if (pairs.FirstOrDefault(p => Contracts.AggregationZones.Reference(p.Zone, p.PerilCode) == reference) is not { } pair)
            {
                return [];
            }

            alerts = alerts.Where(a => a.Zone == pair.Zone && a.PerilCode == pair.PerilCode);
        }

        if (query.From is { } from)
        {
            alerts = alerts.Where(a => a.AsAt >= from);
        }

        if (query.To is { } to)
        {
            alerts = alerts.Where(a => a.AsAt <= to);
        }

        if (query.AfterId is { } after)
        {
            alerts = alerts.Where(a => a.Id.CompareTo(after) > 0);
        }

        return (await alerts.OrderBy(a => a.Id).Take(query.Limit).ToListAsync(cancellationToken)).Select(a => a.View()).ToList();
    }

    public async Task<AggregationAlertView?> Get(Guid id, CancellationToken cancellationToken) =>
        (await db.AggregationAlerts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken))?.View();
}

