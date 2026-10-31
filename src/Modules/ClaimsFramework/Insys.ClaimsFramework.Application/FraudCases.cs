/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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
using Insys.ClaimsFramework.Domain.Claims;

namespace Insys.ClaimsFramework.Application;

// F058 (addenda/finance-and-compliance.md §4.8, OQ-160): every read is a command, so its audit entry is written.

public sealed record OpenFraudCase([property: OneOf("LIFE", "GENERAL")] string Domain, string? ClaimNumber, Guid? PolicyRef, IReadOnlyList<string> IndicatorCodes) : ICommand<FraudCaseView>;

public sealed record AssignFraudInvestigator(Guid Id, Guid InvestigatorId) : ICommand<FraudCaseView>;

public sealed record ConcludeFraudCase(Guid Id, [property: OneOf(FraudOutcomes.Confirmed, FraudOutcomes.NotConfirmed)] string Outcome, string Reason) : ICommand<FraudCaseView>;

public sealed record ReadFraudCase(Guid Id) : ICommand<FraudCaseView>;

public sealed record ReadFraudCases(Guid? AfterId, int Limit) : ICommand<IReadOnlyList<FraudCaseView>>;

public sealed record FraudCaseView(Guid Id, string CaseNumber, [property: OneOf("LIFE", "GENERAL")] string Domain, string? ClaimNumber, Guid? PolicyRef, IReadOnlyList<string> IndicatorCodes,
    [property: OneOf(FraudCaseStatus.Opened, FraudCaseStatus.Investigating, FraudCaseStatus.Concluded)] string Status, Guid? InvestigatorId, string? Outcome, string? OutcomeReason, long RowVersion)
{
    public static FraudCaseView From(FraudCase c) => new(c.Id, c.CaseNumber, c.Domain, c.ClaimNumber, c.PolicyRef, c.IndicatorCodes, c.Status, c.InvestigatorId, c.Outcome, c.OutcomeReason, c.RowVersion);
}

public interface IFraudCases
{
    Task<FraudCase?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<FraudCase>> List(Guid? afterId, int limit, CancellationToken cancellationToken);

    void Add(FraudCase fraudCase);
}

public sealed class OpenFraudCaseValidator : AbstractValidator<OpenFraudCase>
{
    public OpenFraudCaseValidator() => RuleFor(c => c.ClaimNumber).MaximumLength(25);
}

public sealed class ConcludeFraudCaseValidator : AbstractValidator<ConcludeFraudCase>
{
    public ConcludeFraudCaseValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
}

internal sealed class FraudCaseHandlers(IFraudCases cases, IExecutionContext execution, IClock clock, IAuditTrail audit) :
    ICommandHandler<OpenFraudCase, FraudCaseView>,
    ICommandHandler<AssignFraudInvestigator, FraudCaseView>,
    ICommandHandler<ConcludeFraudCase, FraudCaseView>,
    ICommandHandler<ReadFraudCase, FraudCaseView>,
    ICommandHandler<ReadFraudCases, IReadOnlyList<FraudCaseView>>
{
    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The case was not found.");

    private Guid? Person() => execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id) ? id : null;

    private static Error NoPerson => Error.Forbidden(ErrorCodes.Forbidden, "A person acts on a fraud case.");

    public Task<Result<FraudCaseView>> Handle(OpenFraudCase command, CancellationToken cancellationToken)
    {
        if (Person() is not { } user)
        {
            return Task.FromResult<Result<FraudCaseView>>(NoPerson);
        }

        var id = Guid.CreateVersion7();
        var opened = FraudCase.Open(id, $"FC-{id:N}"[..25], command.Domain, command.ClaimNumber, command.PolicyRef, command.IndicatorCodes, user);
        if (opened.IsFailure)
        {
            return Task.FromResult<Result<FraudCaseView>>(opened.Error);
        }

        cases.Add(opened.Value);
        audit.Record(new AuditEntry("CLAIMS", "OpenFraudCase", nameof(FraudCase), id.ToString()));
        return Task.FromResult<Result<FraudCaseView>>(FraudCaseView.From(opened.Value));
    }

    public Task<Result<FraudCaseView>> Handle(AssignFraudInvestigator command, CancellationToken cancellationToken) =>
        Act(command.Id, "AssignFraudInvestigator", (c, _) => c.Assign(command.InvestigatorId), cancellationToken);

    public Task<Result<FraudCaseView>> Handle(ConcludeFraudCase command, CancellationToken cancellationToken) =>
        Act(command.Id, "ConcludeFraudCase", (c, user) => c.Conclude(command.Outcome, command.Reason, user, clock.UtcNow), cancellationToken);

    private async Task<Result<FraudCaseView>> Act(Guid id, string operation, Func<FraudCase, Guid, Result> act, CancellationToken cancellationToken)
    {
        if (Person() is not { } user)
        {
            return NoPerson;
        }

        if (await cases.Get(id, cancellationToken) is not { } fraudCase)
        {
            return NotFound;
        }

        var acted = act(fraudCase, user);
        if (acted.IsFailure)
        {
            return acted.Error;
        }

        audit.Record(new AuditEntry("CLAIMS", operation, nameof(FraudCase), id.ToString()));
        return FraudCaseView.From(fraudCase);
    }

    public async Task<Result<FraudCaseView>> Handle(ReadFraudCase command, CancellationToken cancellationToken)
    {
        if (await cases.Get(command.Id, cancellationToken) is not { } fraudCase)
        {
            return NotFound;
        }

        audit.Record(new AuditEntry("CLAIMS", "ReadFraudCase", nameof(FraudCase), command.Id.ToString()));
        return FraudCaseView.From(fraudCase);
    }

    public async Task<Result<IReadOnlyList<FraudCaseView>>> Handle(ReadFraudCases command, CancellationToken cancellationToken)
    {
        var found = await cases.List(command.AfterId, command.Limit, cancellationToken);
        audit.Record(new AuditEntry("CLAIMS", "ListFraudCases", nameof(FraudCase), "list", new Dictionary<string, string?> { ["rows"] = found.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return Result.Success<IReadOnlyList<FraudCaseView>>(found.Select(FraudCaseView.From).ToList());
    }
}
