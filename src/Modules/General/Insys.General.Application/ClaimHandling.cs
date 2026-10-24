/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.ClaimsFramework.Contracts;
using Insys.General.Domain;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.Party.Contracts;
using Insys.ProductFactory.Contracts;

namespace Insys.General.Application;

/// <summary>Command NotifyLoss: the first notification of a loss on a policy's coverage.</summary>
public sealed record NotifyLoss(Guid PolicyId, Guid? RiskObjectId, string CoverageCode, DateOnly LossDate, string CauseCode, string PerilCode, string? CatEventCode,
    string? Description) : ICommand<ClaimView>;

/// <summary>Command RegisterClaim: the cover is validated (GEN-006) and the Claims Framework allocates the claim number (CLM-005).</summary>
public sealed record RegisterClaim(Guid ClaimId) : ICommand<ClaimView>;

public sealed record AddClaimParty(Guid ClaimId, Guid PartyId, string Role, string? InsurerRef) : ICommand<ClaimView>;

/// <summary>Command AppointAssessor: an assessor on an approved panel for the line (GEN-007).</summary>
public sealed record AppointAssessor(Guid ClaimId, Guid AssessorPartyId) : ICommand<ClaimView>;

/// <summary>Command ApproveSettlement: requested by the maker, approved in Workflow by a second person within authority (SRS-002).</summary>
/// <param name="BankAccountId">The payee's verified bank account (party.bank_account); the approval covers it with the payee and amount.</param>
public sealed record RequestSettlement(Guid ClaimId, Guid PayeePartyId, Guid BankAccountId, string ReserveType, string DecisionCode, string Rationale)
    : ICommand<ClaimView>;

public sealed record DeclineClaim(Guid ClaimId, string DecisionCode, string Rationale) : ICommand<ClaimView>;

public enum ClaimAction
{
    Close,
    Reopen,
}

public sealed record ChangeClaim(Guid ClaimId, ClaimAction Action) : ICommand<ClaimView>;

public sealed record GetClaim(Guid ClaimId) : IQuery<ClaimView>;

public enum AssessmentAction
{
    Start,
    Accept,
    RequestFurtherWork,
}

public sealed record ChangeAssessment(Guid AssessmentId, AssessmentAction Action) : ICommand<AssessmentView>;

public sealed record RecordEstimate(Guid AssessmentId, Guid RepairerPartyId, decimal Amount) : ICommand<AssessmentView>;

/// <summary>Command SubmitReport: the assessed amount; the excess is applied from the coverage's excess schedule (GEN-008).</summary>
public sealed record SubmitReport(Guid AssessmentId, decimal AssessedAmount, Guid? ReportDocumentId) : ICommand<AssessmentView>;

public sealed record GetAssessment(Guid AssessmentId) : IQuery<AssessmentView>;

public sealed record ListAssessments(Guid ClaimId) : IQuery<IReadOnlyList<AssessmentView>>;

public sealed record OpenRecovery(Guid ClaimId, string RecoveryType, Guid? CounterpartyPartyId, decimal ExpectedAmount) : ICommand<RecoveryView>;

public enum RecoveryAction
{
    SendDemand,
    WriteOff,
    Close,
}

public sealed record ChangeRecovery(Guid RecoveryId, RecoveryAction Action) : ICommand<RecoveryView>;

public sealed record AddSalvageItem(Guid RecoveryId, string Description) : ICommand<RecoveryView>;

public sealed record DisposeSalvage(Guid RecoveryId, Guid ItemId, string DisposalMethod, decimal Proceeds) : ICommand<RecoveryView>;

/// <summary>Command RecordRecovery: recorded as a claim financial movement against the reserve type (CLM-004).</summary>
public sealed record RecordRecovery(Guid RecoveryId, decimal Amount, string? ReserveType) : ICommand<RecoveryView>;

public sealed record GetRecovery(Guid RecoveryId) : IQuery<RecoveryView>;

public sealed class NotifyLossValidator : AbstractValidator<NotifyLoss>
{
    public NotifyLossValidator()
    {
        RuleFor(c => c.PolicyId).NotEmpty();
        RuleFor(c => c.CoverageCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.CauseCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.PerilCode).NotEmpty().MaximumLength(10);
        RuleFor(c => c.CatEventCode).MaximumLength(15);
    }
}

public sealed class AddClaimPartyValidator : AbstractValidator<AddClaimParty>
{
    public AddClaimPartyValidator()
    {
        RuleFor(c => c.PartyId).NotEmpty();
        RuleFor(c => c.Role).NotEmpty().MaximumLength(15);
        RuleFor(c => c.InsurerRef).MaximumLength(60);
    }
}

public sealed class RequestSettlementValidator : AbstractValidator<RequestSettlement>
{
    public RequestSettlementValidator()
    {
        RuleFor(c => c.PayeePartyId).NotEmpty();
        RuleFor(c => c.BankAccountId).NotEmpty();
        RuleFor(c => c.ReserveType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.DecisionCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Rationale).NotEmpty().MaximumLength(4000);
    }
}

public sealed class DeclineClaimValidator : AbstractValidator<DeclineClaim>
{
    public DeclineClaimValidator()
    {
        RuleFor(c => c.DecisionCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Rationale).NotEmpty().MaximumLength(4000);
    }
}

public sealed class SubmitReportValidator : AbstractValidator<SubmitReport>
{
    public SubmitReportValidator() => RuleFor(c => c.AssessedAmount).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, true);
}

public sealed class OpenRecoveryValidator : AbstractValidator<OpenRecovery>
{
    public OpenRecoveryValidator()
    {
        RuleFor(c => c.RecoveryType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ExpectedAmount).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, true);
    }
}

internal static class ClaimContext
{
    public static async Task<Result<(GeneralClaim Claim, GeneralPolicy Policy)>> Load(IGeneralRepository general, Guid claimId, CancellationToken cancellationToken)
    {
        if (await general.Claim(claimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        return await general.Policy(claim.PolicyId, cancellationToken) is { } policy
            ? (claim, policy)
            : Error.NotFound(ErrorCodes.NotFound, "The claim's policy was not found.");
    }

    /// <summary>The coverage that answers the claim, and the product's schedules for it.</summary>
    public static async Task<Result<(GeneralCoverage Coverage, ProductTerms Product)>> Coverage(GeneralClaim claim, GeneralPolicy policy, ISellableProducts products,
        Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var product = await Products.Issued(products, tenantId, policy.ProductCode, policy.ProductVersion, day, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        return policy.Coverage(claim.RiskObjectId, claim.CoverageCode) is { } coverage
            ? (coverage, product.Value)
            : Error.Validation(GeneralErrorCodes.NotCovered, $"Policy {policy.PolicyNumber} has no {claim.CoverageCode} coverage for the claim.");
    }
}

internal sealed class NotifyLossHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<NotifyLoss, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(NotifyLoss command, CancellationToken cancellationToken)
    {
        if (await general.Policy(command.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        foreach (var (kind, code) in new[] { (GeneralCode.LossCause, command.CauseCode), (GeneralCode.Peril, command.PerilCode) }
                     .Concat(command.CatEventCode is { } cat ? [(GeneralCode.CatastropheEvent, cat)] : []))
        {
            if (!await codes.Exists(kind, code, businessDate.Today, cancellationToken))
            {
                return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{code} is not a {kind} code in force.");
            }
        }

        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), command.PolicyId, command.RiskObjectId, command.CoverageCode, command.LossDate, command.CauseCode,
            command.PerilCode, command.CatEventCode, command.Description, businessDate.Today, clock.UtcNow);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        claim.Value.PlaceAt(policy.ProductCode, policy.OrgNodeCode);
        general.Add(claim.Value);
        audit.Record(new AuditEntry("GENERAL", "NotifyLoss", nameof(GeneralClaim), claim.Value.Id.ToString()));
        return ClaimView.From(claim.Value);
    }
}

internal sealed class RegisterClaimHandler(IGeneralRepository general, IClaimRegistry registry, IClock clock, IAuditTrail audit) : ICommandHandler<RegisterClaim, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(RegisterClaim command, CancellationToken cancellationToken)
    {
        var loaded = await ClaimContext.Load(general, command.ClaimId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (claim, policy) = loaded.Value;
        if (claim.Status != ClaimStatus.FirstNotification)
        {
            return ClaimView.From(claim);
        }

        // Cover first (GEN-006), so a claim number is allocated only for a loss the policy covers.
        var covered = policy.CoversLoss(claim.RiskObjectId, claim.CoverageCode, claim.LossDate);
        if (covered.IsFailure)
        {
            return covered.Error;
        }

        var facts = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["days_since_inception"] = claim.LossDate.DayNumber - policy.PeriodStart.DayNumber,
            ["days_to_notification"] = DateOnly.FromDateTime(claim.NotifiedAt.UtcDateTime).DayNumber - claim.LossDate.DayNumber,
            ["peril_code"] = claim.PerilCode,
            ["cause_code"] = claim.CauseCode,
            ["catastrophe"] = claim.CatEventCode is not null,
            ["sum_insured"] = policy.Coverage(claim.RiskObjectId, claim.CoverageCode)?.SumInsured,
        });
        var registered = await registry.Register("GENERAL", claim.Id, policy.Id, new ClaimOwner(policy.ProductCode, policy.OrgNodeCode), facts, null, cancellationToken);
        if (!registered.Succeeded)
        {
            return Error.Validation(registered.ErrorCode ?? ErrorCodes.ValidationFailed, registered.ErrorMessage ?? "The claim number could not be allocated.");
        }

        audit.Record(new AuditEntry("GENERAL", "RegisterClaim", nameof(GeneralClaim), claim.Id.ToString(), new Dictionary<string, string?> { ["claim"] = registered.ClaimNumber }));
        var result = claim.RegisterClaim(policy, registered.ClaimNumber!, clock.UtcNow);
        return result.IsFailure ? result.Error : ClaimView.From(claim);
    }
}

internal sealed class AddClaimPartyHandler(IGeneralRepository general) : ICommandHandler<AddClaimParty, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(AddClaimParty command, CancellationToken cancellationToken)
    {
        if (await general.Claim(command.ClaimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        claim.AddParty(command.PartyId, command.Role, command.InsurerRef);
        return ClaimView.From(claim);
    }
}

internal sealed class AppointAssessorHandler(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, ITenantContext tenant, IBusinessDate businessDate, IClock clock)
    : ICommandHandler<AppointAssessor, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(AppointAssessor command, CancellationToken cancellationToken)
    {
        var loaded = await ClaimContext.Load(general, command.ClaimId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (claim, policy) = loaded.Value;
        var product = await Products.Issued(products, tenant.RequireTenantId(), policy.ProductCode, policy.ProductVersion, businessDate.Today, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        if (!await codes.OnPanel(command.AssessorPartyId, product.Value.Line, repairer: false, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.AssessorNotOnPanel, $"The assessor is not on an approved panel for {product.Value.Line} (GEN-007).");
        }

        var result = claim.AppointAssessor(command.AssessorPartyId, clock.UtcNow);
        return result.IsFailure ? result.Error : ClaimView.From(claim);
    }
}

internal sealed class RequestSettlementHandler(IGeneralRepository general, ISellableProducts products, IBankAccounts bankAccounts, ITenantContext tenant,
    IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<RequestSettlement, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(RequestSettlement command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var loaded = await ClaimContext.Load(general, command.ClaimId, cancellationToken);
        if (user.IsFailure || loaded.IsFailure)
        {
            return user.IsFailure ? user.Error : loaded.Error!;
        }

        var (claim, policy) = loaded.Value;
        var assessment = (await general.Assessments(claim.Id, cancellationToken)).Where(a => a.Status == AssessmentStatus.Accepted).MaxBy(a => a.AssessedAt);
        if (assessment is null)
        {
            return Error.Conflict(GeneralErrorCodes.AssessmentNotAccepted, "The settlement follows an accepted loss assessment.");
        }

        var coverage = await ClaimContext.Coverage(claim, policy, products, tenant.RequireTenantId(), businessDate.Today, cancellationToken);
        if (coverage.IsFailure)
        {
            return coverage.Error;
        }

        // Paid only to the payee's own verified account in the policy's currency.
        if (await bankAccounts.Get(tenant.RequireTenantId(), command.BankAccountId, cancellationToken) is not { IsPayable: true } account
            || account.PartyId != command.PayeePartyId || account.Currency != policy.Currency)
        {
            return Error.Validation(GeneralErrorCodes.BankAccountNotPayable, "The bank account is not the payee's verified, current account in the policy's currency.");
        }

        audit.Record(new AuditEntry("GENERAL", "ApproveSettlement", nameof(GeneralClaim), claim.Id.ToString()));
        var limits = coverage.Value.Product.Limits.Where(l => l.CoverageCode == claim.CoverageCode && l.Currency == policy.Currency).ToList();
        var result = claim.RequestSettlement(new SettlementRequest(assessment.AssessedAmount!.Value, assessment.ExcessApplied!.Value, command.PayeePartyId,
            account.Id, account.AccountNumberHash, command.ReserveType, command.DecisionCode, command.Rationale), coverage.Value.Coverage, limits, policy.Currency, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : ClaimView.From(claim);
    }
}

internal sealed class DeclineClaimHandler(IGeneralRepository general, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<DeclineClaim, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(DeclineClaim command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Claim(command.ClaimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        audit.Record(new AuditEntry("GENERAL", "DeclineClaim", nameof(GeneralClaim), claim.Id.ToString(), new Dictionary<string, string?> { ["decision"] = command.DecisionCode }));
        var result = claim.DeclineClaim(command.DecisionCode, command.Rationale, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : ClaimView.From(claim);
    }
}

internal sealed class ChangeClaimHandler(IGeneralRepository general, IClock clock, IAuditTrail audit) : ICommandHandler<ChangeClaim, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(ChangeClaim command, CancellationToken cancellationToken)
    {
        if (await general.Claim(command.ClaimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        audit.Record(new AuditEntry("GENERAL", $"{command.Action}Claim", nameof(GeneralClaim), claim.Id.ToString()));
        var result = command.Action == ClaimAction.Close ? claim.CloseClaim(clock.UtcNow) : claim.ReopenClaim();
        return result.IsFailure ? result.Error : ClaimView.From(claim);
    }
}

internal sealed class GetClaimHandler(IGeneralRepository general) : IQueryHandler<GetClaim, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(GetClaim query, CancellationToken cancellationToken) =>
        await general.Claim(query.ClaimId, cancellationToken) is { } claim ? ClaimView.From(claim) : Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
}

internal sealed class ChangeAssessmentHandler(IGeneralRepository general, IExecutionContext execution) : ICommandHandler<ChangeAssessment, AssessmentView>
{
    public async Task<Result<AssessmentView>> Handle(ChangeAssessment command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Assessment(command.AssessmentId, cancellationToken) is not { } assessment)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The loss assessment was not found.");
        }

        var result = command.Action switch
        {
            AssessmentAction.Start => assessment.Start(),
            AssessmentAction.Accept => assessment.AcceptAssessment(user.Value),
            _ => assessment.RequestFurtherWork(),
        };
        return result.IsFailure ? result.Error : AssessmentView.From(assessment);
    }
}

internal sealed class RecordEstimateHandler(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, ITenantContext tenant, IBusinessDate businessDate)
    : ICommandHandler<RecordEstimate, AssessmentView>
{
    public async Task<Result<AssessmentView>> Handle(RecordEstimate command, CancellationToken cancellationToken)
    {
        if (await general.Assessment(command.AssessmentId, cancellationToken) is not { } assessment)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The loss assessment was not found.");
        }

        var loaded = await ClaimContext.Load(general, assessment.ClaimId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var product = await Products.Issued(products, tenant.RequireTenantId(), loaded.Value.Policy.ProductCode, loaded.Value.Policy.ProductVersion, businessDate.Today,
            cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        if (!await codes.OnPanel(command.RepairerPartyId, product.Value.Line, repairer: true, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.AssessorNotOnPanel, $"The repairer is not on an approved panel for {product.Value.Line} (GEN-007).");
        }

        var result = assessment.RecordEstimate(command.RepairerPartyId, command.Amount);
        return result.IsFailure ? result.Error : AssessmentView.From(assessment);
    }
}

internal sealed class SubmitReportHandler(IGeneralRepository general, ISellableProducts products, ITenantContext tenant, IBusinessDate businessDate, IClock clock) : ICommandHandler<SubmitReport, AssessmentView>
{
    public async Task<Result<AssessmentView>> Handle(SubmitReport command, CancellationToken cancellationToken)
    {
        if (await general.Assessment(command.AssessmentId, cancellationToken) is not { } assessment)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The loss assessment was not found.");
        }

        var loaded = await ClaimContext.Load(general, assessment.ClaimId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var coverage = await ClaimContext.Coverage(loaded.Value.Claim, loaded.Value.Policy, products, tenant.RequireTenantId(), businessDate.Today, cancellationToken);
        if (coverage.IsFailure)
        {
            return coverage.Error;
        }

        // The excess the coverage's excess schedule applies to the assessed amount (GEN-008).
        var schedule = coverage.Value.Product.Limits.Where(l => l.ScheduleCode == coverage.Value.Coverage.ExcessScheduleCode && l.CoverageCode == coverage.Value.Coverage.CoverageCode);
        var excess = coverage.Value.Coverage.ExcessScheduleCode is null ? 0 : ExcessCalculator.Excess(schedule, command.AssessedAmount);
        var result = assessment.SubmitReport(command.AssessedAmount, excess, command.ReportDocumentId, clock.UtcNow);
        return result.IsFailure ? result.Error : AssessmentView.From(assessment);
    }
}

internal sealed class GetAssessmentHandler(IGeneralRepository general) : IQueryHandler<GetAssessment, AssessmentView>
{
    public async Task<Result<AssessmentView>> Handle(GetAssessment query, CancellationToken cancellationToken) =>
        await general.Assessment(query.AssessmentId, cancellationToken) is { } assessment
            ? AssessmentView.From(assessment)
            : Error.NotFound(ErrorCodes.NotFound, "The loss assessment was not found.");
}

internal sealed class ListAssessmentsHandler(IGeneralRepository general) : IQueryHandler<ListAssessments, IReadOnlyList<AssessmentView>>
{
    public async Task<Result<IReadOnlyList<AssessmentView>>> Handle(ListAssessments query, CancellationToken cancellationToken) =>
        (await general.Assessments(query.ClaimId, cancellationToken)).Select(AssessmentView.From).ToList();
}

internal sealed class OpenRecoveryHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate) : ICommandHandler<OpenRecovery, RecoveryView>
{
    public async Task<Result<RecoveryView>> Handle(OpenRecovery command, CancellationToken cancellationToken)
    {
        if (await general.Claim(command.ClaimId, cancellationToken) is not { ClaimNumber: not null } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "No registered claim was found.");
        }

        if (!await codes.Exists(GeneralCode.RecoveryType, command.RecoveryType, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.RecoveryType} is not a recovery type in force.");
        }

        var recovery = RecoveryCase.OpenRecovery(Guid.CreateVersion7(), claim.Id, command.RecoveryType, command.CounterpartyPartyId, command.ExpectedAmount);
        if (recovery.IsFailure)
        {
            return recovery.Error;
        }

        general.Add(recovery.Value);
        return RecoveryView.From(recovery.Value);
    }
}

internal sealed class ChangeRecoveryHandler(IGeneralRepository general) : ICommandHandler<ChangeRecovery, RecoveryView>
{
    public async Task<Result<RecoveryView>> Handle(ChangeRecovery command, CancellationToken cancellationToken)
    {
        if (await general.Recovery(command.RecoveryId, cancellationToken) is not { } recovery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");
        }

        var result = command.Action switch
        {
            RecoveryAction.SendDemand => recovery.SendDemand(),
            RecoveryAction.WriteOff => recovery.WriteOff(),
            _ => recovery.CloseRecovery(),
        };
        return result.IsFailure ? result.Error : RecoveryView.From(recovery);
    }
}

internal sealed class AddSalvageItemHandler(IGeneralRepository general) : ICommandHandler<AddSalvageItem, RecoveryView>
{
    public async Task<Result<RecoveryView>> Handle(AddSalvageItem command, CancellationToken cancellationToken)
    {
        if (await general.Recovery(command.RecoveryId, cancellationToken) is not { } recovery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");
        }

        var result = recovery.AddSalvageItem(command.Description);
        return result.IsFailure ? result.Error : RecoveryView.From(recovery);
    }
}

internal sealed class DisposeSalvageHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate) : ICommandHandler<DisposeSalvage, RecoveryView>
{
    public async Task<Result<RecoveryView>> Handle(DisposeSalvage command, CancellationToken cancellationToken)
    {
        if (await general.Recovery(command.RecoveryId, cancellationToken) is not { } recovery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");
        }

        if (!await codes.Exists(GeneralCode.SalvageDisposal, command.DisposalMethod, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.DisposalMethod} is not a salvage disposal method in force.");
        }

        var result = recovery.DisposeSalvage(command.ItemId, command.DisposalMethod, command.Proceeds);
        return result.IsFailure ? result.Error : RecoveryView.From(recovery);
    }
}

internal sealed class RecordRecoveryHandler(IGeneralRepository general, IClock clock, IAuditTrail audit) : ICommandHandler<RecordRecovery, RecoveryView>
{
    public async Task<Result<RecoveryView>> Handle(RecordRecovery command, CancellationToken cancellationToken)
    {
        if (await general.Recovery(command.RecoveryId, cancellationToken) is not { } recovery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");
        }

        var loaded = await ClaimContext.Load(general, recovery.ClaimId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        // The reserve the recovery is recorded against: as given, else the settlement's reserve type.
        if ((command.ReserveType ?? loaded.Value.Claim.SettlementReserveType) is not { } reserveType)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A recovery is recorded against a reserve type.");
        }

        audit.Record(new AuditEntry("GENERAL", "RecordRecovery", nameof(RecoveryCase), recovery.Id.ToString()));
        var result = recovery.RecordRecovery(command.Amount, loaded.Value.Claim.ClaimNumber!, loaded.Value.Policy.Currency, reserveType, clock.UtcNow);
        return result.IsFailure ? result.Error : RecoveryView.From(recovery);
    }
}

internal sealed class GetRecoveryHandler(IGeneralRepository general) : IQueryHandler<GetRecovery, RecoveryView>
{
    public async Task<Result<RecoveryView>> Handle(GetRecovery query, CancellationToken cancellationToken) =>
        await general.Recovery(query.RecoveryId, cancellationToken) is { } recovery ? RecoveryView.From(recovery) : Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");
}
