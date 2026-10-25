/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory
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
using Insys.BuildingBlocks.Domain;

namespace Insys.ProductFactory.Domain.Products;

/// <summary>States from domain-model.json (product › ProductVersion), stored upper-case as record_status (§7.7).</summary>
public enum ProductVersionStatus
{
    Draft,
    Validated,
    Submitted,
    Approved,
    Published,
    Effective,
    Withdrawn,
    Retired,
}

public enum ProductVersionTrigger
{
    ValidateVersion,
    EditConfiguration,
    SubmitForApproval,
    ApprovalRejected,
    ApproveVersion,
    PublishVersion,
    EffectiveDateReached,
    WithdrawVersion,
    RetireVersion,
}

public enum ProductDomain
{
    Life,
    General,
    Health,
}

/// <summary>A coverage or benefit of the library in force: its domain must be the product's.</summary>
public sealed record CoverageDefinition(string Code, ProductDomain Domain);

/// <summary>product.cfg_benefit_dependency_rules: a benefit Requires or Excludes another.</summary>
public sealed record BenefitDependency(string Benefit, string Relation, string TargetBenefit);

/// <summary>What ValidateVersion checks a version against (PRD-004, PRD-009).</summary>
public sealed record ProductValidationContext(IReadOnlyDictionary<string, CoverageDefinition> Coverages, IReadOnlyList<BenefitDependency> Dependencies,
    DateOnly Today, DateOnly? SupersededFrom);

public sealed record ProductVersionValidated(Guid VersionId, string ProductCode, int Version, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Submitted for approval: Workflow is asked for a PRODUCT_PUBLICATION approval (PRD-006).</summary>
public sealed record ProductVersionSubmitted(Guid VersionId, string ProductCode, int Version, Guid MakerId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProductVersionApproved(Guid VersionId, string ProductCode, int Version, Guid CheckerId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProductVersionPublished(Guid VersionId, string ProductCode, int Version, ProductDomain Domain, DateOnly EffectiveFrom, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record ProductVersionWithdrawn(Guid VersionId, string ProductCode, int Version, DateOnly EffectiveTo, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProductRegistered(Guid VersionId, string ProductCode, ProductDomain Domain, string FamilyCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>When a policy's cover starts: when it is issued (and its period has started), or with the first payment.</summary>
public enum CoverStart
{
    Issue,
    FirstPayment,
}

/// <summary>Whether arrears from before a lapse must be paid before reinstatement (a business decision, configurable per version).</summary>
public enum ReinstatementArrears
{
    None,
    Required,
}

/// <summary>The stored state of a version, as the repository reads it from product.cfg_product_definitions_and_versions.</summary>
public sealed record ProductVersionState(Guid Id, string ProductCode, int Version, ProductDomain Domain, string Name, IReadOnlyList<string> Coverages,
    ProductVersionStatus Status, DateOnly EffectiveFrom, DateOnly? EffectiveTo, Guid? SupersedesId, string? FamilyCode, string? ClassCode, string? ApprovedBy,
    DateTimeOffset? ApprovedAt, long RowVersion, CoverStart CoverStartsOn = CoverStart.Issue, string? EligibilityRulesetCode = null,
    ReinstatementArrears ReinstatementArrears = ReinstatementArrears.None);

/// <summary>
/// The ProductVersion aggregate: one governed version of a product (§7.7). Published versions are immutable (PRD-002);
/// a change to a published product is a new version (PRD-003); a version is validated before publication (PRD-004,
/// PRD-009), its approved test cases pass (PRD-005), publication needs maker-checker approval (PRD-006), and it has an
/// effective date (PRD-007).
/// </summary>
public sealed class ProductVersion : AggregateRoot<Guid>
{
    public const int MaxCodeLength = 15;

    public static readonly StateMachine<ProductVersionStatus, ProductVersionTrigger> Lifecycle = new StateMachine<ProductVersionStatus, ProductVersionTrigger>()
        .Allow(ProductVersionStatus.Draft, ProductVersionTrigger.ValidateVersion, ProductVersionStatus.Validated)
        .Allow(ProductVersionStatus.Validated, ProductVersionTrigger.EditConfiguration, ProductVersionStatus.Draft)
        .Allow(ProductVersionStatus.Validated, ProductVersionTrigger.SubmitForApproval, ProductVersionStatus.Submitted)
        .Allow(ProductVersionStatus.Submitted, ProductVersionTrigger.ApprovalRejected, ProductVersionStatus.Draft)
        .Allow(ProductVersionStatus.Submitted, ProductVersionTrigger.ApproveVersion, ProductVersionStatus.Approved)
        .Allow(ProductVersionStatus.Approved, ProductVersionTrigger.PublishVersion, ProductVersionStatus.Published)
        .Allow(ProductVersionStatus.Published, ProductVersionTrigger.EffectiveDateReached, ProductVersionStatus.Effective)
        .Allow(ProductVersionStatus.Effective, ProductVersionTrigger.WithdrawVersion, ProductVersionStatus.Withdrawn)
        .Allow(ProductVersionStatus.Effective, ProductVersionTrigger.RetireVersion, ProductVersionStatus.Retired);

    private ProductVersion()
    {
    }

    public string ProductCode { get; private set; } = "";

    public int Version { get; private set; }

    public ProductDomain Domain { get; private set; }

    public string Name { get; private set; } = "";

    public IReadOnlyList<string> Coverages { get; private set; } = [];

    public ProductVersionStatus Status { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public DateOnly? EffectiveTo { get; private set; }

    /// <summary>The version this one replaces, closed at this version's effective date when this one is approved.</summary>
    public Guid? SupersedesId { get; private set; }

    public string? FamilyCode { get; private set; }

    public string? ClassCode { get; private set; }

    /// <summary>When cover starts on policies of this version (Phase 4 review, OQ-059).</summary>
    public CoverStart CoverStartsOn { get; private set; }

    /// <summary>The eligibility rule set a proposal on this version must pass (Phase 5 review, item 5).</summary>
    public string? EligibilityRulesetCode { get; private set; }

    public ReinstatementArrears ReinstatementArrears { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>True for a version created in this unit of work.</summary>
    public bool IsNew { get; private set; }

    /// <summary>Configuration can change only before submission: Draft, or Validated (which returns to Draft).</summary>
    public bool IsEditable => Status is ProductVersionStatus.Draft or ProductVersionStatus.Validated;

    /// <summary>Not yet published, withdrawn or retired: a product has at most one version in progress.</summary>
    public bool IsInProgress => Status is ProductVersionStatus.Draft or ProductVersionStatus.Validated or ProductVersionStatus.Submitted or ProductVersionStatus.Approved;

    public static ProductVersion Rehydrate(ProductVersionState state)
    {
        var version = new ProductVersion
        {
            Id = state.Id,
            ProductCode = state.ProductCode,
            Version = state.Version,
            Domain = state.Domain,
            Name = state.Name,
            Coverages = state.Coverages,
            Status = state.Status,
            EffectiveFrom = state.EffectiveFrom,
            EffectiveTo = state.EffectiveTo,
            SupersedesId = state.SupersedesId,
            FamilyCode = state.FamilyCode,
            ClassCode = state.ClassCode,
            CoverStartsOn = state.CoverStartsOn,
            EligibilityRulesetCode = state.EligibilityRulesetCode,
            ReinstatementArrears = state.ReinstatementArrears,
            ApprovedBy = state.ApprovedBy,
            ApprovedAt = state.ApprovedAt,
        };
        version.RestoreRowVersion(state.RowVersion);
        return version;
    }

    /// <summary>
    /// ProductDefinition's RegisterProduct: version 1 of a new product code, in Draft. The code is unique per tenant
    /// and never reused, and the product belongs to exactly one domain (checked by the caller against the taxonomy).
    /// </summary>
    public static ProductVersion RegisterProduct(Guid id, string productCode, ProductDomain domain, string familyCode, string? classCode, string name,
        IReadOnlyList<string> coverages, DateOnly effectiveFrom, DateTimeOffset now)
    {
        var version = New(id, productCode, 1, domain, name, coverages, effectiveFrom, null, familyCode, classCode);
        version.Raise(new ProductRegistered(id, version.ProductCode, domain, familyCode, now));
        return version;
    }

    /// <summary>Command CreateDraftVersion: the next version of a product, a copy of <paramref name="latest"/> to change (PRD-003).</summary>
    public static ProductVersion CreateDraftVersion(Guid id, ProductVersion latest, DateOnly effectiveFrom)
    {
        var version = New(id, latest.ProductCode, latest.Version + 1, latest.Domain, latest.Name, latest.Coverages, effectiveFrom,
            latest.Status is ProductVersionStatus.Published or ProductVersionStatus.Effective ? latest.Id : latest.SupersedesId, latest.FamilyCode, latest.ClassCode);
        version.CoverStartsOn = latest.CoverStartsOn;
        version.EligibilityRulesetCode = latest.EligibilityRulesetCode;
        version.ReinstatementArrears = latest.ReinstatementArrears;
        return version;
    }

    /// <summary>Command EditConfiguration. A Validated version returns to Draft; anything submitted or later is immutable (PRD-002).</summary>
    /// <param name="eligibilityRulesetCode">Unchanged when null; an empty string clears it.</param>
    public Result EditConfiguration(string name, IReadOnlyList<string> coverages, DateOnly effectiveFrom, CoverStart? coverStartsOn = null,
        string? eligibilityRulesetCode = null, ReinstatementArrears? reinstatementArrears = null)
    {
        if (!IsEditable)
        {
            return Error.Conflict(ProductErrorCodes.VersionImmutable,
                $"Version {Version} of {ProductCode} is {Status}; change a published product by creating a new version (PRD-002, PRD-003).");
        }

        if (Status == ProductVersionStatus.Validated)
        {
            var back = Fire(ProductVersionTrigger.EditConfiguration);
            if (back.IsFailure)
            {
                return back;
            }
        }

        Name = Guard.MaxLength(Guard.NotNullOrWhiteSpace(name), 100);
        Coverages = coverages.ToArray();
        EffectiveFrom = effectiveFrom;
        CoverStartsOn = coverStartsOn ?? CoverStartsOn;
        if (eligibilityRulesetCode is not null)
        {
            EligibilityRulesetCode = eligibilityRulesetCode.Length == 0 ? null : Guard.MaxLength(eligibilityRulesetCode, 30);
        }

        ReinstatementArrears = reinstatementArrears ?? ReinstatementArrears;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command ValidateVersion (PRD-004, PRD-009): no missing parameters, invalid values or incompatible benefits.</summary>
    public Result ValidateVersion(ProductValidationContext context, DateTimeOffset now)
    {
        var problems = Problems(context);
        if (problems.Count > 0)
        {
            return Error.Validation(ProductErrorCodes.ValidationFailed, $"Version {Version} of {ProductCode} is not valid: {string.Join(" ", problems)}");
        }

        var result = Fire(ProductVersionTrigger.ValidateVersion);
        if (result.IsSuccess)
        {
            Raise(new ProductVersionValidated(Id, ProductCode, Version, now));
        }

        return result;
    }

    /// <summary>Revalidation after static data changed (domain-model.json: StaticDataVersionActivated): a Validated version that no longer passes returns to Draft.</summary>
    public bool Revalidate(ProductValidationContext context)
    {
        if (Status != ProductVersionStatus.Validated || Problems(context).Count == 0)
        {
            return false;
        }

        return Fire(ProductVersionTrigger.EditConfiguration).IsSuccess;
    }

    /// <summary>Command SubmitForApproval. The approved test cases must have passed on this validated configuration (PRD-005).</summary>
    public Result SubmitForApproval(Guid makerId, bool simulationPassed, string reason, DateTimeOffset now)
    {
        if (Status == ProductVersionStatus.Validated && !simulationPassed)
        {
            return Error.Validation(ProductErrorCodes.SimulationRequired,
                $"Run the simulation on validated version {Version} of {ProductCode}; its test cases must pass before submission (PRD-005).");
        }

        var result = Fire(ProductVersionTrigger.SubmitForApproval);
        if (result.IsSuccess)
        {
            Raise(new ProductVersionSubmitted(Id, ProductCode, Version, makerId, string.IsNullOrWhiteSpace(reason) ? "Publication" : reason, now));
        }

        return result;
    }

    /// <summary>Transition "Approval rejected": back to Draft for the maker, for every ending other than approval (rejected, expired, withdrawn).</summary>
    public Result RejectApproval() => Fire(ProductVersionTrigger.ApprovalRejected);

    /// <summary>
    /// Whether an ending (not granted) is already applied, so a redelivery changes nothing: the version is back with the
    /// maker (Draft, or Validated again), not waiting for approval and not approved.
    /// </summary>
    public bool RejectionApplied => Status is ProductVersionStatus.Draft or ProductVersionStatus.Validated;

    /// <summary>Command ApproveVersion, from Workflow's ApprovalGranted (maker and checker already differ; SRS-002, PRD-006).</summary>
    public Result ApproveVersion(Guid checkerId, DateTimeOffset now)
    {
        var result = Fire(ProductVersionTrigger.ApproveVersion);
        if (result.IsSuccess)
        {
            ApprovedBy = checkerId.ToString();
            ApprovedAt = now;
            Raise(new ProductVersionApproved(Id, ProductCode, Version, checkerId, now));
        }

        return result;
    }

    /// <summary>Command PublishVersion: in force from its effective date (PRD-007).</summary>
    public Result PublishVersion(DateTimeOffset now)
    {
        var result = Fire(ProductVersionTrigger.PublishVersion);
        if (result.IsSuccess)
        {
            Raise(new ProductVersionPublished(Id, ProductCode, Version, Domain, EffectiveFrom, now));
        }

        return result;
    }

    /// <summary>
    /// Closes this version's period at the effective date of the version that replaces it, as static data does
    /// (V0002's no-overlap constraint). The only change a published version takes.
    /// </summary>
    public void CloseAt(DateOnly effectiveTo)
    {
        if (EffectiveTo is null || EffectiveTo > effectiveTo)
        {
            EffectiveTo = effectiveTo;
            MarkChanged();
        }
    }

    /// <summary>Transition "Effective date reached".</summary>
    public Result BecomeEffective(DateOnly today) =>
        today < EffectiveFrom
            ? Error.Conflict(ErrorCodes.InvalidStateTransition, $"Version {Version} of {ProductCode} is effective from {EffectiveFrom:yyyy-MM-dd}.")
            : Fire(ProductVersionTrigger.EffectiveDateReached);

    /// <summary>Command WithdrawVersion: no new business from <paramref name="effectiveTo"/>.</summary>
    public Result WithdrawVersion(DateOnly effectiveTo, DateOnly today, DateTimeOffset now)
    {
        if (effectiveTo < today || effectiveTo <= EffectiveFrom)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A version is withdrawn from today or a later date, after its effective date.");
        }

        var result = Fire(ProductVersionTrigger.WithdrawVersion);
        if (result.IsSuccess)
        {
            CloseAt(effectiveTo);
            Raise(new ProductVersionWithdrawn(Id, ProductCode, Version, effectiveTo, now));
        }

        return result;
    }

    public Result RetireVersion() => Fire(ProductVersionTrigger.RetireVersion);

    private static ProductVersion New(Guid id, string productCode, int number, ProductDomain domain, string name, IReadOnlyList<string> coverages,
        DateOnly effectiveFrom, Guid? supersedes, string? familyCode, string? classCode)
    {
        var version = new ProductVersion
        {
            Id = Guard.NotEmpty(id),
            ProductCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(productCode), MaxCodeLength),
            Version = number,
            Domain = domain,
            Name = Guard.MaxLength(Guard.NotNullOrWhiteSpace(name), 100),
            Coverages = coverages.ToArray(),
            Status = ProductVersionStatus.Draft,
            EffectiveFrom = effectiveFrom,
            SupersedesId = supersedes,
            FamilyCode = familyCode,
            ClassCode = classCode,
            IsNew = true,
        };
        version.MarkChanged();
        return version;
    }

    private List<string> Problems(ProductValidationContext context)
    {
        var problems = new List<string>();
        if (Coverages.Count == 0)
        {
            problems.Add("It has no coverages or benefits.");
        }

        foreach (var duplicate in Coverages.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{duplicate.Key} is listed more than once.");
        }

        foreach (var code in Coverages.Distinct(StringComparer.Ordinal))
        {
            if (!context.Coverages.TryGetValue(code, out var coverage))
            {
                problems.Add($"{code} is not in the coverage and benefit library in force.");
            }
            else if (coverage.Domain != Domain)
            {
                problems.Add($"{code} is a {coverage.Domain} coverage, not {Domain}.");
            }
        }

        var included = Coverages.ToHashSet(StringComparer.Ordinal);
        foreach (var rule in context.Dependencies.Where(d => included.Contains(d.Benefit)))
        {
            if (rule.Relation == "REQUIRES" && !included.Contains(rule.TargetBenefit))
            {
                problems.Add($"{rule.Benefit} requires {rule.TargetBenefit}.");
            }
            else if (rule.Relation == "EXCLUDES" && included.Contains(rule.TargetBenefit))
            {
                problems.Add($"{rule.Benefit} cannot be combined with {rule.TargetBenefit}.");
            }
        }

        if (EffectiveFrom < context.Today)
        {
            problems.Add($"The effective date {EffectiveFrom:yyyy-MM-dd} is in the past (PRD-007).");
        }

        if (context.SupersededFrom is { } previous && EffectiveFrom <= previous)
        {
            problems.Add($"The effective date must be after {previous:yyyy-MM-dd}, when the version it replaces took effect.");
        }

        return problems;
    }

    private Result Fire(ProductVersionTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

public static class ProductErrorCodes
{
    [ErrorCode(409, "error.product.code_in_use")]
    public const string CodeInUse = "PRODUCT_CODE_IN_USE";

    [ErrorCode(422, "error.product.family_not_found")]
    public const string FamilyNotFound = "PRODUCT_FAMILY_NOT_FOUND";

    [ErrorCode(409, "error.product.version_immutable")]
    public const string VersionImmutable = "PRODUCT_VERSION_IMMUTABLE";

    [ErrorCode(409, "error.product.version_in_progress")]
    public const string VersionInProgress = "PRODUCT_VERSION_IN_PROGRESS";

    [ErrorCode(422, "error.product.validation_failed")]
    public const string ValidationFailed = "PRODUCT_VALIDATION_FAILED";

    [ErrorCode(422, "error.product.simulation_required")]
    public const string SimulationRequired = "PRODUCT_SIMULATION_REQUIRED";
}
