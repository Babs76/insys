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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Contracts;
using Insys.Party.Domain.Parties;
using System.Text.Json;

namespace Insys.Party.Application;

// F059 (addenda/finance-and-compliance.md §4.5) and F064 (§4.7).

public sealed record ReceiveDataSubjectRequest(Guid PartyId, [property: OneOf(DataSubjectRequestTypes.Access, DataSubjectRequestTypes.Correction, DataSubjectRequestTypes.Deletion)] string RequestType)
    : ICommand<DataSubjectRequestView>;

/// <summary>A member raises their own request on the portal.</summary>
public sealed record ReceivePortalDataSubjectRequest([property: OneOf(DataSubjectRequestTypes.Access, DataSubjectRequestTypes.Correction, DataSubjectRequestTypes.Deletion)] string RequestType)
    : ICommand<DataSubjectRequestView>;

public sealed record ConfirmDataSubjectIdentity(Guid Id) : ICommand<DataSubjectRequestView>;

public sealed record ApproveDataSubjectRequest(Guid Id) : ICommand<DataSubjectRequestView>;

public sealed record RefuseDataSubjectRequest(Guid Id, string Reason) : ICommand<DataSubjectRequestView>;

/// <summary>An approved access request: the party's records as a file, audited as it leaves. Health records only for someone with the clinical function.</summary>
public sealed record ExportDataSubjectRequest(Guid Id) : ICommand<string>;

/// <summary>An approved correction, once made through the existing party change: the reference links them.</summary>
public sealed record CompleteDataSubjectCorrection(Guid Id, string Reference) : ICommand<DataSubjectRequestView>;

/// <summary>An approved deletion: the existing anonymise check. Anything keeping the party's data refuses it, with the reason as the answer to the data subject.</summary>
public sealed record ExecuteDataSubjectDeletion(Guid Id) : ICommand<DataSubjectRequestView>;

public sealed record GetDataSubjectRequest(Guid Id) : IQuery<DataSubjectRequestView>;

public sealed record ListDataSubjectRequests(Guid? PartyId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<DataSubjectRequestView>>;

public sealed record ListPortalDataSubjectRequests : IQuery<IReadOnlyList<DataSubjectRequestView>>;

/// <summary>The daily job's work (F064): each dormant or deceased party whose retention has ended gets one review.</summary>
public sealed record RaisePartyRetentionReviews(int Limit) : ICommand<int>;

public sealed record DataSubjectRequestView(Guid Id, string RequestNumber, Guid PartyId,
    [property: OneOf(DataSubjectRequestTypes.Access, DataSubjectRequestTypes.Correction, DataSubjectRequestTypes.Deletion)] string RequestType, DateOnly ReceivedOn, DateOnly DueOn,
    [property: OneOf(DataSubjectRequestStatus.Received, DataSubjectRequestStatus.IdentityConfirmed, DataSubjectRequestStatus.Approved, DataSubjectRequestStatus.Completed, DataSubjectRequestStatus.Refused)] string Status,
    string? Outcome, string? OutcomeReason, long RowVersion)
{
    public static DataSubjectRequestView From(DataSubjectRequest r) => new(r.Id, r.RequestNumber, r.PartyId, r.RequestType, r.ReceivedOn, r.DueOn, r.Status, r.Outcome, r.OutcomeReason, r.RowVersion);
}

public interface IDataSubjectRequests
{
    Task<DataSubjectRequest?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DataSubjectRequest>> List(Guid? partyId, Guid? afterId, int limit, CancellationToken cancellationToken);

    /// <summary>The tenant's days to answer in force; with none, <see cref="DataSubjectDefaults.ResponseDays"/> (OQ-159).</summary>
    Task<int> ResponseDays(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Dormant or deceased parties with no retention review raised yet.</summary>
    Task<IReadOnlyList<Guid>> RetentionCandidates(int limit, CancellationToken cancellationToken);

    void Add(DataSubjectRequest request);
}

public static class DataSubjectDefaults
{
    public const int ResponseDays = 30;

    /// <summary>Record types that reveal health information (HLT-008): in an export only for the clinical function.</summary>
    public static bool IsClinical(string recordType) => recordType.StartsWith("HEALTH_", StringComparison.Ordinal);
}

public sealed class RefuseDataSubjectRequestValidator : AbstractValidator<RefuseDataSubjectRequest>
{
    public RefuseDataSubjectRequestValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
}

public sealed class CompleteDataSubjectCorrectionValidator : AbstractValidator<CompleteDataSubjectCorrection>
{
    public CompleteDataSubjectCorrectionValidator() => RuleFor(c => c.Reference).NotEmpty().MaximumLength(1000);
}

internal sealed class DataSubjectRequestHandlers(IDataSubjectRequests requests, IPartyRepository parties, AnonymisationCheck check, IEnumerable<IPartyLinkedRecords> sources, IExecutionContext execution,
    ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<ReceiveDataSubjectRequest, DataSubjectRequestView>,
    ICommandHandler<ReceivePortalDataSubjectRequest, DataSubjectRequestView>,
    ICommandHandler<ConfirmDataSubjectIdentity, DataSubjectRequestView>,
    ICommandHandler<ApproveDataSubjectRequest, DataSubjectRequestView>,
    ICommandHandler<RefuseDataSubjectRequest, DataSubjectRequestView>,
    ICommandHandler<ExportDataSubjectRequest, string>,
    ICommandHandler<CompleteDataSubjectCorrection, DataSubjectRequestView>,
    ICommandHandler<ExecuteDataSubjectDeletion, DataSubjectRequestView>,
    ICommandHandler<RaisePartyRetentionReviews, int>,
    IQueryHandler<GetDataSubjectRequest, DataSubjectRequestView>,
    IQueryHandler<ListDataSubjectRequests, IReadOnlyList<DataSubjectRequestView>>,
    IQueryHandler<ListPortalDataSubjectRequests, IReadOnlyList<DataSubjectRequestView>>
{
    /// <summary>The clinical function's permission (Health's constant; Party doesn't reference Health).</summary>
    internal const string ClinicalRead = "health_case.read_clinical";

    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The request was not found.");

    private Guid? Person() => execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id) ? id : null;

    public async Task<Result<DataSubjectRequestView>> Handle(ReceiveDataSubjectRequest command, CancellationToken cancellationToken)
    {
        if (await parties.Get(command.PartyId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        return await Receive(command.PartyId, command.RequestType, Person(), cancellationToken);
    }

    public async Task<Result<DataSubjectRequestView>> Handle(ReceivePortalDataSubjectRequest command, CancellationToken cancellationToken) =>
        execution.External is { Kind: ExternalSubject.Party, PartyId: { } party }
            ? await Receive(party, command.RequestType, null, cancellationToken)
            : NotFound;

    private async Task<Result<DataSubjectRequestView>> Receive(Guid partyId, string type, Guid? by, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var today = businessDate.Today;
        var request = DataSubjectRequest.Receive(id, $"DSR-{id:N}"[..25], partyId, type, today, await requests.ResponseDays(today, cancellationToken), by, clock.UtcNow);
        requests.Add(request);
        audit.Record(new AuditEntry("PARTY", "ReceiveDataSubjectRequest", nameof(DataSubjectRequest), id.ToString(), new Dictionary<string, string?> { ["type"] = type }));
        return DataSubjectRequestView.From(request);
    }

    public Task<Result<DataSubjectRequestView>> Handle(ConfirmDataSubjectIdentity command, CancellationToken cancellationToken) =>
        Act(command.Id, "ConfirmDataSubjectIdentity", (r, user) => r.ConfirmIdentity(user), cancellationToken);

    public Task<Result<DataSubjectRequestView>> Handle(ApproveDataSubjectRequest command, CancellationToken cancellationToken) =>
        Act(command.Id, "ApproveDataSubjectRequest", (r, user) => r.Approve(user), cancellationToken);

    public Task<Result<DataSubjectRequestView>> Handle(RefuseDataSubjectRequest command, CancellationToken cancellationToken) =>
        Act(command.Id, "RefuseDataSubjectRequest", (r, _) => r.Refuse(command.Reason), cancellationToken);

    public Task<Result<DataSubjectRequestView>> Handle(CompleteDataSubjectCorrection command, CancellationToken cancellationToken) =>
        Act(command.Id, "CompleteDataSubjectCorrection", (r, _) => r.RequestType == DataSubjectRequestTypes.Correction ? r.Complete(command.Reference)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a correction request completes with a reference."), cancellationToken);

    private async Task<Result<DataSubjectRequestView>> Act(Guid id, string operation, Func<DataSubjectRequest, Guid, Result> act, CancellationToken cancellationToken)
    {
        if (Person() is not { } user)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "A person acts on a data-subject request.");
        }

        if (await requests.Get(id, cancellationToken) is not { } request)
        {
            return NotFound;
        }

        var acted = act(request, user);
        if (acted.IsFailure)
        {
            return acted.Error;
        }

        audit.Record(new AuditEntry("PARTY", operation, nameof(DataSubjectRequest), id.ToString()));
        return DataSubjectRequestView.From(request);
    }

    public async Task<Result<string>> Handle(ExportDataSubjectRequest command, CancellationToken cancellationToken)
    {
        if (await requests.Get(command.Id, cancellationToken) is not { } request)
        {
            return NotFound;
        }

        if (request.RequestType != DataSubjectRequestTypes.Access || request.Status is not (DataSubjectRequestStatus.Approved or DataSubjectRequestStatus.Completed))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an approved access request is exported.");
        }

        if (await parties.Get(request.PartyId, cancellationToken) is not { } party)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        var tenantId = tenant.RequireTenantId();
        var records = new List<PartyLinkedRecord>();
        foreach (var source in sources)
        {
            records.AddRange(await source.For(tenantId, request.PartyId, businessDate.Zone.Id, cancellationToken));
        }

        var clinical = execution.HasPermission(ClinicalRead);
        var shown = records.Where(r => clinical || !DataSubjectDefaults.IsClinical(r.RecordType)).OrderBy(r => r.RecordType, StringComparer.Ordinal).ThenBy(r => r.RecordId).ToList();
        if (request.Status == DataSubjectRequestStatus.Approved)
        {
            request.Complete(null);
        }

        audit.Record(new AuditEntry("PARTY", "ExportDataSubjectRequest", nameof(DataSubjectRequest), command.Id.ToString(), new Dictionary<string, string?>
        {
            ["records"] = shown.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["clinicalWithheld"] = (records.Count - shown.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        return JsonSerializer.Serialize(new { party = PartyView.From(party, i => i.NumberLast4 ?? ""), records = shown.Select(r => new { r.RecordType, r.RecordId, r.ClosedOn }) }, JsonSerializerOptions.Web);
    }

    public async Task<Result<DataSubjectRequestView>> Handle(ExecuteDataSubjectDeletion command, CancellationToken cancellationToken)
    {
        if (await requests.Get(command.Id, cancellationToken) is not { } request)
        {
            return NotFound;
        }

        if (request.RequestType != DataSubjectRequestTypes.Deletion || request.Status != DataSubjectRequestStatus.Approved)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an approved deletion request is run.");
        }

        if (await parties.Get(request.PartyId, cancellationToken) is not { } party)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        string? refusal = null;
        if (party.Status is not (PartyStatus.Deceased or PartyStatus.Dormant))
        {
            refusal = "The relationship is still active.";
        }
        else if (await check.Blocks(party.Id, cancellationToken) is { Count: > 0 } blocks)
        {
            refusal = "Records are still kept: " + string.Join(", ", blocks.Select(b => b.Until is { } until ? $"{b.Reason} until {until:yyyy-MM-dd}" : b.Reason).Distinct(StringComparer.Ordinal));
        }

        audit.Record(new AuditEntry("PARTY", "ExecuteDataSubjectDeletion", nameof(DataSubjectRequest), command.Id.ToString(), new Dictionary<string, string?> { ["refused"] = refusal is null ? "false" : "true" }));
        if (refusal is not null)
        {
            request.Refuse(refusal);
            return DataSubjectRequestView.From(request);
        }

        var anonymised = party.AnonymiseParty(clock.UtcNow);
        if (anonymised.IsFailure)
        {
            return anonymised.Error;
        }

        request.Complete(null);
        return DataSubjectRequestView.From(request);
    }

    public async Task<Result<int>> Handle(RaisePartyRetentionReviews command, CancellationToken cancellationToken)
    {
        var raised = 0;
        foreach (var id in await requests.RetentionCandidates(command.Limit, cancellationToken))
        {
            if ((await check.Blocks(id, cancellationToken)).Count == 0 && await parties.Get(id, cancellationToken) is { } party)
            {
                party.RaiseRetentionReview(businessDate.Today, clock.UtcNow);
                raised++;
            }
        }

        return raised;
    }

    public async Task<Result<DataSubjectRequestView>> Handle(GetDataSubjectRequest query, CancellationToken cancellationToken) =>
        await requests.Get(query.Id, cancellationToken) is { } request ? DataSubjectRequestView.From(request) : NotFound;

    public async Task<Result<IReadOnlyList<DataSubjectRequestView>>> Handle(ListDataSubjectRequests query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<DataSubjectRequestView>>((await requests.List(query.PartyId, query.AfterId, query.Limit, cancellationToken)).Select(DataSubjectRequestView.From).ToList());

    public async Task<Result<IReadOnlyList<DataSubjectRequestView>>> Handle(ListPortalDataSubjectRequests query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<DataSubjectRequestView>>(execution.External is { Kind: ExternalSubject.Party, PartyId: { } party }
            ? (await requests.List(party, null, 100, cancellationToken)).Select(DataSubjectRequestView.From).ToList()
            : []);
}
