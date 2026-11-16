/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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

namespace Insys.Assist.Domain;

/// <summary>Where a draft value came from (AIA-084).</summary>
public static class ItemFlags
{
    public const string Pack = "PACK";
    public const string Tailored = "TAILORED";
    public const string Mapped = "MAPPED";
    public const string Missing = "MISSING";
}

/// <summary>Why a value is Missing.</summary>
public static class MissingReasons
{
    /// <summary>A regulated value from an overlay not approved for this tenant (ASSIST_PACK_NOT_APPROVED, AIA-082): never filled.</summary>
    public const string NotApproved = "NOT_APPROVED";

    /// <summary>The pack leaves a regulated value empty: only the tenant, or a reviewed overlay, can give it.</summary>
    public const string NoValue = "NO_VALUE";
}

public static class ItemDecisions
{
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
}

public static class SetupStatuses
{
    public const string Draft = "DRAFT";

    /// <summary>The completeness check passed and the values are locked; the batches are being loaded into the SETUP change set (AIA-086). Resumable.</summary>
    public const string Submitting = "SUBMITTING";

    /// <summary>The SETUP change set waits for its approval, or is being applied.</summary>
    public const string Submitted = "SUBMITTED";

    /// <summary>The SETUP change set is in force.</summary>
    public const string Applied = "APPLIED";
}

/// <summary>One table's batch, loaded and submitted to the run's SETUP change set (AIA-086), in the order it is applied.</summary>
public sealed record SetupBatchLoaded(int Seq, string Table, string EntityCode, Guid BatchId, Guid ChangeRequestId, int Lines, string Hash);

/// <summary>Every batch is loaded: Static Data records the set and puts it to one approval.</summary>
public sealed record SetupChangeSetReady(Guid SetId, Guid RunId, Guid MakerId, IReadOnlyList<SetupBatchLoaded> Batches, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The submission is withdrawn before its set was requested: the batches loaded so far are discarded by their modules.</summary>
public sealed record SetupSubmissionWithdrawn(Guid SetId, Guid RunId, Guid MakerId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A run of AI-3's wizard on a tenant still onboarding (AI Assist SRS §5A): its questionnaire answers, versioned (AIA-081), the pack versions the draft was
/// assembled from (AIA-089) and its status. The draft's values are its <see cref="SetupItem"/>s. Nothing here changes the tenant's configuration: the SETUP
/// change set does, in 7.7, after a second person's approval.
/// </summary>
public sealed class SetupRun : AggregateRoot<Guid>
{
    private SetupRun()
    {
    }

    public string Status { get; private set; } = SetupStatuses.Draft;

    /// <summary>Who ran the wizard: never the one who approves its change set (AIA-086).</summary>
    public string StartedBy { get; private set; } = "";

    public int AnswersVersion { get; private set; }

    /// <summary>The pack versions the draft was assembled from, in layer order (AIA-089).</summary>
    public Guid[] PackIds { get; private set; } = [];

    public DateTimeOffset AssembledAt { get; private set; }

    /// <summary>How many times the run has been submitted; each attempt's batches have their own ids (AIA-086).</summary>
    public int Attempt { get; private set; }

    /// <summary>The SETUP change set the run is submitted as; cleared if the set is not approved.</summary>
    public Guid? SetId { get; private set; }

    /// <summary>Who submitted it: whoever ran the wizard, so never its approver (AIA-086).</summary>
    public string? SubmittedBy { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public static SetupRun Start(string startedBy, IReadOnlyList<Guid> packIds, DateTimeOffset at) =>
        new() { Id = Guid.CreateVersion7(), StartedBy = startedBy, AnswersVersion = 1, PackIds = [.. packIds], AssembledAt = at };

    /// <summary>
    /// AIA-086: the run is submitted as a SETUP change set, by whoever ran the wizard only, so its maker is never its approver. The values are locked from now
    /// on; a set not approved returns the run to draft.
    /// </summary>
    public Result BeginSubmission(string by, DateTimeOffset at)
    {
        if (by != StartedBy)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "A setup is submitted by whoever ran the wizard, so a second person approves it.");
        }

        if (Status != SetupStatuses.Draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The setup is {Status}; only a draft is submitted.");
        }

        (Status, Attempt, SetId, SubmittedBy, SubmittedAt) = (SetupStatuses.Submitting, Attempt + 1, Guid.CreateVersion7(), by, at);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Every batch is loaded: the set is requested from Static Data, which puts it to one approval.</summary>
    public Result Submitted(IReadOnlyList<SetupBatchLoaded> batches, DateTimeOffset at)
    {
        if (Status != SetupStatuses.Submitting || SetId is not { } setId || !Guid.TryParse(SubmittedBy, out var maker))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The setup is {Status}.");
        }

        Status = SetupStatuses.Submitted;
        Raise(new SetupChangeSetReady(setId, Id, maker, batches, at));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// The submitter withdraws a submission that stopped part-way (a batch refused on every attempt), before its set was requested. The batches loaded so far
    /// are discarded and the run is a draft again, to be corrected and submitted as a new attempt.
    /// </summary>
    public Result WithdrawSubmission(string by, DateTimeOffset at)
    {
        if (Status != SetupStatuses.Submitting || SetId is not { } setId || !Guid.TryParse(SubmittedBy, out var maker))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The setup is {Status}; only a submission that stopped part-way is withdrawn. A submitted set is ended in Workflow.");
        }

        if (by != SubmittedBy)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only whoever submitted the setup withdraws its submission.");
        }

        Raise(new SetupSubmissionWithdrawn(setId, Id, maker, at));
        (Status, SetId, SubmittedBy, SubmittedAt) = (SetupStatuses.Draft, null, null, null);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// A value of the draft was decided. The run's version moves with it, so a decision can't slip in beside the submission's check and lock unseen: one of
    /// the two is refused as a conflict.
    /// </summary>
    public void ValueDecided() => MarkChanged();

    /// <summary>The set the run was submitted as is in force. A redelivery changes nothing.</summary>
    public bool Applied(Guid setId)
    {
        if (SetId != setId || Status == SetupStatuses.Applied)
        {
            return false;
        }

        Status = SetupStatuses.Applied;
        MarkChanged();
        return true;
    }

    /// <summary>The set wasn't approved: its drafts are discarded, and the run is a draft again, to be corrected and submitted as a new attempt.</summary>
    public bool NotApproved(Guid setId)
    {
        if (SetId != setId || Status is not (SetupStatuses.Submitting or SetupStatuses.Submitted))
        {
            return false;
        }

        (Status, SetId, SubmittedBy, SubmittedAt) = (SetupStatuses.Draft, null, null, null);
        MarkChanged();
        return true;
    }

    /// <summary>New answers re-draft the run (AIA-081): a draft only.</summary>
    public Result Reanswer(IReadOnlyList<Guid> packIds, DateTimeOffset at)
    {
        if (Status != SetupStatuses.Draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a draft setup takes new answers.");
        }

        (AnswersVersion, PackIds, AssembledAt) = (AnswersVersion + 1, [.. packIds], at);
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>One version of a run's questionnaire answers, kept as given (AIA-081).</summary>
public sealed class SetupAnswer : AggregateRoot<Guid>
{
    private SetupAnswer()
    {
    }

    public Guid RunId { get; private set; }

    public int Version { get; private set; }

    public string Answers { get; private set; } = "{}";

    public static SetupAnswer For(Guid runId, int version, string answers) => new() { Id = Guid.CreateVersion7(), RunId = runId, Version = version, Answers = answers };
}

/// <summary>One value of a run's draft: its table and code, where it came from (AIA-084), and the reviewer's decision on it.</summary>
public sealed class SetupItem : AggregateRoot<Guid>
{
    private SetupItem()
    {
    }

    public Guid RunId { get; private set; }

    public string TableName { get; private set; } = "";

    public string RowCode { get; private set; } = "";

    public string Class { get; private set; } = "";

    public bool Regulated { get; private set; }

    public string Flag { get; private set; } = ItemFlags.Pack;

    /// <summary>The value as JSON; null when Missing.</summary>
    public string? Values { get; private set; }

    public Guid? SourcePackId { get; private set; }

    public int? SourceLineNo { get; private set; }

    public string? MissingReason { get; private set; }

    public string? Decision { get; private set; }

    public string? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DecisionNote { get; private set; }

    /// <summary>The answers version that last drafted it.</summary>
    public int AnswersVersion { get; private set; }

    /// <summary>Whether the current answers still bring the row. Once they don't, it is kept, rejected and never decided again (AIA-081).</summary>
    public bool InDraft { get; private set; } = true;

    public static SetupItem Draft(Guid runId, int answersVersion, string table, string code, string @class, bool regulated, string flag, string? values, Guid? sourcePackId,
        int? sourceLineNo, string? missingReason) => new()
        {
            Id = Guid.CreateVersion7(), RunId = runId, AnswersVersion = answersVersion, TableName = table, RowCode = code, Class = @class, Regulated = regulated, Flag = flag,
            Values = values, SourcePackId = sourcePackId, SourceLineNo = sourceLineNo, MissingReason = missingReason,
        };

    /// <summary>Whether a new draft of the same row would be this one (AIA-081: changing an answer re-drafts only what it affects).</summary>
    public bool Same(string flag, string? values, Guid? sourcePackId, int? sourceLineNo) =>
        Flag == flag && Values == values && SourcePackId == sourcePackId && SourceLineNo == sourceLineNo;

    /// <summary>A re-draft of the row: its decision goes, as the value it decided on did.</summary>
    public void Redraft(int answersVersion, string @class, bool regulated, string flag, string? values, Guid? sourcePackId, int? sourceLineNo, string? missingReason)
    {
        (AnswersVersion, Class, Regulated, Flag, Values, SourcePackId, SourceLineNo, MissingReason) = (answersVersion, @class, regulated, flag, values, sourcePackId, sourceLineNo, missingReason);
        (Decision, DecidedBy, DecidedAt, DecisionNote, InDraft) = (null, null, null, null, true);
        MarkChanged();
    }

    /// <summary>The current answers no longer bring the row: it is rejected and leaves the draft, its value kept for the record.</summary>
    public void LeaveDraft(string by, DateTimeOffset at)
    {
        (Decision, DecisionNote, DecidedBy, DecidedAt, InDraft) = (ItemDecisions.Rejected, "No longer drafted from the answers.", by, at, false);
        MarkChanged();
    }

    /// <summary>The reviewer's decision (§5A.5). A Missing value can't be accepted: there is nothing to accept. A value out of the draft isn't decided again.</summary>
    public Result Decide(string decision, string? note, string by, DateTimeOffset at)
    {
        if (!InDraft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The current answers no longer bring this value, so it isn't decided again.");
        }

        if (decision is not (ItemDecisions.Accepted or ItemDecisions.Rejected))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "decision is ACCEPTED or REJECTED.");
        }

        if (decision == ItemDecisions.Accepted && Flag == ItemFlags.Missing)
        {
            return MissingReason == MissingReasons.NotApproved
                ? Error.Validation(PackErrorCodes.NotApproved, "A regulated value from an overlay not approved for this tenant can't be accepted.")
                : Error.Validation(ErrorCodes.ValidationFailed, "A value the pack leaves empty can't be accepted: it is Missing.");
        }

        (Decision, DecisionNote, DecidedBy, DecidedAt) = (decision, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), by, at);
        MarkChanged();
        return Result.Success();
    }
}
