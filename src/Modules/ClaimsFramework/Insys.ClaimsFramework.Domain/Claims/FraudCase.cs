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
using Insys.BuildingBlocks.Domain;

namespace Insys.ClaimsFramework.Domain.Claims;

public static class FraudCaseStatus
{
    public const string Opened = "OPENED";
    public const string Investigating = "INVESTIGATING";
    public const string Concluded = "CONCLUDED";
}

public static class FraudOutcomes
{
    public const string Confirmed = "CONFIRMED";
    public const string NotConfirmed = "NOT_CONFIRMED";
}

/// <summary>
/// The FraudCase aggregate (F058) for Life and General, as Health's is. It raises no event and has no portal route, so its subject can't be tipped off: nothing outside the fraud function
/// learns of it (OQ-160). Its outcome changes nothing about a claim: holding or releasing one is a person's separate act.
/// </summary>
public sealed class FraudCase : AggregateRoot<Guid>
{
    private FraudCase()
    {
    }

    public string CaseNumber { get; private set; } = "";

    public string Domain { get; private set; } = "";

    public string? ClaimNumber { get; private set; }

    public Guid? PolicyRef { get; private set; }

    public string[] IndicatorCodes { get; private set; } = [];

    public string Status { get; private set; } = FraudCaseStatus.Opened;

    public Guid? InvestigatorId { get; private set; }

    public string? Outcome { get; private set; }

    public string? OutcomeReason { get; private set; }

    public Guid OpenedBy { get; private set; }

    public DateTimeOffset? ConcludedAt { get; private set; }

    public static Result<FraudCase> Open(Guid id, string number, string domain, string? claimNumber, Guid? policyRef, IReadOnlyList<string> indicatorCodes, Guid openedBy)
    {
        if (claimNumber is null && policyRef is null)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A fraud case is about a claim or a policy.");
        }

        var opened = new FraudCase
        {
            Id = Guard.NotEmpty(id), CaseNumber = Guard.MaxLength(number, 25), Domain = domain, ClaimNumber = claimNumber, PolicyRef = policyRef, IndicatorCodes = [.. indicatorCodes], OpenedBy = Guard.NotEmpty(openedBy),
        };
        opened.MarkChanged();
        return opened;
    }

    public Result Assign(Guid investigator)
    {
        if (Status == FraudCaseStatus.Concluded)
        {
            return Wrong();
        }

        Status = FraudCaseStatus.Investigating;
        InvestigatorId = Guard.NotEmpty(investigator);
        MarkChanged();
        return Result.Success();
    }

    public Result Conclude(string outcome, string reason, Guid by, DateTimeOffset now)
    {
        if (Status != FraudCaseStatus.Investigating)
        {
            return Wrong();
        }

        if (by != InvestigatorId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "The investigator concludes the case.");
        }

        Status = FraudCaseStatus.Concluded;
        Outcome = outcome;
        OutcomeReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 1000);
        ConcludedAt = now;
        MarkChanged();
        return Result.Success();
    }

    private Result Wrong() => Error.Conflict(ErrorCodes.InvalidStateTransition, $"Case {CaseNumber} is {Status}.");
}
