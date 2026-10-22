using System.Linq.Expressions;
using System.Text.Json;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Groups;
using Insys.Life.Domain.Policies;
using Insys.Life.Domain.Proposals;
using Insys.Life.Domain.Underwriting;
using Insys.Life.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>The Life module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class LifeDbContext(DbContextOptions<LifeDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "life";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string Schema => SchemaName;

    public DbSet<LifeProposal> Proposals => Set<LifeProposal>();

    public DbSet<LifeUnderwritingCase> Cases => Set<LifeUnderwritingCase>();

    public DbSet<LifePolicy> Policies => Set<LifePolicy>();

    public DbSet<LifePolicyLoan> Loans => Set<LifePolicyLoan>();

    public DbSet<LifeClaim> Claims => Set<LifeClaim>();

    public DbSet<GroupScheme> Schemes => Set<GroupScheme>();

    public DbSet<CashValue> CashValues => Set<CashValue>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        LifeStaticData.Configure(modelBuilder);
        ConfigureProposals(modelBuilder);
        ConfigurePolicies(modelBuilder);
        ConfigureClaimsAndSchemes(modelBuilder);
    }

    private static void ConfigureProposals(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LifeProposal>(proposal =>
        {
            proposal.ToTable("proposal");
            Root(proposal);
            proposal.Ignore(p => p.Billing);
            proposal.Ignore(p => p.TotalSumAssured);
            proposal.Property(p => p.ProposalNumber).HasMaxLength(15);
            proposal.Property(p => p.ProductCode).HasMaxLength(15);
            proposal.Property(p => p.IntermediaryCode).HasMaxLength(15);
            proposal.Property(p => p.ChannelCode).HasMaxLength(12);
            proposal.Property(p => p.Status).HasConversion(new UpperCaseEnumConverter<ProposalStatus>()).HasMaxLength(15);
            proposal.Property(p => p.Currency).HasColumnType("char(3)");
            proposal.Property(p => p.Frequency).HasMaxLength(10);
            proposal.Property(p => p.QuotedPremium).HasPrecision(18, 2);
            proposal.Property(p => p.CollectionMethod).HasMaxLength(15);
            proposal.Property(p => p.EligibilityOutcome).HasMaxLength(15);
            proposal.Property(p => p.RulesetCode).HasMaxLength(30);
            proposal.Property(p => p.BeneficiaryNominations).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                json => JsonSerializer.Deserialize<List<BeneficiaryNomination>>(json, Json) ?? new List<BeneficiaryNomination>(),
                Comparer<List<BeneficiaryNomination>>(v => v.ToList()));
            Children(proposal, p => p.Lives, "ProposalId");
            Children(proposal, p => p.Covers, "ProposalId");
            Children(proposal, p => p.Disclosures, "ProposalId");
        });

        modelBuilder.Entity<ProposalInsuredLife>(life =>
        {
            life.ToTable("proposal_insured_life");
            Child(life);
            life.Property(l => l.LifeRole).HasMaxLength(10);
            life.Property(l => l.AgeNext).HasColumnType("smallint");
            life.Property(l => l.SmokerStatus).HasMaxLength(10);
            life.Property(l => l.OccupationCode).HasMaxLength(10);
        });

        modelBuilder.Entity<RequestedCover>(cover =>
        {
            cover.ToTable("proposal_cover");
            Child(cover);
            cover.Property(c => c.BenefitCode).HasMaxLength(20);
            cover.Property(c => c.SumAssured).HasPrecision(18, 2);
            cover.Property(c => c.TermYears).HasColumnType("smallint");
            cover.Property(c => c.Premium).HasPrecision(18, 2);
            cover.HasOne<ProposalInsuredLife>().WithMany().HasForeignKey("TenantId", nameof(RequestedCover.InsuredLifeId)).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<DisclosureAnswer>(answer =>
        {
            answer.ToTable("disclosure_answer");
            Child(answer);
            answer.Property(a => a.QuestionCode).HasMaxLength(20);
            answer.Property(a => a.Answer).HasColumnType("jsonb");
            answer.HasOne<ProposalInsuredLife>().WithMany().HasForeignKey("TenantId", nameof(DisclosureAnswer.InsuredLifeId)).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<LifeUnderwritingCase>(underwriting =>
        {
            underwriting.ToTable("underwriting_case");
            Root(underwriting);
            underwriting.Ignore(c => c.DecisionPending);
            underwriting.Ignore(c => c.SubjectHash);
            underwriting.Property(c => c.Status).HasConversion(new UpperCaseEnumConverter<CaseStatus>()).HasMaxLength(30);
            underwriting.Property(c => c.Outcome).HasConversion(new UpperCaseEnumConverter<UnderwritingOutcome>()).HasMaxLength(10);
            underwriting.Property(c => c.RulesetCode).HasMaxLength(30);
            underwriting.Property(c => c.DecisionCode).HasMaxLength(15);
            underwriting.Property(c => c.DecisionReason).HasMaxLength(20);
            underwriting.Property(c => c.AuthorityRole).HasMaxLength(30);
            underwriting.Property(c => c.ReferredTo).HasMaxLength(30);
            Children(underwriting, c => c.Requirements, "CaseId");
            Children(underwriting, c => c.Items, "CaseId");
        });

        modelBuilder.Entity<UnderwritingRequirement>(requirement =>
        {
            requirement.ToTable("underwriting_requirement");
            Child(requirement);
            requirement.Ignore(r => r.Outstanding);
            requirement.Property(r => r.EvidenceType).HasMaxLength(20);
            requirement.Property(r => r.Status).HasMaxLength(12);
            requirement.Property(r => r.WaiverReason).HasMaxLength(200);
        });

        modelBuilder.Entity<UnderwritingDecisionItem>(item =>
        {
            item.ToTable("underwriting_decision_item");
            Child(item);
            item.Property(i => i.BenefitCode).HasMaxLength(20);
            item.Property(i => i.DecisionCode).HasMaxLength(15);
            item.Property(i => i.LoadingPct).HasPrecision(7, 2);
            item.Property(i => i.LoadingPerMille).HasPrecision(9, 4);
            item.Property(i => i.ExclusionCode).HasMaxLength(20);
        });
    }

    private static void ConfigurePolicies(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LifePolicy>(policy =>
        {
            policy.ToTable("policy");
            Root(policy);
            policy.Ignore(p => p.Schedule);
            policy.Ignore(p => p.Billing);
            policy.Ignore(p => p.LapsedOn);
            policy.Property(p => p.PolicyNumber).HasMaxLength(15);
            policy.Property(p => p.ProductCode).HasMaxLength(15);
            policy.Property(p => p.Status).HasConversion(new UpperCaseEnumConverter<LifePolicyStatus>()).HasMaxLength(12);
            policy.Property(p => p.Currency).HasColumnType("char(3)");
            policy.Property(p => p.PremiumFrequency).HasMaxLength(10);
            policy.Property(p => p.IntermediaryCode).HasMaxLength(15);
            policy.Property(p => p.CollectionMethod).HasMaxLength(15);
            Children(policy, p => p.Lives, "PolicyId");
            Children(policy, p => p.Benefits, "PolicyId");
            Children(policy, p => p.Beneficiaries, "PolicyId");
            Children(policy, p => p.Schedules, "PolicyId");
            Children(policy, p => p.Events, "PolicyId");
            Children(policy, p => p.LapseHistory, "PolicyId", "_lapses");
        });

        modelBuilder.Entity<PolicyInsuredLife>(life =>
        {
            life.ToTable("policy_insured_life");
            Child(life);
            life.Property(l => l.LifeRole).HasMaxLength(10);
            life.Property(l => l.EntryAge).HasColumnType("smallint");
        });

        modelBuilder.Entity<LifeBenefit>(benefit =>
        {
            benefit.ToTable("policy_benefit");
            Child(benefit);
            benefit.Property(b => b.BenefitCode).HasMaxLength(20);
            benefit.Property(b => b.SumAssured).HasPrecision(18, 2);
            benefit.Property(b => b.Premium).HasPrecision(18, 2);
            benefit.Property(b => b.LoadingPct).HasPrecision(7, 2);
            benefit.Property(b => b.Status).HasMaxLength(10);
        });

        modelBuilder.Entity<LifeBeneficiary>(beneficiary =>
        {
            beneficiary.ToTable("beneficiary");
            Child(beneficiary);
            beneficiary.Property(b => b.RelationshipCode).HasMaxLength(15);
            beneficiary.Property(b => b.SharePct).HasPrecision(5, 2);
        });

        modelBuilder.Entity<LifePremiumSchedule>(schedule =>
        {
            schedule.ToTable("premium_schedule");
            Child(schedule);
            schedule.Property(s => s.Frequency).HasMaxLength(10);
            schedule.Property(s => s.Premium).HasPrecision(18, 2);
            schedule.Property(s => s.MissedCount).HasColumnType("smallint");
        });

        // History rows are written once (insert-only).
        modelBuilder.Entity<PolicyEvent>(policyEvent =>
        {
            policyEvent.ToTable("policy_event");
            policyEvent.HasKey(e => new { e.TenantId, e.Id });
            policyEvent.HasAuditColumns(updatable: false);
            policyEvent.Property(e => e.EventType).HasMaxLength(20);
            policyEvent.Property(e => e.Before).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json), json => JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Json) ?? new Dictionary<string, string?>(),
                Comparer<Dictionary<string, string?>>(v => new Dictionary<string, string?>(v)));
            policyEvent.Property(e => e.After).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json), json => JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Json) ?? new Dictionary<string, string?>(),
                Comparer<Dictionary<string, string?>>(v => new Dictionary<string, string?>(v)));
        });

        modelBuilder.Entity<LapseReinstatement>(lapse =>
        {
            lapse.ToTable("lapse_reinstatement");
            lapse.HasKey(l => new { l.TenantId, l.Id });
            lapse.HasAuditColumns(updatable: false);
            lapse.Property(l => l.EventType).HasMaxLength(12);
            lapse.Property(l => l.Reason).HasMaxLength(20);
        });

        modelBuilder.Entity<LifePolicyLoan>(loan =>
        {
            loan.ToTable("policy_loan");
            Root(loan);
            loan.Property(l => l.LoanNumber).HasMaxLength(15);
            loan.Property(l => l.Principal).HasPrecision(18, 2);
            loan.Property(l => l.RateCode).HasMaxLength(15);
            loan.Property(l => l.Balance).HasPrecision(18, 2);
            loan.Property(l => l.Status).HasConversion(new UpperCaseEnumConverter<LoanStatus>()).HasMaxLength(10);
            Children(loan, l => l.Transactions, "LoanId");
        });

        modelBuilder.Entity<LoanTransaction>(transaction =>
        {
            transaction.ToTable("policy_loan_transaction");
            transaction.HasKey(t => new { t.TenantId, t.Id });
            transaction.HasAuditColumns(updatable: false);
            transaction.Property(t => t.TxnType).HasMaxLength(12);
            transaction.Property(t => t.Amount).HasPrecision(18, 2);
        });

        // Cash values come from valuation (OQ-052); Life only reads them.
        modelBuilder.Entity<CashValue>(value =>
        {
            value.ToTable("cash_value");
            value.HasKey(v => new { v.TenantId, v.Id });
            value.HasAuditColumns();
            value.Property(v => v.Value).HasColumnName("cash_value").HasPrecision(18, 2);
            value.Property(v => v.SurrenderValue).HasPrecision(18, 2);
            value.Property(v => v.ScaleCode).HasMaxLength(15);
            value.Property(v => v.RowVersion).IsConcurrencyToken();
        });
    }

    private static void ConfigureClaimsAndSchemes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LifeClaim>(claim =>
        {
            claim.ToTable("claim");
            Root(claim);
            claim.Ignore(c => c.IsOpen);
            claim.Ignore(c => c.ApprovalPending);
            claim.Ignore(c => c.SubjectHash);
            claim.Property(c => c.ClaimNumber).HasMaxLength(25);
            claim.Property(c => c.ClaimType).HasMaxLength(15);
            claim.Property(c => c.CauseCode).HasMaxLength(10);
            claim.Property(c => c.Status).HasConversion(new UpperCaseEnumConverter<LifeClaimStatus>()).HasMaxLength(15);
            claim.Property(c => c.Currency).HasColumnType("char(3)");
            claim.Property(c => c.AssessedAmount).HasPrecision(18, 2);
            claim.Property(c => c.ApprovedAmount).HasPrecision(18, 2);
            claim.Property(c => c.PaidAmount).HasPrecision(18, 2);
            claim.Property(c => c.ReserveType).HasMaxLength(15);
            claim.Property(c => c.DecisionReason).HasMaxLength(20);
            claim.Property(c => c.AuthorityRole).HasMaxLength(30);
            Children(claim, c => c.Lines, "ClaimId");
            Children(claim, c => c.Payees, "ClaimId");
        });

        modelBuilder.Entity<ClaimBenefitLine>(line =>
        {
            line.ToTable("claim_benefit_line");
            Child(line);
            line.Property(l => l.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<LifeClaimPayee>(payee =>
        {
            payee.ToTable("claim_payee");
            payee.HasKey(p => new { p.TenantId, p.Id });
            payee.HasAuditColumns(updatable: false);
            payee.Property(p => p.BankAccountHash).HasMaxLength(64);
            payee.Property(p => p.SharePct).HasPrecision(5, 2);
            payee.Property(p => p.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<GroupScheme>(scheme =>
        {
            scheme.ToTable("group_scheme");
            Root(scheme);
            scheme.Ignore(s => s.CurrentMembers);
            scheme.Property(s => s.SchemeNumber).HasMaxLength(12);
            scheme.Property(s => s.ProductCode).HasMaxLength(15);
            scheme.Property(s => s.SchemeType).HasMaxLength(15);
            scheme.Property(s => s.Status).HasConversion(new UpperCaseEnumConverter<SchemeStatus>()).HasMaxLength(10);
            scheme.Property(s => s.Currency).HasColumnType("char(3)");
            Children(scheme, s => s.Members, "SchemeId");
        });

        modelBuilder.Entity<GroupMember>(member =>
        {
            member.ToTable("group_member");
            Child(member);
            member.Ignore(m => m.Current);
            member.Property(m => m.CategoryCode).HasMaxLength(10);
            member.Property(m => m.AnnualSalary).HasPrecision(18, 2);
            member.Property(m => m.CoverAmount).HasPrecision(18, 2);
            member.Property(m => m.EvidenceStatus).HasMaxLength(15);
            member.Property(m => m.FreeCoverLimit).HasPrecision(18, 2);
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

    private static void Children<TRoot, TChild>(EntityTypeBuilder<TRoot> root, Expression<Func<TRoot, IEnumerable<TChild>?>> navigation, string foreignKey,
        string? field = null)
        where TRoot : class
        where TChild : class
    {
        var collection = root.HasMany(navigation).WithOne().HasForeignKey("TenantId", foreignKey).OnDelete(DeleteBehavior.Cascade).Metadata.PrincipalToDependent!;
        var builder = root.Navigation(collection.Name).UsePropertyAccessMode(PropertyAccessMode.Field);
        if (field is not null)
        {
            builder.HasField(field);
        }
    }

    private static ValueComparer<T> Comparer<T>(Func<T, T> snapshot) where T : class =>
        new((a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json), v => JsonSerializer.Serialize(v, Json).GetHashCode(), v => snapshot(v));
}

/// <summary>A policy's cash and surrender value on a valuation date (life.cash_value). TODO(spec): written by valuation once actuarial defines it (OQ-052).</summary>
public sealed class CashValue : BuildingBlocks.Domain.ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PolicyId { get; set; }

    public DateOnly ValuationDate { get; set; }

    public decimal Value { get; set; }

    public decimal SurrenderValue { get; set; }

    public string ScaleCode { get; set; } = "";

    public int ScaleVersion { get; set; }

    public long RowVersion { get; set; }
}
