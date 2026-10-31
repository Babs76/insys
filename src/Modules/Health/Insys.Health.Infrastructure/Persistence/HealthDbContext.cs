/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using System.Linq.Expressions;
using System.Text.Json;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Benefits;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Members;
using Insys.Health.Domain.Payments;
using Insys.Health.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>The Health module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class HealthDbContext(DbContextOptions<HealthDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "health";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string Schema => SchemaName;

    public DbSet<HealthScheme> Schemes => Set<HealthScheme>();

    public DbSet<HealthMembership> Memberships => Set<HealthMembership>();

    public DbSet<BenefitAccumulator> Accumulators => Set<BenefitAccumulator>();

    public DbSet<EligibilityDecision> EligibilityDecisions => Set<EligibilityDecision>();

    public DbSet<Preauthorisation> Preauthorisations => Set<Preauthorisation>();

    public DbSet<MedicalClaim> Claims => Set<MedicalClaim>();

    public DbSet<UtilisationCase> UtilisationCases => Set<UtilisationCase>();

    public DbSet<ComplexCase> ComplexCases => Set<ComplexCase>();

    public DbSet<ClinicalReferral> ClinicalReferrals => Set<ClinicalReferral>();

    public DbSet<Domain.Providers.ProviderMeasureResult> ProviderMeasureResults => Set<Domain.Providers.ProviderMeasureResult>();

    public DbSet<Domain.Providers.ProviderQualityReview> ProviderQualityReviews => Set<Domain.Providers.ProviderQualityReview>();

    public DbSet<Domain.Providers.ProviderStatement> ProviderStatements => Set<Domain.Providers.ProviderStatement>();

    public DbSet<ProviderRecovery> ProviderRecoveries => Set<ProviderRecovery>();

    public DbSet<ProviderCreditNote> ProviderCreditNotes => Set<ProviderCreditNote>();

    public DbSet<HealthFraudCase> FraudCases => Set<HealthFraudCase>();

    public DbSet<ProviderPaymentRun> Runs => Set<ProviderPaymentRun>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        HealthStaticData.Configure(modelBuilder);
        ConfigureMembers(modelBuilder);
        ConfigureCare(modelBuilder);
        ConfigureRuns(modelBuilder);
    }

    private static void ConfigureMembers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HealthScheme>(scheme =>
        {
            scheme.ToTable("scheme");
            Root(scheme);
            scheme.Property(s => s.SchemeNumber).HasMaxLength(12);
            scheme.Property(s => s.SponsorCode).HasMaxLength(15);
            scheme.Property(s => s.Status).HasConversion(new UpperCaseEnumConverter<SchemeStatus>()).HasMaxLength(10);
            scheme.Property(s => s.Currency).HasColumnType("char(3)");
        });

        modelBuilder.Entity<HealthMembership>(membership =>
        {
            membership.ToTable("membership");
            Root(membership);
            membership.Property(m => m.MemberNumber).HasMaxLength(12);
            membership.Property(m => m.PlanCode).HasMaxLength(15);
            membership.Property(m => m.OrgNodeCode).HasMaxLength(15);
            membership.Property(m => m.ProductCode).HasMaxLength(15);
            membership.Property(m => m.ChannelCode).HasMaxLength(12);
            membership.Property(m => m.OptionCode).HasMaxLength(10);
            membership.Property(m => m.Status).HasConversion(new UpperCaseEnumConverter<MembershipStatus>()).HasMaxLength(12);
            membership.Property(m => m.MemberCategory).HasMaxLength(10);
            membership.Property(m => m.Currency).HasColumnType("char(3)");
            membership.Property(m => m.WaitingPeriods).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json), json => JsonSerializer.Deserialize<List<WaitingPeriod>>(json, Json) ?? new List<WaitingPeriod>(),
                new ValueComparer<List<WaitingPeriod>>((a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(), v => v.ToList()));
            Children(membership, m => m.Dependants, "MembershipId");
            Children(membership, m => m.Events, "MembershipId");
        });

        modelBuilder.Entity<Dependant>(dependant =>
        {
            dependant.ToTable("member_dependant");
            Child(dependant);
            dependant.Property(d => d.DependantType).HasMaxLength(10);
            dependant.Property(d => d.Suffix).HasColumnType("char(2)");
            dependant.Property(d => d.Status).HasMaxLength(12);
        });

        modelBuilder.Entity<MembershipEvent>(membershipEvent =>
        {
            membershipEvent.ToTable("membership_event");
            membershipEvent.HasKey(e => new { e.TenantId, e.Id });
            membershipEvent.HasAuditColumns(updatable: false);
            membershipEvent.Property(e => e.EventType).HasMaxLength(15);
            membershipEvent.Property(e => e.Reason).HasMaxLength(30);
        });

        modelBuilder.Entity<BenefitAccumulator>(accumulator =>
        {
            accumulator.ToTable("benefit_accumulator");
            Root(accumulator);
            accumulator.Ignore(a => a.Key);
            accumulator.Ignore(a => a.State);
            accumulator.Ignore(a => a.Remaining);
            accumulator.Property(a => a.BenefitCategory).HasMaxLength(15);
            accumulator.Property(a => a.Scope).HasMaxLength(10);
            accumulator.Property(a => a.LimitAmount).HasPrecision(18, 2);
            accumulator.Property(a => a.UsedAmount).HasPrecision(18, 2);
            accumulator.Property(a => a.ReservedAmount).HasPrecision(18, 2);
            accumulator.Property(a => a.DeductibleMet).HasPrecision(18, 2);
            accumulator.Property(a => a.CopayTaken).HasPrecision(18, 2);
        });

        // Insert-only and partitioned by decided_at: no row_version, and decided_at is part of the key.
        modelBuilder.Entity<EligibilityDecision>(decision =>
        {
            decision.ToTable("eligibility_decision");
            decision.HasKey(d => new { d.TenantId, d.Id, d.DecidedAt });
            decision.HasAuditColumns(updatable: false);
            decision.Ignore(d => d.DomainEvents);
            decision.Ignore(d => d.HasChanges);
            decision.Ignore(d => d.RowVersion);
            decision.Property(d => d.MemberNumber).HasMaxLength(12);
            decision.Property(d => d.DependantSuffix).HasColumnType("char(2)");
            decision.Property(d => d.ProviderId).HasMaxLength(15);
            decision.Property(d => d.ServiceType).HasMaxLength(20);
            decision.Property(d => d.ResponseCode).HasMaxLength(10);
        });
    }

    private static void ConfigureCare(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Preauthorisation>(preauth =>
        {
            preauth.ToTable("preauthorisation");
            Root(preauth);
            preauth.Property(p => p.AuthNumber).HasMaxLength(15);
            preauth.Property(p => p.ProviderId).HasMaxLength(15);
            preauth.Property(p => p.RequestedAmount).HasPrecision(18, 2);
            preauth.Property(p => p.AuthorisedAmount).HasPrecision(18, 2);
            preauth.Property(p => p.Status).HasConversion(new UpperCaseEnumConverter<PreauthStatus>()).HasMaxLength(30);
            preauth.Property(p => p.ServiceType).HasMaxLength(15);
            preauth.Property(p => p.DecisionReason).HasMaxLength(30);
            Children(preauth, p => p.Lines, "PreauthId");
        });

        modelBuilder.Entity<PreauthLine>(line =>
        {
            line.ToTable("preauth_line");
            Child(line);
            line.Property(l => l.ProcedureCode).HasMaxLength(10);
            line.Property(l => l.Quantity).HasColumnType("smallint");
            line.Property(l => l.AuthorisedAmount).HasPrecision(18, 2);
            line.Property(l => l.BenefitCategory).HasMaxLength(15);
            line.Property(l => l.ReservedAmount).HasPrecision(18, 2);
            line.Property(l => l.ConsumedAmount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<MedicalClaim>(claim =>
        {
            claim.ToTable("medical_claim");
            Root(claim);
            claim.Property(c => c.ClaimNumber).HasMaxLength(25);
            claim.Property(c => c.Source).HasMaxLength(10);
            claim.Property(c => c.ProviderId).HasMaxLength(15);
            claim.Property(c => c.FormatCode).HasMaxLength(15);
            claim.Property(c => c.TotalClaimed).HasPrecision(18, 2);
            claim.Property(c => c.TotalPaid).HasPrecision(18, 2);
            claim.Property(c => c.Status).HasConversion(new UpperCaseEnumConverter<MedicalClaimStatus>()).HasMaxLength(30);
            claim.Property(c => c.Currency).HasColumnType("char(3)");
            claim.Property(c => c.PendReason).HasMaxLength(30);
            claim.Ignore(c => c.PartlyApproved);
            claim.Property(c => c.DecisionOutcome).HasMaxLength(20);
            claim.Property(c => c.DecisionReasonCode).HasMaxLength(20);
            Children(claim, c => c.Lines, "ClaimId");
        });

        modelBuilder.Entity<MedicalClaimLine>(line =>
        {
            line.ToTable("medical_claim_line");
            Child(line);
            line.Property(l => l.LineNo).HasColumnType("smallint");
            line.Property(l => l.TariffCode).HasMaxLength(20);
            line.Property(l => l.DiagnosisCode).HasMaxLength(10);
            line.Property(l => l.Quantity).HasColumnType("smallint");
            line.Property(l => l.ClaimedAmount).HasPrecision(18, 2);
            line.Property(l => l.AllowedAmount).HasPrecision(18, 2);
            line.Property(l => l.CopayAmount).HasPrecision(18, 2);
            line.Property(l => l.PaidAmount).HasPrecision(18, 2);
            line.Property(l => l.AdjustmentReason).HasMaxLength(30);
            line.Property(l => l.BenefitCategory).HasMaxLength(15);
            line.Property(l => l.NetworkStatus).HasMaxLength(15);
            line.Property(l => l.TariffAmount).HasPrecision(18, 2);
            line.Property(l => l.DeductibleAmount).HasPrecision(18, 2);
            line.Property(l => l.MemberLiability).HasPrecision(18, 2);
            line.Property(l => l.Outcome).HasMaxLength(10);
            line.Property(l => l.DaysSupply).HasColumnType("smallint");
            line.Property(l => l.PrescriptionNumber).HasMaxLength(30);
            line.Property(l => l.PrescriberRef).HasMaxLength(30);
            line.Property(l => l.RepeatNumber).HasColumnType("smallint");
            line.Property(l => l.RepeatsAuthorised).HasColumnType("smallint");
            line.Property(l => l.PaidQuantity).HasColumnType("smallint");
            line.Property(l => l.ActiveIngredient).HasMaxLength(100);
            line.Property(l => l.FormularyTier).HasMaxLength(40);
        });

        modelBuilder.Entity<ComplexCase>(complex =>
        {
            complex.ToTable("complex_case"); // Wave 5a §4.3, F034 to F037
            Root(complex);
            complex.Ignore(c => c.CurrentPlan);
            complex.Property(c => c.CaseNumber).HasMaxLength(25);
            complex.Property(c => c.OpenedFrom).HasMaxLength(15);
            complex.Property(c => c.ConditionCategory).HasMaxLength(120);
            complex.Property(c => c.Status).HasMaxLength(10);
            complex.Property(c => c.CloseReason).HasMaxLength(500);
            complex.HasMany(c => c.Assignments).WithOne().HasForeignKey(a => new { a.TenantId, a.ComplexCaseId });
            complex.HasMany(c => c.Plans).WithOne().HasForeignKey(p => new { p.TenantId, p.ComplexCaseId });
            complex.HasMany(c => c.Notes).WithOne().HasForeignKey(n => new { n.TenantId, n.ComplexCaseId });
            complex.Navigation(c => c.Assignments).UsePropertyAccessMode(PropertyAccessMode.Field);
            complex.Navigation(c => c.Plans).UsePropertyAccessMode(PropertyAccessMode.Field);
            complex.Navigation(c => c.Notes).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ComplexCaseAssignment>(assignment =>
        {
            assignment.ToTable("complex_case_assignment");
            Child(assignment);
            assignment.Property(a => a.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<CarePlan>(plan =>
        {
            plan.ToTable("care_plan");
            Child(plan);
            plan.Property(p => p.ChangeReason).HasMaxLength(500);
            plan.HasMany(p => p.Items).WithOne().HasForeignKey(i => new { i.TenantId, i.CarePlanId });
            plan.Navigation(p => p.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CarePlanItem>(item =>
        {
            item.ToTable("care_plan_item");
            Child(item);
            item.Property(i => i.Kind).HasMaxLength(10);
            item.Property(i => i.Description).HasMaxLength(1000);
            item.Property(i => i.Responsible).HasMaxLength(120);
            item.Property(i => i.Status).HasMaxLength(10);
            item.Property(i => i.ProgressNote).HasMaxLength(1000);
        });

        modelBuilder.Entity<CareCoordinationNote>(note =>
        {
            note.ToTable("care_coordination_note");
            Child(note);
            note.Property(n => n.ContactType).HasMaxLength(10);
            note.Property(n => n.ContactRef).HasMaxLength(120);
            note.Property(n => n.Note).HasMaxLength(2000);
            note.Property(n => n.SharedWithProviderId).HasMaxLength(15);
            note.Property(n => n.ConsentReference).HasMaxLength(120);
        });

        modelBuilder.Entity<ClinicalReferral>(referral =>
        {
            referral.ToTable("clinical_referral"); // Wave 5a §4.5, F038
            Root(referral);
            referral.Property(r => r.ReferralNumber).HasMaxLength(25);
            referral.Property(r => r.ReferringProviderId).HasMaxLength(15);
            referral.Property(r => r.ReferredProviderId).HasMaxLength(15);
            referral.Property(r => r.ReferredSpecialty).HasMaxLength(40);
            referral.Property(r => r.ClinicalReason).HasMaxLength(1000);
            referral.Property(r => r.ConsentReference).HasMaxLength(120);
            referral.Property(r => r.Status).HasMaxLength(10);
            referral.Property(r => r.DeclineReason).HasMaxLength(500);
        });

        modelBuilder.Entity<ProviderRecovery>(recovery =>
        {
            recovery.ToTable("provider_recovery"); // Wave 5a §4.7, F049
            Root(recovery);
            recovery.Property(r => r.RecoveryNumber).HasMaxLength(25);
            recovery.Property(r => r.DebtorKind).HasMaxLength(10);
            recovery.Property(r => r.ProviderId).HasMaxLength(15);
            recovery.Property(r => r.Source).HasMaxLength(12);
            recovery.Property(r => r.ClaimNumber).HasMaxLength(25);
            recovery.Property(r => r.OriginalAmount).HasPrecision(18, 2);
            recovery.Property(r => r.Balance).HasPrecision(18, 2);
            recovery.Property(r => r.RepayAmount).HasPrecision(18, 2);
            recovery.Property(r => r.Currency).HasMaxLength(3).IsFixedLength();
            recovery.Property(r => r.Basis).HasMaxLength(10);
            recovery.Property(r => r.Status).HasMaxLength(12);
            recovery.Property(r => r.Reason).HasMaxLength(500);
            recovery.Property(r => r.WriteOffReason).HasMaxLength(500);
            recovery.Ignore(r => r.Taken);
            recovery.Ignore(r => r.Offsettable);
            recovery.Ignore(r => r.WriteOffHash);
            recovery.Ignore(r => r.FindingHash);
            Children(recovery, r => r.Offsets, "RecoveryId");
        });

        modelBuilder.Entity<ProviderRecoveryOffset>(offset =>
        {
            offset.ToTable("provider_recovery_offset");
            Child(offset);
            offset.Property(o => o.Amount).HasPrecision(18, 2);
            offset.Property(o => o.Status).HasMaxLength(10);
        });

        modelBuilder.Entity<ProviderCreditNote>(note =>
        {
            note.ToTable("provider_credit_note"); // Wave 5a §4.6, F048
            Root(note);
            note.Property(n => n.CreditNoteNumber).HasMaxLength(25);
            note.Property(n => n.ProviderId).HasMaxLength(15);
            note.Property(n => n.ProviderReference).HasMaxLength(40);
            note.Property(n => n.Amount).HasPrecision(18, 2);
            note.Property(n => n.Currency).HasMaxLength(3).IsFixedLength();
            note.Property(n => n.Status).HasMaxLength(10);
            note.Property(n => n.Reason).HasMaxLength(500);
            note.Ignore(n => n.SubjectHash);
        });

        modelBuilder.Entity<Domain.Providers.ProviderMeasureResult>(result =>
        {
            result.ToTable("provider_measure_result"); // Wave 5a §4.1, F028
            Root(result);
            result.Property(r => r.ProviderId).HasMaxLength(15);
            result.Property(r => r.MonthKey).HasMaxLength(7).IsFixedLength();
            result.Property(r => r.MeasureCode).HasMaxLength(25);
            result.Property(r => r.Value).HasPrecision(18, 4);
            result.Property(r => r.Threshold).HasPrecision(18, 4);
            result.Property(r => r.IsBreached).HasColumnName("breached");
        });

        modelBuilder.Entity<Domain.Providers.ProviderQualityReview>(review =>
        {
            review.ToTable("provider_quality_review"); // F029
            Root(review);
            review.Property(r => r.ProviderId).HasMaxLength(15);
            review.Property(r => r.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<Domain.Providers.ProviderStatement>(statement =>
        {
            statement.ToTable("provider_statement"); // F030
            Root(statement);
            statement.Property(s => s.ProviderId).HasMaxLength(15);
            statement.Property(s => s.StatementRef).HasMaxLength(40);
            statement.Property(s => s.Currency).HasMaxLength(3).IsFixedLength();
            statement.Property(s => s.Status).HasMaxLength(10);
            statement.HasMany(s => s.Lines).WithOne().HasForeignKey(l => new { l.TenantId, l.StatementId });
            statement.Navigation(s => s.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Domain.Providers.ProviderStatementLine>(line =>
        {
            line.ToTable("provider_statement_line");
            Child(line);
            line.Ignore(l => l.Open);
            line.Property(l => l.Reference).HasMaxLength(25);
            line.Property(l => l.StatementAmount).HasPrecision(18, 2);
            line.Property(l => l.OurAmount).HasPrecision(18, 2);
            line.Property(l => l.MatchResult).HasMaxLength(20);
            line.Property(l => l.Resolution).HasMaxLength(500);
        });

        modelBuilder.Entity<UtilisationCase>(utilisation =>
        {
            utilisation.ToTable("utilisation_case");
            Root(utilisation);
            utilisation.Property(c => c.RuleCode).HasMaxLength(15);
            utilisation.Property(c => c.Status).HasConversion(new UpperCaseEnumConverter<UtilisationStatus>()).HasMaxLength(12);
            utilisation.Property(c => c.Outcome).HasMaxLength(20);
        });

        modelBuilder.Entity<HealthFraudCase>(fraud =>
        {
            fraud.ToTable("fraud_case");
            Root(fraud);
            fraud.Property(c => c.SubjectType).HasMaxLength(12);
            fraud.Property(c => c.SubjectRef).HasMaxLength(30);
            fraud.Property(c => c.Status).HasConversion(new UpperCaseEnumConverter<FraudStatus>()).HasMaxLength(30);
            fraud.Property(c => c.OutcomeCode).HasMaxLength(15);
        });
    }

    private static void ConfigureRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderPaymentRun>(run =>
        {
            run.ToTable("provider_payment_run");
            Root(run);
            run.Ignore(r => r.SubjectHash);
            run.Property(r => r.RunCode).HasMaxLength(15);
            run.Property(r => r.TotalAmount).HasPrecision(18, 2);
            run.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<RunStatus>()).HasMaxLength(12);
            run.Property(r => r.Currency).HasColumnType("char(3)");
            Children(run, r => r.Remittances, "RunId");
        });

        modelBuilder.Entity<RemittanceAdvice>(remittance =>
        {
            remittance.ToTable("remittance_advice");
            remittance.HasKey(r => new { r.TenantId, r.Id });
            remittance.HasAuditColumns(updatable: false);
            remittance.Property(r => r.ProviderId).HasMaxLength(15);
            remittance.Property(r => r.Amount).HasPrecision(18, 2);
        });
    }

    private static void Root<T>(EntityTypeBuilder<T> root) where T : BuildingBlocks.Domain.AggregateRoot<Guid>
    {
        root.HasKey(r => new { r.TenantId, r.Id });
        root.HasAuditColumns();
        root.Ignore(r => r.DomainEvents);
        root.Ignore(r => r.HasChanges);
    }

    private static void Child<T>(EntityTypeBuilder<T> child) where T : class
    {
        child.HasKey("TenantId", "Id");
        child.HasAuditColumns();
    }

    private static void Children<TRoot, TChild>(EntityTypeBuilder<TRoot> root, Expression<Func<TRoot, IEnumerable<TChild>?>> navigation, string foreignKey)
        where TRoot : class
        where TChild : class
    {
        var collection = root.HasMany(navigation).WithOne().HasForeignKey("TenantId", foreignKey).OnDelete(DeleteBehavior.Cascade).Metadata.PrincipalToDependent!;
        root.Navigation(collection.Name).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
