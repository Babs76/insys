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

/// <summary>The set needs its one approval (Tier 1), bound to its hash (AIA-086).</summary>
/// <summary>A set's statuses (sdm.setup_change_set.status), as Static Data's contracts publish them.</summary>
public static class SetupSetStatuses
{
    public const string Submitted = "SUBMITTED";
    public const string Applying = "APPLYING";
    public const string Applied = "APPLIED";
    public const string NotApproved = "NOT_APPROVED";
}

/// <summary>One batch as submitted to a set.</summary>
public sealed record SetupBatchSubmitted(int Seq, string Table, string EntityCode, Guid BatchId, Guid ChangeRequestId, int Lines, string Hash);

public sealed record SetupApprovalNeeded(Guid SetId, string ApprovalType, Guid MakerId, string Hash, int Batches, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record SetupChangeSetCompleted(Guid SetId, Guid SourceRef, Guid CheckerId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record SetupChangeSetEnded(Guid SetId, Guid SourceRef, string Ending, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>One table's batch in a set, in the order it is applied.</summary>
public sealed class SetupChangeSetBatchEntry : Entity<Guid>, ITenantScoped
{
    private SetupChangeSetBatchEntry()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid SetId { get; private set; }

    public int Seq { get; private set; }

    public string TableName { get; private set; } = "";

    public string EntityCode { get; private set; } = "";

    public Guid BatchId { get; private set; }

    public Guid ChangeRequestId { get; private set; }

    public int LineCount { get; private set; }

    public string Hash { get; private set; } = "";

    public DateTimeOffset? AppliedAt { get; private set; }

    internal static SetupChangeSetBatchEntry Create(Guid setId, int seq, string table, string entityCode, Guid batchId, Guid changeRequestId, int lines, string hash) => new()
    {
        Id = Guid.CreateVersion7(), SetId = setId, Seq = seq, TableName = Guard.MaxLength(Guard.NotNullOrWhiteSpace(table), 80),
        EntityCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(entityCode), 40), BatchId = Guard.NotEmpty(batchId), ChangeRequestId = Guard.NotEmpty(changeRequestId),
        LineCount = lines, Hash = Guard.MaxLength(Guard.NotNullOrWhiteSpace(hash), 64),
    };

    internal void Applied(DateTimeOffset at) => AppliedAt ??= at;
}

/// <summary>
/// The SetupChangeSet aggregate (sdm.setup_change_set, AIA-086, D-11): a tenant's setup as one change, one import batch per table (BR-016). One approval of
/// the strictest tier decides every batch, bound to a hash of them all, and the approver is never the maker (who ran the wizard). The approved set is applied
/// batch by batch in its order; each batch is one transaction in its own module, so the set is resumable, and it is APPLYING until its last batch is in force.
/// A set not approved discards its drafts.
/// </summary>
public sealed class SetupChangeSet : AggregateRoot<Guid>
{
    private readonly List<SetupChangeSetBatchEntry> _batches = [];

    private SetupChangeSet()
    {
    }

    public string SetNumber { get; private set; } = "";

    /// <summary>The setup it came from (an Assist setup run).</summary>
    public Guid SourceRef { get; private set; }

    public Guid MakerId { get; private set; }

    /// <summary>SHA-256 over every batch (its order, table, batch, line count and lines' hash): the approval's subject.</summary>
    public string Hash { get; private set; } = "";

    public string Status { get; private set; } = SetupSetStatuses.Submitted;

    public Guid? CheckerId { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    /// <summary>How a set not approved ended: REJECTED, EXPIRED or WITHDRAWN.</summary>
    public string? Ending { get; private set; }

    public IReadOnlyList<SetupChangeSetBatchEntry> Batches => _batches;

    public bool IsOpen => Status is SetupSetStatuses.Submitted or SetupSetStatuses.Applying;

    /// <summary>The hash a set's approval is bound to.</summary>
    public static string HashOf(IEnumerable<(int Seq, string Table, Guid BatchId, int Lines, string Hash)> batches)
    {
        var text = string.Concat(batches.OrderBy(b => b.Seq).Select(b =>
            $"{b.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{b.Table}|{b.BatchId:D}|{b.Lines.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{b.Hash}\n"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    public static Result<SetupChangeSet> Submit(Guid id, Guid sourceRef, Guid makerId, IReadOnlyList<SetupBatchSubmitted> batches, string approvalType, DateTimeOffset now)
    {
        if (makerId == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "A setup is submitted by an identified user.");
        }

        if (batches.Count == 0 || batches.Select(b => b.Seq).Distinct().Count() != batches.Count || batches.Select(b => b.Table).Distinct().Count() != batches.Count)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A setup change set has one batch per table, each with its own place in the order.");
        }

        var set = new SetupChangeSet { Id = Guard.NotEmpty(id), SetNumber = References.Of("SC-", id, 19), SourceRef = sourceRef, MakerId = makerId };
        foreach (var b in batches.OrderBy(b => b.Seq))
        {
            set._batches.Add(SetupChangeSetBatchEntry.Create(id, b.Seq, b.Table, b.EntityCode, b.BatchId, b.ChangeRequestId, b.Lines, b.Hash));
        }

        set.Hash = HashOf(set._batches.Select(b => (b.Seq, b.TableName, b.BatchId, b.LineCount, b.Hash)));
        set.Raise(new SetupApprovalNeeded(id, approvalType, makerId, set.Hash, set._batches.Count, now));
        set.MarkChanged();
        return set;
    }

    /// <summary>Whether this checker's approval is already applied: a redelivery changes nothing.</summary>
    public bool ApprovalApplied(Guid checkerId) => Status is SetupSetStatuses.Applying or SetupSetStatuses.Applied && CheckerId == checkerId;

    /// <summary>
    /// Workflow's approval: refused to the maker (SRS-002, AIA-086), and unless it was given for this set's hash, so a set that changed needs a new approval.
    /// The first batch is then applied; <see cref="Next"/> names it.
    /// </summary>
    public Result Approve(Guid checkerId, string? subjectHash, DateTimeOffset now)
    {
        if (checkerId == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "A setup is approved by an identified user.");
        }

        if (checkerId == MakerId)
        {
            return MakerChecker.EnsureDifferentPeople(MakerId.ToString(), checkerId.ToString());
        }

        if (Status != SetupSetStatuses.Submitted)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The setup change set is {Status}.");
        }

        if (!string.Equals(subjectHash, Hash, StringComparison.Ordinal))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval was given for different contents.");
        }

        (Status, CheckerId, ApprovedAt) = (SetupSetStatuses.Applying, checkerId, now);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>The next batch to apply, in order; null once every batch is in force.</summary>
    public SetupChangeSetBatchEntry? Next() => Status == SetupSetStatuses.Applying ? _batches.OrderBy(b => b.Seq).FirstOrDefault(b => b.AppliedAt is null) : null;

    /// <summary>A batch is in force. Only the batch whose turn it is; a redelivery changes nothing. The last one completes the set.</summary>
    public Result BatchApplied(Guid batchId, DateTimeOffset now)
    {
        if (_batches.SingleOrDefault(b => b.BatchId == batchId) is not { } batch)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The batch is not in this setup change set.");
        }

        if (batch.AppliedAt is not null)
        {
            return Result.Success();
        }

        if (Next() != batch)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A setup change set's batches are applied in their order.");
        }

        batch.Applied(now);
        if (_batches.All(b => b.AppliedAt is not null))
        {
            (Status, AppliedAt) = (SetupSetStatuses.Applied, now);
            Raise(new SetupChangeSetCompleted(Id, SourceRef, CheckerId!.Value, now));
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>The approval ended without being granted (rejected, expired, withdrawn): nothing of the set is applied. A rejection by the maker is refused.</summary>
    public Result NotApproved(string ending, Guid checkerId, string reason, DateTimeOffset now)
    {
        if (Status == SetupSetStatuses.NotApproved)
        {
            return Result.Success();
        }

        if (checkerId != Guid.Empty && checkerId == MakerId)
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "A setup is rejected by a checker other than its maker.");
        }

        if (Status != SetupSetStatuses.Submitted)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The setup change set is {Status}.");
        }

        (Status, Ending, CheckerId) = (SetupSetStatuses.NotApproved, ending, checkerId == Guid.Empty ? null : checkerId);
        Raise(new SetupChangeSetEnded(Id, SourceRef, ending, reason, now));
        MarkChanged();
        return Result.Success();
    }
}
