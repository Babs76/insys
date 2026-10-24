/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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

namespace Insys.Tenancy.Domain.Tenants;

/// <summary>States from domain-model.json (tenant › Tenant), stored upper-case in tenant.tenant.status.</summary>
public enum TenantStatus
{
    Onboarding,
    Active,
    Suspended,
    Terminated,
}

public enum TenantTrigger
{
    ActivateTenant,
    SuspendTenant,
    ReactivateTenant,
    TerminateTenant,
}

/// <summary>Isolation profile A, B or C (static data spec, tenant.cfg_isolation_profile_assignment).</summary>
public enum IsolationProfile
{
    A,
    B,
    C,
}

/// <summary>
/// The onboarding steps, in the order of the static data specification's tenant load sequence (§8), which it places
/// inside the TEN-008 onboarding workflow. Proposed; see OQ-029.
/// </summary>
public static class OnboardingSteps
{
    public static readonly IReadOnlyList<string> All =
    [
        "PLATFORM_REFERENCE_DATA",
        "TENANT_REGISTRATION",
        "ORGANISATION_AND_ACCESS",
        "FINANCIAL_FOUNDATIONS",
        "SHARED_OPERATIONAL_SETUP",
        "PAYMENTS_AND_DISTRIBUTION",
        "DOMAIN_REFERENCE_DATA",
        "PRODUCT_FACTORY",
        "REINSURANCE",
        "INTEGRATION_AND_REPORTING",
        "END_TO_END_TESTS",
    ];
}

/// <summary>Entity OnboardingCase (tenant.onboarding_case): the tenant's progress through the onboarding steps.</summary>
public sealed class OnboardingCase : Entity<Guid>, ITenantScoped
{
    private OnboardingCase()
    {
    }

    public Guid TenantId { get; private set; }

    public string CurrentStep { get; private set; } = OnboardingSteps.All[0];

    /// <summary>IN_PROGRESS or COMPLETED.</summary>
    public string Status { get; private set; } = "IN_PROGRESS";

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public long RowVersion { get; private set; }

    public bool IsCompleted => Status == "COMPLETED";

    /// <summary>The waivers and evidence recorded so far (tenant.onboarding_case.attestations).</summary>
    public IReadOnlyList<OnboardingAttestation> Attestations { get; private set; } = [];

    internal Result Attest(string check, string note, Guid by, DateTimeOffset now)
    {
        if (!OnboardingAttestation.Allowed.TryGetValue(check, out var allowed) || allowed.Step != CurrentStep || IsCompleted)
        {
            return Error.Validation(TenancyErrorCodes.AttestationNotAllowed,
                $"{check} cannot be recorded here: waivers are for TREATIES and WEBHOOKS_OR_NONE, evidence for SMOKE_RUN, each in its own step.");
        }

        Attestations = [.. Attestations.Where(a => a.Check != check), new OnboardingAttestation(allowed.Step, check, allowed.Kind, Guard.MaxLength(note, 500), by, now)];
        return Result.Success();
    }

    internal static OnboardingCase Start(Guid tenantId, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), TenantId = tenantId, StartedAt = now };

    internal Result CompleteStep(string step, DateTimeOffset now)
    {
        if (IsCompleted)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Onboarding is already complete.");
        }

        if (step != CurrentStep)
        {
            return Error.Conflict(TenancyErrorCodes.OnboardingStepOutOfOrder, $"The current onboarding step is {CurrentStep}; steps are completed in order.");
        }

        var index = OnboardingSteps.All.ToList().IndexOf(step);
        if (index == OnboardingSteps.All.Count - 1)
        {
            Status = "COMPLETED";
            CompletedAt = now;
        }
        else
        {
            CurrentStep = OnboardingSteps.All[index + 1];
        }

        return Result.Success();
    }
}

/// <summary>
/// A waiver ("no reinsurance", "no webhooks", with a reason) or evidence (the end-to-end run's reference) the operator
/// records for a check instead of configuration (onboarding readiness §2).
/// </summary>
public sealed record OnboardingAttestation(string Step, string Check, string Kind, string Note, Guid By, DateTimeOffset At)
{
    public static readonly IReadOnlyDictionary<string, (string Step, string Kind)> Allowed = new Dictionary<string, (string, string)>
    {
        ["TREATIES"] = ("REINSURANCE", "WAIVER"),
        ["WEBHOOKS_OR_NONE"] = ("INTEGRATION_AND_REPORTING", "WAIVER"),
        ["SMOKE_RUN"] = ("END_TO_END_TESTS", "EVIDENCE"),
    };
}

/// <summary>Entity StatusChange (tenant.tenant_status_history): every status change with its reason.</summary>
public sealed class StatusChange : Entity<Guid>, ITenantScoped
{
    private StatusChange()
    {
    }

    public Guid TenantId { get; private set; }

    public TenantStatus FromStatus { get; private set; }

    public TenantStatus ToStatus { get; private set; }

    public string Reason { get; private set; } = "";

    public Guid ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    internal static StatusChange Record(Guid tenantId, TenantStatus from, TenantStatus to, string reason, Guid changedBy, DateTimeOffset at) =>
        new() { Id = Guid.CreateVersion7(), TenantId = tenantId, FromStatus = from, ToStatus = to, Reason = reason, ChangedBy = changedBy, ChangedAt = at };
}

public sealed record TenantSuspended(Guid TenantId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The Tenant aggregate (tenant.tenant with its onboarding case and status history). The tenant identifier is unique
/// and immutable (TEN-001); onboarding, suspension, reactivation and termination follow defined workflows (TEN-008).
/// </summary>
public sealed class Tenant : AggregateRoot<Guid>
{
    public static readonly StateMachine<TenantStatus, TenantTrigger> Lifecycle = new StateMachine<TenantStatus, TenantTrigger>()
        .Allow(TenantStatus.Onboarding, TenantTrigger.ActivateTenant, TenantStatus.Active)
        .Allow(TenantStatus.Active, TenantTrigger.SuspendTenant, TenantStatus.Suspended)
        .Allow(TenantStatus.Suspended, TenantTrigger.ReactivateTenant, TenantStatus.Active)
        .Allow(TenantStatus.Active, TenantTrigger.TerminateTenant, TenantStatus.Terminated)
        .Allow(TenantStatus.Suspended, TenantTrigger.TerminateTenant, TenantStatus.Terminated);

    private readonly List<OnboardingCase> _onboardingCases = [];
    private readonly List<StatusChange> _statusChanges = [];

    private Tenant()
    {
    }

    /// <summary>The short business code (max 10 characters), immutable like the identifier.</summary>
    public string TenantCode { get; private set; } = "";

    public string LegalName { get; private set; } = "";

    public IsolationProfile IsolationProfile { get; private set; }

    /// <summary>
    /// The tenant's IANA time zone, required at registration (ADR-025): its business dates follow it until its locale
    /// defaults set one. Null only for tenants registered before it was required.
    /// </summary>
    public string? TimeZone { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public OnboardingCase? Onboarding => _onboardingCases.SingleOrDefault();

    public IReadOnlyList<StatusChange> StatusChanges => _statusChanges;

    /// <summary>Command RegisterTenant: the tenant starts onboarding (TEN-008).</summary>
    public static Result<Tenant> RegisterTenant(Guid tenantId, string tenantCode, string legalName, IsolationProfile profile, string timeZone, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A tenant needs an identifier (TEN-001).");
        }

        var tenant = new Tenant
        {
            Id = tenantId,
            TenantCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(tenantCode), 10),
            LegalName = Guard.MaxLength(Guard.NotNullOrWhiteSpace(legalName), 150),
            IsolationProfile = profile,
            TimeZone = Guard.MaxLength(Guard.NotNullOrWhiteSpace(timeZone), 50),
            Status = TenantStatus.Onboarding,
            CreatedAt = now,
        };
        tenant.TenantId = tenantId;
        tenant._onboardingCases.Add(OnboardingCase.Start(tenantId, now));
        tenant.MarkChanged();
        return tenant;
    }

    /// <summary>Records a waiver or evidence for a check of the current onboarding step (onboarding readiness §2).</summary>
    public Result AttestOnboarding(string check, string note, Guid by, DateTimeOffset now)
    {
        if (Status != TenantStatus.Onboarding || Onboarding is null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a tenant in onboarding has onboarding steps.");
        }

        var result = Onboarding.Attest(check, note, by, now);
        if (result.IsSuccess)
        {
            MarkChanged();
        }

        return result;
    }

    /// <summary>Completes the current onboarding step (TEN-008). Proposed command; see OQ-029.</summary>
    public Result CompleteOnboardingStep(string step, DateTimeOffset now)
    {
        if (Status != TenantStatus.Onboarding || Onboarding is null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a tenant in onboarding has onboarding steps.");
        }

        var result = Onboarding.CompleteStep(step, now);
        if (result.IsSuccess)
        {
            MarkChanged();
        }

        return result;
    }

    /// <summary>Command ActivateTenant: allowed only when onboarding is complete (TEN-008).</summary>
    public Result ActivateTenant(Guid operatorId, string reason, DateTimeOffset now)
    {
        if (Status == TenantStatus.Onboarding && Onboarding?.IsCompleted != true)
        {
            return Error.Conflict(TenancyErrorCodes.OnboardingIncomplete, $"Onboarding is not complete; the current step is {Onboarding?.CurrentStep}.");
        }

        return Change(TenantTrigger.ActivateTenant, operatorId, reason, now);
    }

    public Result SuspendTenant(Guid operatorId, string reason, DateTimeOffset now)
    {
        var result = Change(TenantTrigger.SuspendTenant, operatorId, reason, now);
        if (result.IsSuccess)
        {
            Raise(new TenantSuspended(Id, now));
        }

        return result;
    }

    public Result ReactivateTenant(Guid operatorId, string reason, DateTimeOffset now) => Change(TenantTrigger.ReactivateTenant, operatorId, reason, now);

    public Result TerminateTenant(Guid operatorId, string reason, DateTimeOffset now) => Change(TenantTrigger.TerminateTenant, operatorId, reason, now);

    /// <summary>
    /// Command ChangeIsolationProfile. The invariant "requires an approved data migration" names no source of approval,
    /// so the command needs the reference of the approved migration and records it in the status history (OQ-030).
    /// </summary>
    public Result ChangeIsolationProfile(IsolationProfile profile, string approvedMigrationRef, Guid operatorId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(approvedMigrationRef))
        {
            return Error.Conflict(TenancyErrorCodes.DataMigrationRequired, "Changing the isolation profile requires an approved data migration.");
        }

        if (Status == TenantStatus.Terminated)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A terminated tenant cannot change isolation profile.");
        }

        if (profile == IsolationProfile)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The tenant already has this isolation profile.");
        }

        _statusChanges.Add(StatusChange.Record(Id, Status, Status, $"Isolation profile {IsolationProfile} -> {profile}; migration {approvedMigrationRef}", operatorId, now));
        IsolationProfile = profile;
        MarkChanged();
        return Result.Success();
    }

    private Result Change(TenantTrigger trigger, Guid operatorId, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A reason is required for every tenant status change (TEN-008).");
        }

        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        _statusChanges.Add(StatusChange.Record(Id, Status, next.Value, reason, operatorId, now));
        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

public static class TenancyErrorCodes
{
    [ErrorCode(409, "error.tenancy.onboarding_incomplete")]
    public const string OnboardingIncomplete = "ONBOARDING_INCOMPLETE";

    [ErrorCode(409, "error.tenancy.onboarding_step_order")]
    public const string OnboardingStepOutOfOrder = "ONBOARDING_STEP_OUT_OF_ORDER";

    [ErrorCode(409, "error.tenancy.data_migration_required")]
    public const string DataMigrationRequired = "DATA_MIGRATION_REQUIRED";

    [ErrorCode(409, "error.tenancy.code_taken")]
    public const string TenantCodeTaken = "TENANT_CODE_TAKEN";

    [ErrorCode(422, "error.tenancy.onboarding_step_not_ready")]
    public const string OnboardingStepNotReady = "ONBOARDING_STEP_NOT_READY";

    [ErrorCode(422, "error.tenancy.attestation_not_allowed")]
    public const string AttestationNotAllowed = "ATTESTATION_NOT_ALLOWED";
}
