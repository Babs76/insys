/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData
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

namespace Insys.StaticData.Domain.Changes;

/// <summary>
/// Change request states (sdm.change_request.status). The Draft state of domain-model.json is the draft row in the
/// owning module's table; a change request starts at submission.
/// </summary>
public enum ChangeStatus
{
    PendingApproval,
    Approved,
    Rejected,
    Active,
    Deprecated,
    Retired,
}

public enum ChangeTrigger
{
    ApproveChange,
    RejectChange,
    ActivateVersion,
    DeprecateCode,
    RetireCode,
}

/// <param name="RecordRef">The JSON reference to the record version, as stored in change_request.after.</param>
public sealed record StaticDataChangeApproved(Guid ChangeRequestId, string RecordRef, Guid CheckerId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The change needs a checker: Workflow is asked for an approval of this type (ADR-009).</summary>
public sealed record StaticDataApprovalNeeded(Guid ChangeRequestId, string ApprovalType, Guid MakerId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record StaticDataChangeRejected(Guid ChangeRequestId, string RecordRef, Guid CheckerId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The StaticDataChange aggregate (sdm.change_request). Maker and checker differ for Tier 1 always, and for Tier 2
/// unless the tenant has switched it off (SDM-003); the database also refuses a checker equal to the maker. Changes
/// that need no checker (Tier 3, or Tier 2 with the setting off) are approved on submission with no checker. A change
/// that needs one is decided in Workflow: the approval is requested here and its outcome applied by ApproveChange or
/// RejectChange (ADR-009).
/// </summary>
public sealed class StaticDataChange : AggregateRoot<Guid>
{
    public static readonly StateMachine<ChangeStatus, ChangeTrigger> Lifecycle = new StateMachine<ChangeStatus, ChangeTrigger>()
        .Allow(ChangeStatus.PendingApproval, ChangeTrigger.ApproveChange, ChangeStatus.Approved)
        .Allow(ChangeStatus.PendingApproval, ChangeTrigger.RejectChange, ChangeStatus.Rejected)
        .Allow(ChangeStatus.Approved, ChangeTrigger.ActivateVersion, ChangeStatus.Active)
        .Allow(ChangeStatus.Active, ChangeTrigger.DeprecateCode, ChangeStatus.Deprecated)
        .Allow(ChangeStatus.Deprecated, ChangeTrigger.RetireCode, ChangeStatus.Retired);

    private StaticDataChange()
    {
    }

    public string RequestNumber { get; private set; } = "";

    public string EntityCode { get; private set; } = "";

    /// <summary>"{code}@{version}", prefixed "G:" for a global row.</summary>
    public string RecordKey { get; private set; } = "";

    /// <summary>CREATE or UPDATE.</summary>
    public string Action { get; private set; } = "";

    public string? Before { get; private set; }

    /// <summary>A JSON reference to the record version (never its values; INT-007).</summary>
    public string After { get; private set; } = "{}";

    public Guid MakerId { get; private set; }

    public Guid? CheckerId { get; private set; }

    public ChangeStatus Status { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public string Reason { get; private set; } = "";

    public static string KeyFor(string code, int version, bool global) => $"{(global ? "G:" : "")}{code}@{version}";

    /// <summary>Command SubmitChange, from the owning module's StaticDataChangeSubmitted.</summary>
    public static StaticDataChange SubmitChange(Guid id, string entityCode, string recordKey, string action, string after, Guid makerId, DateOnly effectiveFrom,
        string reason, bool requiresChecker, string approvalType, DateTimeOffset now)
    {
        var change = new StaticDataChange
        {
            Id = Guard.NotEmpty(id),
            RequestNumber = "CR-" + id.ToString("N")[..16].ToUpperInvariant(),
            EntityCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(entityCode), 40),
            RecordKey = Guard.MaxLength(Guard.NotNullOrWhiteSpace(recordKey), 120),
            Action = action,
            After = after,
            MakerId = makerId,
            EffectiveFrom = effectiveFrom,
            Reason = string.IsNullOrWhiteSpace(reason) ? "(no reason given)" : reason,
            Status = ChangeStatus.PendingApproval,
        };

        // Tier 3 is descriptive data "changed by one authorised maintainer" (static data specification §2); Tier 2 too when
        // the tenant has switched maker-checker off.
        if (!requiresChecker)
        {
            change.Status = ChangeStatus.Approved;
            change.Raise(new StaticDataChangeApproved(id, after, makerId, now));
        }
        else
        {
            change.Raise(new StaticDataApprovalNeeded(id, approvalType, makerId, change.Reason, now));
        }

        change.MarkChanged();
        return change;
    }

    /// <summary>Command ApproveChange (SDM-003), from Workflow's ApprovalGranted.</summary>
    public Result ApproveChange(Guid checkerId, bool requiresDifferentChecker, DateTimeOffset now)
    {
        if (checkerId == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Changes are approved by an identified user.");
        }

        if (requiresDifferentChecker && checkerId == MakerId)
        {
            return MakerChecker.EnsureDifferentPeople(MakerId.ToString(), checkerId.ToString());
        }

        var result = Fire(ChangeTrigger.ApproveChange);
        if (result.IsSuccess)
        {
            CheckerId = checkerId == MakerId ? null : checkerId;
            Raise(new StaticDataChangeApproved(Id, After, checkerId, now));
        }

        return result;
    }

    /// <summary>
    /// Command RejectChange, from Workflow's ApprovalRejected: the owning module returns the version to draft. Every ending
    /// other than approval (rejected, expired, withdrawn) rejects it; an expired or withdrawn approval has no checker
    /// (Guid.Empty), so none is recorded.
    /// </summary>
    public Result RejectChange(Guid checkerId, string reason, DateTimeOffset now)
    {
        if (checkerId != Guid.Empty && checkerId == MakerId)
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "A change is rejected by a checker other than its maker.");
        }

        var result = Fire(ChangeTrigger.RejectChange);
        if (result.IsSuccess)
        {
            CheckerId = checkerId == Guid.Empty ? null : checkerId;
            Raise(new StaticDataChangeRejected(Id, After, checkerId, reason, now));
        }

        return result;
    }

    /// <summary>Command ActivateVersion, from the owning module's StaticDataVersionActivated.</summary>
    public Result ActivateVersion() => Fire(ChangeTrigger.ActivateVersion);

    /// <summary>Command DeprecateCode, from the owning module's StaticDataCodeDeprecated.</summary>
    public Result DeprecateCode() => Fire(ChangeTrigger.DeprecateCode);

    /// <summary>Command RetireCode. No event reports retirement yet (OQ-028), so nothing calls it.</summary>
    public Result RetireCode() => Fire(ChangeTrigger.RetireCode);

    private Result Fire(ChangeTrigger trigger)
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

public enum ImportStatus
{
    Submitted,
    Validating,
    Committed,
    Rejected,
}

/// <summary>Entity ImportError (sdm.import_error): one rejected line.</summary>
public sealed class ImportError : Entity<Guid>, ITenantScoped
{
    private ImportError()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid BatchId { get; private set; }

    public int LineNo { get; private set; }

    public string Field { get; private set; } = "";

    public string Message { get; private set; } = "";

    internal static ImportError Create(Guid batchId, int line, string field, string message) =>
        new() { Id = Guid.CreateVersion7(), BatchId = batchId, LineNo = line, Field = field.Length <= 60 ? field : field[..60], Message = message };
}

/// <summary>
/// The ImportBatch aggregate (sdm.import_batch): one bulk load, committed completely or not at all (SDM-012). The owning
/// module validates and commits in its own schema; this records the outcome it reports.
/// </summary>
public sealed class ImportBatch : AggregateRoot<Guid>
{
    private readonly List<ImportError> _errors = [];

    private ImportBatch()
    {
    }

    public string BatchNumber { get; private set; } = "";

    public string EntityCode { get; private set; } = "";

    public string FileRef { get; private set; } = "";

    public int RowCount { get; private set; }

    public int RejectedCount { get; private set; }

    public ImportStatus Status { get; private set; }

    public Guid SubmittedBy { get; private set; }

    public IReadOnlyList<ImportError> Errors => _errors;

    /// <summary>Commands SubmitImport, ValidateImport and CommitImport, recorded together once the owning module has committed.</summary>
    public static ImportBatch CommitImport(Guid id, string entityCode, string fileRef, int rowCount, Guid submittedBy) =>
        Create(id, entityCode, fileRef, rowCount, submittedBy, ImportStatus.Committed);

    /// <summary>Validating → Rejected on errors found: nothing was saved, and every rejected line is kept.</summary>
    public static ImportBatch RejectImport(Guid id, string entityCode, string fileRef, int rowCount, Guid submittedBy,
        IEnumerable<(int Line, string Field, string Message)> errors)
    {
        var batch = Create(id, entityCode, fileRef, rowCount, submittedBy, ImportStatus.Rejected);
        foreach (var (line, field, message) in errors)
        {
            batch._errors.Add(ImportError.Create(id, line, field, message));
        }

        batch.RejectedCount = batch._errors.Select(e => e.LineNo).Distinct().Count();
        return batch;
    }

    private static ImportBatch Create(Guid id, string entityCode, string fileRef, int rowCount, Guid submittedBy, ImportStatus status)
    {
        var batch = new ImportBatch
        {
            Id = id,
            BatchNumber = "IB-" + id.ToString("N")[..16].ToUpperInvariant(),
            EntityCode = entityCode,
            FileRef = fileRef.Length <= 200 ? fileRef : fileRef[..200],
            RowCount = rowCount,
            SubmittedBy = submittedBy,
            Status = status,
        };
        batch.MarkChanged();
        return batch;
    }
}

/// <summary>Whether a change at this tier needs a checker other than the maker (SDM-003).</summary>
public static class MakerCheckerPolicy
{
    public static bool RequiresChecker(int tier, bool tier2MakerChecker) => tier switch
    {
        1 => true,
        2 => tier2MakerChecker,
        _ => false,
    };
}

/// <summary>Switching Tier 2 maker-checker off waits for a second person: Workflow is asked for a Tier 1 approval (OQ-046).</summary>
public sealed record GovernanceChangeRequested(Guid SettingsId, Guid RequestedBy, string ApprovalType, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The tenant's static data governance settings (sdm.tenant_governance_setting). Proposed aggregate for SDM-003's
/// "unless the tenant switches it off": Tier 2 maker-checker is on by default, and Tier 1 cannot be switched off.
/// Switching Tier 2 off weakens control, so it is itself a Tier 1 change: it is held as pending until a second person
/// approves it in Workflow (OQ-046, V0013). Switching it on takes effect at once.
/// </summary>
public sealed class TenantGovernanceSettings : AggregateRoot<Guid>
{
    private TenantGovernanceSettings()
    {
    }

    public bool Tier2MakerChecker { get; private set; } = true;

    /// <summary>False while a switch-off waits for approval; null when nothing is pending.</summary>
    public bool? PendingTier2MakerChecker { get; private set; }

    public Guid? PendingRequestedBy { get; private set; }

    public DateTimeOffset? PendingRequestedAt { get; private set; }

    public static TenantGovernanceSettings Defaults(Guid id) => new() { Id = id };

    /// <summary>Switches Tier 2 maker-checker on at once, cancelling any pending switch-off. Returns false when nothing changed.</summary>
    public bool SwitchTier2On()
    {
        if (Tier2MakerChecker && PendingTier2MakerChecker is null)
        {
            return false;
        }

        Tier2MakerChecker = true;
        ClearPending();
        MarkChanged();
        return true;
    }

    /// <summary>Asks for Tier 2 maker-checker to be switched off. Nothing changes until the approval is granted.</summary>
    public Result<bool> RequestTier2Off(Guid requestedBy, string approvalType, DateTimeOffset now)
    {
        if (!Tier2MakerChecker)
        {
            return false;
        }

        if (PendingTier2MakerChecker is not null)
        {
            return Error.Conflict(StaticDataGovernanceCodes.ChangePending, "Switching Tier 2 maker-checker off is already waiting for approval.");
        }

        PendingTier2MakerChecker = false;
        PendingRequestedBy = requestedBy;
        PendingRequestedAt = now;
        Raise(new GovernanceChangeRequested(Id, requestedBy, approvalType, now));
        MarkChanged();
        return true;
    }

    /// <summary>
    /// Workflow's decision on the pending switch-off. The checker is never the requester (SRS-002). Every ending other
    /// than approval (rejected, expired, withdrawn) drops the pending change and the setting stays as it was.
    /// </summary>
    public Result DecidePending(bool approved, Guid checkerId)
    {
        if (PendingTier2MakerChecker is not { } pending || PendingRequestedBy is not { } requester)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No governance change is waiting for approval.");
        }

        if (checkerId != Guid.Empty && checkerId == requester)
        {
            return MakerChecker.EnsureDifferentPeople(requester.ToString(), checkerId.ToString());
        }

        if (approved && checkerId == Guid.Empty)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A governance change is approved by a checker.");
        }

        if (approved)
        {
            Tier2MakerChecker = pending;
        }

        ClearPending();
        MarkChanged();
        return Result.Success();
    }

    private void ClearPending()
    {
        PendingTier2MakerChecker = null;
        PendingRequestedBy = null;
        PendingRequestedAt = null;
    }
}

public static class StaticDataGovernanceCodes
{
    [ErrorCode(409, "error.static_data.governance_change_pending")]
    public const string ChangePending = "GOVERNANCE_CHANGE_PENDING";
}
