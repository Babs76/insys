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
using FluentValidation;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Contracts;
using Insys.Health.Domain;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Payments;
using Insys.Workflow.Contracts;

namespace Insys.Health.Application;

// Overpayment recovery, credit notes and disputes (addenda/providers-and-care.md §4.6 to §4.8; F048 to F050, OQ-139, OQ-145, OQ-146).

public sealed record RecoveryOffsetView(Guid Id, Guid RunId, Guid RemittanceId, decimal Amount, [property: OneOf(OffsetStatuses.Reserved, OffsetStatuses.Taken, OffsetStatuses.Released)] string Status);

public sealed record ProviderRecoveryView(Guid Id, string RecoveryNumber, [property: OneOf(RecoveryDebtors.Provider, RecoveryDebtors.Member)] string DebtorKind, string? ProviderId,
    [property: OneOf(RecoverySources.Reversal, RecoverySources.CreditNote, RecoverySources.Finding)] string Source, Guid? ClaimId, string? ClaimNumber, decimal OriginalAmount, decimal Balance, string Currency,
    [property: OneOf(RecoveryBases.Offset, RecoveryBases.Invoice, RecoveryBases.Person)] string Basis,
    [property: OneOf(RecoveryStatuses.Proposed, RecoveryStatuses.Open, RecoveryStatuses.Invoiced, RecoveryStatuses.Recovered, RecoveryStatuses.WrittenOff, RecoveryStatuses.Reversed, RecoveryStatuses.Rejected)] string Status,
    string Reason, DateOnly RaisedOn, DateOnly? ReviewDueOn, bool InReview, Guid? HeldByCaseId, Guid? InvoiceId, string? WriteOffReason, decimal RepayAmount, Guid? RepaidInRunId, DateOnly? ClosedOn,
    IReadOnlyList<RecoveryOffsetView> Offsets, long RowVersion)
{
    public static ProviderRecoveryView From(ProviderRecovery r) => new(r.Id, r.RecoveryNumber, r.DebtorKind, r.ProviderId, r.Source, r.ClaimId, r.ClaimNumber, r.OriginalAmount, r.Balance, r.Currency, r.Basis, r.Status,
        r.Reason, r.RaisedOn, r.ReviewDueOn, r.InReview, r.HeldByCaseId, r.InvoiceId, r.WriteOffReason, r.RepayAmount, r.RepaidInRunId, r.ClosedOn,
        r.Offsets.Select(o => new RecoveryOffsetView(o.Id, o.RunId, o.RemittanceId, o.Amount, o.Status)).ToList(), r.RowVersion);
}

/// <summary>A provider's view of what it owes: no internal reason, review or approval.</summary>
public sealed record PortalRecoveryView(Guid Id, string RecoveryNumber, [property: OneOf(RecoverySources.Reversal, RecoverySources.CreditNote, RecoverySources.Finding)] string Source, string? ClaimNumber,
    decimal OriginalAmount, decimal Balance, string Currency, [property: OneOf(RecoveryBases.Offset, RecoveryBases.Invoice, RecoveryBases.Person)] string Basis,
    [property: OneOf(RecoveryStatuses.Proposed, RecoveryStatuses.Open, RecoveryStatuses.Invoiced, RecoveryStatuses.Recovered, RecoveryStatuses.WrittenOff, RecoveryStatuses.Reversed, RecoveryStatuses.Rejected)] string Status,
    bool Disputed, DateOnly RaisedOn, IReadOnlyList<RecoveryOffsetView> Offsets)
{
    public static PortalRecoveryView From(ProviderRecovery r) => new(r.Id, r.RecoveryNumber, r.Source, r.ClaimNumber, r.OriginalAmount, r.Balance, r.Currency, r.Basis, r.Status, r.HeldByCaseId is not null, r.RaisedOn,
        r.Offsets.Where(o => o.Status != OffsetStatuses.Released).Select(o => new RecoveryOffsetView(o.Id, o.RunId, o.RemittanceId, o.Amount, o.Status)).ToList());
}

public sealed record ProviderCreditNoteView(Guid Id, string CreditNoteNumber, string ProviderId, string ProviderReference, Guid? ClaimId, Guid? RemittanceId, decimal Amount, string Currency,
    [property: OneOf(CreditNoteStatuses.Submitted, CreditNoteStatuses.Approved, CreditNoteStatuses.Rejected)] string Status, string Reason, DateTimeOffset? DecidedAt, Guid? RecoveryId, long RowVersion)
{
    public static ProviderCreditNoteView From(ProviderCreditNote n) => new(n.Id, n.CreditNoteNumber, n.ProviderId, n.ProviderReference, n.ClaimId, n.RemittanceId, n.Amount, n.Currency, n.Status, n.Reason, n.DecidedAt, n.RecoveryId, n.RowVersion);
}

/// <param name="ReplacesClaimNumber">The reversed claim this one replaces.</param>
public sealed record RemittanceClaimLine(Guid ClaimId, string ClaimNumber, decimal Amount, string? ReplacesClaimNumber);

/// <param name="OriginalClaimNumber">The claim first paid, then reversed.</param>
/// <param name="ReversedOn">When it was reversed.</param>
/// <param name="NewClaimNumber">The claim that replaced it, if any.</param>
public sealed record RemittanceOffsetLine(string RecoveryNumber, string? OriginalClaimNumber, DateOnly? ReversedOn, string? NewClaimNumber, decimal Amount);

/// <summary>A remittance as a provider reads it: the original, the reversal, the new claim and the net (5a.3).</summary>
public sealed record RemittanceDetailView(Guid Id, Guid RunId, string RunCode, string ProviderId, string Currency, decimal GrossAmount, decimal OffsetAmount, decimal RepaidAmount, decimal NetAmount, bool PaymentDue,
    Guid? PaymentId, DateOnly? PaidOn, Guid? DocumentId, IReadOnlyList<RemittanceClaimLine> Claims, IReadOnlyList<RemittanceOffsetLine> Offsets);

public sealed record ListProviderRecoveries(string? ProviderId, string? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProviderRecoveryView>>;

public sealed record GetProviderRecovery(Guid RecoveryId) : IQuery<ProviderRecoveryView>;

public sealed record ProposeRecoveryFinding(string ProviderId, decimal Amount, string Currency, string Reason) : ICommand<ProviderRecoveryView>;

public sealed record InvoiceProviderRecovery(Guid RecoveryId) : ICommand<ProviderRecoveryView>;

public sealed record RequestRecoveryWriteOff(Guid RecoveryId, string Reason) : ICommand<ProviderRecoveryView>;

public sealed record ReverseProviderRecovery(Guid RecoveryId, string Reason) : ICommand<ProviderRecoveryView>;

/// <summary>A dispute case that names no remittance or claim of the recovery holds it directly (OQ-146).</summary>
public sealed record HoldRecoveryForDispute(Guid RecoveryId, Guid CaseId) : ICommand<ProviderRecoveryView>;

public sealed record SubmitCreditNote(string ProviderId, string ProviderReference, Guid? ClaimId, Guid? RemittanceId, decimal Amount, string Currency, string Reason) : ICommand<ProviderCreditNoteView>;

public sealed record SubmitPortalCreditNote(string ProviderReference, Guid? ClaimId, Guid? RemittanceId, decimal Amount, string Currency, string Reason) : ICommand<ProviderCreditNoteView>;

public sealed record GetCreditNote(Guid CreditNoteId) : IQuery<ProviderCreditNoteView>;

public sealed record ListCreditNotes(string? ProviderId, string? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProviderCreditNoteView>>;

public sealed record ListPortalRecoveries(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalRecoveryView>>;

public sealed record ListPortalCreditNotes(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProviderCreditNoteView>>;

public sealed record GetRemittanceDetail(Guid RemittanceId) : IQuery<RemittanceDetailView>;

public sealed record GetPortalRemittance(Guid RemittanceId) : IQuery<RemittanceDetailView>;

public sealed record FindRecoveriesDue(DateOnly Day, int Limit) : IQuery<IReadOnlyList<Guid>>;

public sealed record SendRecoveryToReview(Guid RecoveryId) : ICommand<bool>;

public sealed class ProposeRecoveryFindingValidator : AbstractValidator<ProposeRecoveryFinding>
{
    public ProposeRecoveryFindingValidator()
    {
        RuleFor(c => c.ProviderId).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Amount).GreaterThan(0);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class RequestRecoveryWriteOffValidator : AbstractValidator<RequestRecoveryWriteOff>
{
    public RequestRecoveryWriteOffValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

public sealed class ReverseProviderRecoveryValidator : AbstractValidator<ReverseProviderRecovery>
{
    public ReverseProviderRecoveryValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(450);
}

public sealed class SubmitCreditNoteValidator : AbstractValidator<SubmitCreditNote>
{
    public SubmitCreditNoteValidator()
    {
        RuleFor(c => c.ProviderId).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ProviderReference).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class SubmitPortalCreditNoteValidator : AbstractValidator<SubmitPortalCreditNote>
{
    public SubmitPortalCreditNoteValidator()
    {
        RuleFor(c => c.ProviderReference).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>How a recovery is raised: the provider agreement's offset flag, the tenant's review period, the notice's party.</summary>
public sealed class RecoveryRaiser(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IClock clock)
{
    public const int DefaultReviewDays = 90;

    public async Task<(bool Offset, DateOnly ReviewDueOn, string? CollectionMethod)> Terms(string? providerId, CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        var settings = await codes.RecoverySettings(today, cancellationToken);
        var offset = providerId is not null && (await codes.ProviderOffsetFlags(providerId, today, cancellationToken) is { Count: > 0 } flags && flags.Any(f => f is not null)
            ? flags.Where(f => f is not null).All(f => f == true)
            : settings?.MedicalScheme ?? false);
        return (offset, today.AddDays(settings?.ReviewAfterDays ?? DefaultReviewDays), settings?.InvoiceCollectionMethod);
    }

    /// <summary>Raised once per source: a redelivery finds the one already raised.</summary>
    public async Task<ProviderRecovery> Raise(string debtorKind, string? providerId, Guid? partyId, string source, Guid sourceId, Guid? claimId, string? claimNumber, decimal amount, string currency, string reason,
        CancellationToken cancellationToken)
    {
        if (await health.RecoveryForSource(source, sourceId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var (offset, reviewDue, method) = await Terms(providerId, cancellationToken);
        var id = Guid.CreateVersion7();
        var recovery = ProviderRecovery.Raise(id, Numbers.Of("RC", id, 25), debtorKind, providerId, partyId, source, sourceId, claimId, claimNumber, amount, currency, offset, reason, businessDate.Today, reviewDue, method, clock.UtcNow);
        health.Add(recovery);
        return recovery;
    }
}

internal sealed class RecoveryHandlers(IHealthRepository health, IHealthCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    IQueryHandler<ListProviderRecoveries, IReadOnlyList<ProviderRecoveryView>>,
    IQueryHandler<GetProviderRecovery, ProviderRecoveryView>,
    ICommandHandler<ProposeRecoveryFinding, ProviderRecoveryView>,
    ICommandHandler<InvoiceProviderRecovery, ProviderRecoveryView>,
    ICommandHandler<RequestRecoveryWriteOff, ProviderRecoveryView>,
    ICommandHandler<ReverseProviderRecovery, ProviderRecoveryView>,
    ICommandHandler<HoldRecoveryForDispute, ProviderRecoveryView>,
    ICommandHandler<SubmitCreditNote, ProviderCreditNoteView>,
    IQueryHandler<GetCreditNote, ProviderCreditNoteView>,
    IQueryHandler<ListCreditNotes, IReadOnlyList<ProviderCreditNoteView>>,
    IQueryHandler<FindRecoveriesDue, IReadOnlyList<Guid>>,
    ICommandHandler<SendRecoveryToReview, bool>
{
    private static readonly Error NotFound = Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");

    /// <summary>A provider's recoveries span products and branches, so someone with a restricted scope does not work on them (F103).</summary>
    private Error? OutOfScope => execution.CurrentScope.IsUnrestricted ? null : Error.Forbidden(ErrorCodes.OutOfScope, "A provider's recoveries span products; they need unrestricted scope.");

    public async Task<Result<IReadOnlyList<ProviderRecoveryView>>> Handle(ListProviderRecoveries query, CancellationToken cancellationToken) =>
        OutOfScope is { } refused ? refused : Result.Success<IReadOnlyList<ProviderRecoveryView>>((await health.Recoveries(query.ProviderId, query.Status, query.AfterId, query.Limit, cancellationToken)).Select(ProviderRecoveryView.From).ToList());

    public async Task<Result<ProviderRecoveryView>> Handle(GetProviderRecovery query, CancellationToken cancellationToken) =>
        OutOfScope is { } refused ? refused : await health.Recovery(query.RecoveryId, cancellationToken) is { } recovery ? ProviderRecoveryView.From(recovery) : NotFound;

    public async Task<Result<ProviderRecoveryView>> Handle(ProposeRecoveryFinding command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (OutOfScope is { } refused)
        {
            return refused;
        }

        if (await codes.Provider(command.ProviderId, businessDate.Today, cancellationToken) is not { } provider)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.ProviderId} is not a provider in force.");
        }

        var id = Guid.CreateVersion7();
        var recovery = ProviderRecovery.ProposeFinding(id, Numbers.Of("RC", id, 25), command.ProviderId, provider.PartyId, command.Amount, command.Currency, command.Reason, user.Value, businessDate.Today, clock.UtcNow);
        health.Add(recovery);
        audit.Record(new AuditEntry("HEALTH", "ProposeRecoveryFinding", nameof(ProviderRecovery), id.ToString(), new Dictionary<string, string?> { ["provider"] = command.ProviderId, ["reason"] = command.Reason }));
        return ProviderRecoveryView.From(recovery);
    }

    public async Task<Result<ProviderRecoveryView>> Handle(InvoiceProviderRecovery command, CancellationToken cancellationToken)
    {
        var method = (await codes.RecoverySettings(businessDate.Today, cancellationToken))?.InvoiceCollectionMethod;
        return await Apply(command.RecoveryId, "InvoiceRecovery", null, (r, _) => r.Invoice(businessDate.Today, method, clock.UtcNow), cancellationToken);
    }

    public Task<Result<ProviderRecoveryView>> Handle(RequestRecoveryWriteOff command, CancellationToken cancellationToken) =>
        Apply(command.RecoveryId, "RequestWriteOff", command.Reason, (r, user) => r.RequestWriteOff(command.Reason, user, clock.UtcNow), cancellationToken);

    public Task<Result<ProviderRecoveryView>> Handle(ReverseProviderRecovery command, CancellationToken cancellationToken) =>
        Apply(command.RecoveryId, "ReverseRecovery", command.Reason, (r, _) => r.Reverse(command.Reason, businessDate.Today, clock.UtcNow), cancellationToken);

    public Task<Result<ProviderRecoveryView>> Handle(HoldRecoveryForDispute command, CancellationToken cancellationToken) =>
        Apply(command.RecoveryId, "HoldRecovery", null, (r, _) =>
        {
            r.HoldForDispute(command.CaseId);
            return r.HeldByCaseId == command.CaseId ? Result.Success() : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Recovery {r.RecoveryNumber} is {r.Status}, or held by another case.");
        }, cancellationToken);

    public async Task<Result<ProviderCreditNoteView>> Handle(SubmitCreditNote command, CancellationToken cancellationToken) =>
        OutOfScope is { } refused
            ? refused
            : await CreditNotes.Submit(health, codes, execution, businessDate, clock, audit, command.ProviderId, command.ProviderReference, command.ClaimId, command.RemittanceId, command.Amount, command.Currency, command.Reason, cancellationToken);

    public async Task<Result<ProviderCreditNoteView>> Handle(GetCreditNote query, CancellationToken cancellationToken) =>
        OutOfScope is { } refused ? refused : await health.CreditNote(query.CreditNoteId, cancellationToken) is { } note ? ProviderCreditNoteView.From(note) : Error.NotFound(ErrorCodes.NotFound, "The credit note was not found.");

    public async Task<Result<IReadOnlyList<ProviderCreditNoteView>>> Handle(ListCreditNotes query, CancellationToken cancellationToken) =>
        OutOfScope is { } refused ? refused : Result.Success<IReadOnlyList<ProviderCreditNoteView>>((await health.CreditNotes(query.ProviderId, query.Status, query.AfterId, query.Limit, cancellationToken)).Select(ProviderCreditNoteView.From).ToList());

    public async Task<Result<IReadOnlyList<Guid>>> Handle(FindRecoveriesDue query, CancellationToken cancellationToken) =>
        Result.Success(await health.RecoveriesDueForReview(query.Day, query.Limit, cancellationToken));

    public async Task<Result<bool>> Handle(SendRecoveryToReview command, CancellationToken cancellationToken)
    {
        if (await health.Recovery(command.RecoveryId, cancellationToken) is { } recovery)
        {
            recovery.SendToReview(clock.UtcNow);
        }

        return true;
    }

    private async Task<Result<ProviderRecoveryView>> Apply(Guid id, string operation, string? reason, Func<ProviderRecovery, Guid, Result> change, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (OutOfScope is { } refused)
        {
            return refused;
        }

        if (await health.Recovery(id, cancellationToken) is not { } recovery)
        {
            return NotFound;
        }

        audit.Record(new AuditEntry("HEALTH", operation, nameof(ProviderRecovery), id.ToString(), new Dictionary<string, string?> { ["reason"] = reason }));
        var result = change(recovery, user.Value);
        return result.IsFailure ? result.Error : ProviderRecoveryView.From(recovery);
    }
}

internal static class CreditNotes
{
    public static async Task<Result<ProviderCreditNoteView>> Submit(IHealthRepository health, IHealthCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit, string providerId,
        string reference, Guid? claimId, Guid? remittanceId, decimal amount, string currency, string reason, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await codes.Provider(providerId, businessDate.Today, cancellationToken) is null)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{providerId} is not a provider in force.");
        }

        // It credits only this provider's own claim or remittance, as the portal's read would show it.
        if (claimId is { } claim && (await health.Claim(claim, cancellationToken))?.ProviderId != providerId)
        {
            return Error.Validation(HealthErrorCodes.CreditNoteInvalid, "The claim is not this provider's.");
        }

        if (remittanceId is { } remittance && (await health.RunForRemittance(remittance, cancellationToken))?.Remittances.SingleOrDefault(r => r.Id == remittance)?.ProviderId != providerId)
        {
            return Error.Validation(HealthErrorCodes.CreditNoteInvalid, "The remittance is not this provider's.");
        }

        if (await health.CreditNoteExists(providerId, reference, cancellationToken))
        {
            return Error.Conflict(HealthErrorCodes.CreditNoteInvalid, $"Credit note {reference} of {providerId} is already recorded.");
        }

        var id = Guid.CreateVersion7();
        var note = ProviderCreditNote.Submit(id, Numbers.Of("CN", id, 25), providerId, reference, claimId, remittanceId, amount, currency, reason, user.Value, clock.UtcNow);
        if (note.IsFailure)
        {
            return note.Error;
        }

        health.Add(note.Value);
        audit.Record(new AuditEntry("HEALTH", "SubmitCreditNote", nameof(ProviderCreditNote), id.ToString(), new Dictionary<string, string?> { ["provider"] = providerId, ["reference"] = reference }));
        return ProviderCreditNoteView.From(note.Value);
    }
}

/// <summary>A provider's own recoveries, credit notes and remittances: as itself, whatever a request says (external-user access §3).</summary>
internal sealed class PortalRecoveryHandlers(IHealthRepository health, IHealthCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    IQueryHandler<ListPortalRecoveries, IReadOnlyList<PortalRecoveryView>>,
    IQueryHandler<ListPortalCreditNotes, IReadOnlyList<ProviderCreditNoteView>>,
    ICommandHandler<SubmitPortalCreditNote, ProviderCreditNoteView>,
    IQueryHandler<GetPortalRemittance, RemittanceDetailView>,
    IQueryHandler<GetRemittanceDetail, RemittanceDetailView>
{
    private static readonly Error NotFound = Error.NotFound(ErrorCodes.NotFound, "The remittance was not found.");

    private string ProviderId => execution.External?.ProviderId ?? throw new InvalidOperationException("A provider portal endpoint is used by a provider only.");

    public async Task<Result<IReadOnlyList<PortalRecoveryView>>> Handle(ListPortalRecoveries query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalRecoveryView>>((await health.Recoveries(ProviderId, null, query.AfterId, query.Limit, cancellationToken))
            .Where(r => r.Status is not (RecoveryStatuses.Proposed or RecoveryStatuses.Rejected)).Select(PortalRecoveryView.From).ToList());

    public async Task<Result<IReadOnlyList<ProviderCreditNoteView>>> Handle(ListPortalCreditNotes query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ProviderCreditNoteView>>((await health.CreditNotes(ProviderId, null, query.AfterId, query.Limit, cancellationToken)).Select(ProviderCreditNoteView.From).ToList());

    public Task<Result<ProviderCreditNoteView>> Handle(SubmitPortalCreditNote command, CancellationToken cancellationToken) =>
        CreditNotes.Submit(health, codes, execution, businessDate, clock, audit, ProviderId, command.ProviderReference, command.ClaimId, command.RemittanceId, command.Amount, command.Currency, command.Reason, cancellationToken);

    public async Task<Result<RemittanceDetailView>> Handle(GetPortalRemittance query, CancellationToken cancellationToken) =>
        await Detail(query.RemittanceId, ProviderId, cancellationToken);

    public async Task<Result<RemittanceDetailView>> Handle(GetRemittanceDetail query, CancellationToken cancellationToken)
    {
        if (await health.RunForRemittance(query.RemittanceId, cancellationToken) is not { } run)
        {
            return NotFound;
        }

        // F103: only a remittance whose claims are all in the caller's scope.
        var claims = await health.ClaimsInRun(run.Id, cancellationToken);
        var scopes = await health.MembershipScopes(claims.Select(c => c.MembershipId).Distinct().ToList(), cancellationToken);
        return RunVisibility.Of(run.Remittances, claims, scopes, execution.CurrentScope).Visible.Any(r => r.Id == query.RemittanceId)
            ? await Detail(query.RemittanceId, null, cancellationToken)
            : NotFound;
    }

    private async Task<Result<RemittanceDetailView>> Detail(Guid remittanceId, string? onlyProvider, CancellationToken cancellationToken)
    {
        if (await health.RunForRemittance(remittanceId, cancellationToken) is not { } run || run.Remittances.SingleOrDefault(r => r.Id == remittanceId) is not { } remittance
            || (onlyProvider is not null && remittance.ProviderId != onlyProvider))
        {
            return NotFound;
        }

        var claims = (await health.ClaimsInRun(run.Id, cancellationToken)).Where(c => c.Source != ClaimSources.Member && c.ProviderId == remittance.ProviderId).ToList();
        var lines = new List<RemittanceClaimLine>();
        foreach (var claim in claims.OrderBy(c => c.ClaimNumber, StringComparer.Ordinal))
        {
            lines.Add(new RemittanceClaimLine(claim.Id, claim.ClaimNumber, claim.TotalPaid ?? 0, claim.ReplacesClaimId is { } original ? (await health.Claim(original, cancellationToken))?.ClaimNumber : null));
        }

        var offsets = new List<RemittanceOffsetLine>();
        foreach (var recovery in await health.RecoveriesOnRemittance(remittanceId, cancellationToken))
        {
            var original = recovery.ClaimId is { } claimId ? await health.Claim(claimId, cancellationToken) : null;
            var replacement = original?.ReplacedByClaimId is { } next ? await health.Claim(next, cancellationToken) : null;
            offsets.AddRange(recovery.Offsets.Where(o => o.RemittanceId == remittanceId && o.Status != OffsetStatuses.Released)
                .Select(o => new RemittanceOffsetLine(recovery.RecoveryNumber, recovery.ClaimNumber, original?.ReversedAt is { } at ? businessDate.DateOf(at) : null, replacement?.ClaimNumber, o.Amount)));
        }

        return new RemittanceDetailView(remittance.Id, run.Id, run.RunCode, remittance.ProviderId, run.Currency, remittance.GrossAmount, remittance.OffsetAmount, remittance.RepaidAmount, remittance.Amount, remittance.Amount > 0,
            remittance.PaymentId, remittance.PaidOn, remittance.DocumentId, lines, offsets);
    }
}

/// <summary>
/// Reactions: a reversed claim raises its recovery (a member's goes to a person, OQ-145); Workflow decides credit notes, findings and write-offs;
/// a dispute holds a recovery from offsets until its case closes (OQ-146); a recovery's invoice settles it.
/// </summary>
public sealed class RecoveryPolicies(IHealthRepository health, IHealthCodes codes, RecoveryRaiser raiser, IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<Contracts.MedicalClaimReversed>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<CaseOpened>,
    IIntegrationEventHandler<CaseClosed>,
    IIntegrationEventHandler<InvoiceIssued>,
    IIntegrationEventHandler<InvoiceSettled>
{
    public async Task Handle(Contracts.MedicalClaimReversed reversed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (reversed.Amount <= 0 || await health.Claim(reversed.ClaimId, cancellationToken) is not { } claim)
        {
            return;
        }

        if (claim.Source == ClaimSources.Member)
        {
            var party = (await health.Membership(claim.MembershipId, cancellationToken))?.PrincipalPartyId;
            await raiser.Raise(RecoveryDebtors.Member, null, party, RecoverySources.Reversal, claim.Id, claim.Id, claim.ClaimNumber, reversed.Amount, reversed.Currency, reversed.Reason, cancellationToken);
            return;
        }

        var provider = reversed.ProviderId is { } id ? await codes.Provider(id, businessDate.Today, cancellationToken) : null;
        await raiser.Raise(RecoveryDebtors.Provider, reversed.ProviderId, provider?.PartyId, RecoverySources.Reversal, claim.Id, claim.Id, claim.ClaimNumber, reversed.Amount, reversed.Currency, reversed.Reason, cancellationToken);
    }

    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(true, granted.BusinessRefType, granted.BusinessRefId, granted.ApprovalType, granted.ApprovalId, granted.CheckerId, granted.SubjectHash, PrerequisiteCodes.ApprovalNotApplied, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(false, rejected.BusinessRefType, rejected.BusinessRefId, rejected.ApprovalType, rejected.ApprovalId, rejected.CheckerId, rejected.SubjectHash, PrerequisiteCodes.ApprovalEndingNotApplied, cancellationToken);

    private async Task Decide(bool approved, string refType, Guid refId, string approvalType, Guid approvalId, Guid? checker, string? hash, string prerequisite, CancellationToken cancellationToken)
    {
        var who = checker ?? Guid.Empty;
        Result result;
        if (refType == HealthApprovals.CreditNoteBusinessRefType)
        {
            if (await health.CreditNote(refId, cancellationToken) is not { } note || note.DecidedByApproval(approvalId))
            {
                return;
            }

            Guid? recoveryId = null;
            if (approved && note.SubjectHash == hash && note.Status == CreditNoteStatuses.Submitted)
            {
                var provider = await codes.Provider(note.ProviderId, businessDate.Today, cancellationToken);
                var claimNumber = note.ClaimId is { } claimId ? (await health.Claim(claimId, cancellationToken))?.ClaimNumber : null;
                recoveryId = (await raiser.Raise(RecoveryDebtors.Provider, note.ProviderId, provider?.PartyId, RecoverySources.CreditNote, note.Id, note.ClaimId, claimNumber, note.Amount, note.Currency,
                    $"Credit note {note.ProviderReference}", cancellationToken)).Id;
            }

            result = note.Decide(approved, approvalId, who, hash, recoveryId, clock.UtcNow);
        }
        else if (refType == HealthApprovals.RecoveryBusinessRefType)
        {
            if (await health.Recovery(refId, cancellationToken) is not { } recovery || recovery.ApprovalId == approvalId)
            {
                return;
            }

            if (approvalType == HealthApprovals.RecoveryFindingApprovalType)
            {
                var (offset, reviewDue, method) = await raiser.Terms(recovery.ProviderId, cancellationToken);
                result = recovery.DecideFinding(approved, approvalId, who, hash, offset, businessDate.Today, reviewDue, method, clock.UtcNow);
            }
            else
            {
                result = recovery.DecideWriteOff(approved, approvalId, who, hash, businessDate.Today, clock.UtcNow);
            }
        }
        else
        {
            return;
        }

        if (result.IsFailure)
        {
            throw new PrerequisiteMissingException(prerequisite, $"{approvalType}:{approvalId}");
        }
    }

    public async Task Handle(CaseOpened opened, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (opened.Family != CaseFamilies.Dispute)
        {
            return;
        }

        foreach (var link in opened.Links)
        {
            var recoveries = link.LinkType switch
            {
                CaseLinkTypes.Remittance => await health.RecoveriesOnRemittance(link.RecordId, cancellationToken),
                CaseLinkTypes.PaymentRun => await health.RecoveriesInRun(link.RecordId, cancellationToken),
                CaseLinkTypes.Claim => await health.RecoveriesOfClaim(link.RecordId, cancellationToken),
                _ => [],
            };
            foreach (var recovery in recoveries)
            {
                recovery.HoldForDispute(opened.CaseId);
            }
        }
    }

    public async Task Handle(CaseClosed closed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var recovery in await health.RecoveriesHeldBy(closed.CaseId, cancellationToken))
        {
            recovery.ReleaseHold(closed.CaseId);
        }
    }

    public async Task Handle(InvoiceIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (issued.SourceDomain == HealthReferences.RecoverySourceDomain && await health.Recovery(issued.PolicyRef, cancellationToken) is { } recovery)
        {
            recovery.InvoiceIssued(issued.InvoiceId);
        }
    }

    public async Task Handle(InvoiceSettled settled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (settled.SourceDomain == HealthReferences.RecoverySourceDomain && await health.Recovery(settled.PolicyRef, cancellationToken) is { } recovery)
        {
            recovery.InvoiceSettled(businessDate.Today, clock.UtcNow);
        }
    }
}
