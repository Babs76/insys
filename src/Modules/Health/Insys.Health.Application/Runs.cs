using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Payments;
using Insys.Party.Contracts;

namespace Insys.Health.Application;

public sealed record OpenPaymentRun(string RunCode, string Currency) : ICommand<PaymentRunView>;

public sealed record CutOffPaymentRun(Guid RunId) : ICommand<PaymentRunView>;

public sealed record CalculatePaymentRun(Guid RunId) : ICommand<PaymentRunView>;

public sealed record ReleasePaymentRun(Guid RunId) : ICommand<PaymentRunView>;

public sealed record GetPaymentRun(Guid RunId) : IQuery<PaymentRunView>;

public sealed record OpenUtilisationCase(Guid MembershipId, string RuleCode, Guid? ClaimId) : ICommand<UtilisationCaseView>;

public sealed record ReviewUtilisationCase(Guid CaseId) : ICommand<UtilisationCaseView>;

public sealed record ConcludeUtilisationCase(Guid CaseId, string Outcome) : ICommand<UtilisationCaseView>;

public sealed record GetUtilisationCase(Guid CaseId) : IQuery<UtilisationCaseView>;

public sealed record InvestigateFraudCase(Guid CaseId) : ICommand<FraudCaseView>;

public sealed record ConcludeFraudCase(Guid CaseId, string OutcomeCode) : ICommand<FraudCaseView>;

public sealed record GetFraudCase(Guid CaseId) : IQuery<FraudCaseView>;

public sealed class OpenPaymentRunValidator : AbstractValidator<OpenPaymentRun>
{
    public OpenPaymentRunValidator()
    {
        RuleFor(c => c.RunCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Currency).Length(3);
    }
}

internal sealed class OpenPaymentRunHandler(IHealthRepository health, IHealthCodes codes, IClock clock, IAuditTrail audit) : ICommandHandler<OpenPaymentRun, PaymentRunView>
{
    public async Task<Result<PaymentRunView>> Handle(OpenPaymentRun command, CancellationToken cancellationToken)
    {
        if (!await codes.RunCalendarExists(command.RunCode, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.RunCode} is not a payment run calendar in force.");
        }

        var run = ProviderPaymentRun.OpenRun(Guid.CreateVersion7(), command.RunCode, command.Currency, clock.UtcNow);
        health.Add(run);
        audit.Record(new AuditEntry("HEALTH", "OpenRun", nameof(ProviderPaymentRun), run.Id.ToString()));
        return PaymentRunView.From(run);
    }
}

/// <summary>
/// Commands CutOffRun, CalculateRun and ReleaseRun. Calculation takes the claims approved for payment before the
/// cut-off and not in another run, one remittance per provider into the provider party's verified account (ADR-013),
/// and assigns the claims to the run in the same transaction so no claim is paid twice (ADR-012). TODO(spec): member
/// reimbursement claims are not paid by provider runs (OQ-060 item 11).
/// </summary>
internal sealed class PaymentRunHandlers(IHealthRepository health, IHealthCodes codes, IBankAccounts bankAccounts, ITenantContext tenant, IExecutionContext execution,
    IClock clock, IAuditTrail audit) :
    ICommandHandler<CutOffPaymentRun, PaymentRunView>,
    ICommandHandler<CalculatePaymentRun, PaymentRunView>,
    ICommandHandler<ReleasePaymentRun, PaymentRunView>
{
    public Task<Result<PaymentRunView>> Handle(CutOffPaymentRun command, CancellationToken cancellationToken) =>
        Apply(command.RunId, "CutOffRun", run => Task.FromResult(run.CutOffRun(clock.UtcNow)), cancellationToken);

    public Task<Result<PaymentRunView>> Handle(CalculatePaymentRun command, CancellationToken cancellationToken) =>
        Apply(command.RunId, "CalculateRun", async run =>
        {
            var user = Users.Require(execution);
            if (user.IsFailure)
            {
                return user.Error;
            }

            var claims = (await health.ClaimsForRun(run.CutOff, run.Currency, cancellationToken)).Where(c => c.ProviderId is not null && (c.TotalPaid ?? 0) > 0).ToList();
            var remittances = new List<RemittanceRequest>();
            foreach (var group in claims.GroupBy(c => c.ProviderId!))
            {
                if (await codes.Provider(group.Key, clock.UtcToday, cancellationToken) is not { PartyId: { } partyId }
                    || await bankAccounts.Current(tenant.RequireTenantId(), partyId, run.Currency, cancellationToken) is not { IsPayable: true } account)
                {
                    return Error.Validation(HealthErrorCodes.ProviderNotPayable, $"Provider {group.Key} has no party with a verified bank account in {run.Currency}.");
                }

                remittances.Add(new RemittanceRequest(group.Key, partyId, account.Id, group.Sum(c => c.TotalPaid!.Value), group.Count()));
            }

            var calculated = run.CalculateRun(remittances, user.Value, clock.UtcNow);
            if (calculated.IsFailure)
            {
                return calculated;
            }

            foreach (var claim in claims)
            {
                var assigned = claim.AssignToRun(run.Id);
                if (assigned.IsFailure)
                {
                    return assigned;
                }
            }

            return Result.Success();
        }, cancellationToken);

    public Task<Result<PaymentRunView>> Handle(ReleasePaymentRun command, CancellationToken cancellationToken) =>
        Apply(command.RunId, "ReleaseRun", run => Task.FromResult(run.ReleaseRun(clock.UtcToday, clock.UtcNow)), cancellationToken);

    private async Task<Result<PaymentRunView>> Apply(Guid id, string operation, Func<ProviderPaymentRun, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (await health.Run(id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment run was not found.");
        }

        audit.Record(new AuditEntry("HEALTH", operation, nameof(ProviderPaymentRun), run.Id.ToString()));
        var result = await change(run);
        return result.IsFailure ? result.Error : PaymentRunView.From(run);
    }
}

internal sealed class GetPaymentRunHandler(IHealthRepository health) : IQueryHandler<GetPaymentRun, PaymentRunView>
{
    public async Task<Result<PaymentRunView>> Handle(GetPaymentRun query, CancellationToken cancellationToken) =>
        await health.Run(query.RunId, cancellationToken) is { } run ? PaymentRunView.From(run) : Error.NotFound(ErrorCodes.NotFound, "The payment run was not found.");
}

internal sealed class CaseHandlers(IHealthRepository health, IExecutionContext execution, IClock clock, IAuditTrail audit) :
    ICommandHandler<OpenUtilisationCase, UtilisationCaseView>,
    ICommandHandler<ReviewUtilisationCase, UtilisationCaseView>,
    ICommandHandler<ConcludeUtilisationCase, UtilisationCaseView>,
    IQueryHandler<GetUtilisationCase, UtilisationCaseView>,
    ICommandHandler<InvestigateFraudCase, FraudCaseView>,
    ICommandHandler<ConcludeFraudCase, FraudCaseView>,
    IQueryHandler<GetFraudCase, FraudCaseView>
{
    public async Task<Result<UtilisationCaseView>> Handle(OpenUtilisationCase command, CancellationToken cancellationToken)
    {
        if (await health.Membership(command.MembershipId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        var opened = UtilisationCase.OpenCase(Guid.CreateVersion7(), command.MembershipId, command.RuleCode, command.ClaimId, clock.UtcNow);
        health.Add(opened);
        audit.Record(new AuditEntry("HEALTH", "OpenUtilisationCase", nameof(UtilisationCase), opened.Id.ToString()));
        return UtilisationCaseView.From(opened);
    }

    public Task<Result<UtilisationCaseView>> Handle(ReviewUtilisationCase command, CancellationToken cancellationToken) =>
        Utilisation(command.CaseId, (c, user) => c.ReviewCase(user), cancellationToken);

    public Task<Result<UtilisationCaseView>> Handle(ConcludeUtilisationCase command, CancellationToken cancellationToken) =>
        Utilisation(command.CaseId, (c, user) => c.ConcludeCase(command.Outcome, user, clock.UtcNow), cancellationToken);

    public async Task<Result<UtilisationCaseView>> Handle(GetUtilisationCase query, CancellationToken cancellationToken) =>
        await health.UtilisationCase(query.CaseId, cancellationToken) is { } found ? UtilisationCaseView.From(found) : Error.NotFound(ErrorCodes.NotFound, "The case was not found.");

    public Task<Result<FraudCaseView>> Handle(InvestigateFraudCase command, CancellationToken cancellationToken) =>
        Fraud(command.CaseId, (c, user) => c.Investigate(user), cancellationToken);

    public Task<Result<FraudCaseView>> Handle(ConcludeFraudCase command, CancellationToken cancellationToken) =>
        Fraud(command.CaseId, (c, user) => c.ConcludeCase(command.OutcomeCode, user, clock.UtcNow), cancellationToken);

    public async Task<Result<FraudCaseView>> Handle(GetFraudCase query, CancellationToken cancellationToken) =>
        await health.FraudCase(query.CaseId, cancellationToken) is { } found ? FraudCaseView.From(found) : Error.NotFound(ErrorCodes.NotFound, "The case was not found.");

    private async Task<Result<UtilisationCaseView>> Utilisation(Guid id, Func<UtilisationCase, Guid, Result> change, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.UtilisationCase(id, cancellationToken) is not { } found)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The case was not found.");
        }

        var result = change(found, user.Value);
        return result.IsFailure ? result.Error : UtilisationCaseView.From(found);
    }

    private async Task<Result<FraudCaseView>> Fraud(Guid id, Func<HealthFraudCase, Guid, Result> change, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.FraudCase(id, cancellationToken) is not { } found)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The case was not found.");
        }

        audit.Record(new AuditEntry("HEALTH", "FraudCase", nameof(HealthFraudCase), found.Id.ToString()));
        var result = change(found, user.Value);
        return result.IsFailure ? result.Error : FraudCaseView.From(found);
    }
}
