using Insys.Billing.Domain.Billing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Health.Contracts;

namespace Insys.Billing.Application;

/// <summary>
/// Billing's reactions to Health memberships (process "Health care to provider payment": CreateSchedule for
/// contributions). A membership with a known monthly contribution (scheme members) is billed monthly from its start;
/// a change of members amends the contribution; termination ends the schedule. TODO(spec): the monthly billing
/// frequency code and the contribution of individual members (no scheme) are not specified (OQ-061).
/// </summary>
public sealed class HealthContributionBilling(IBillingRepository billing, IBillingCodes codes, IClock clock) :
    IIntegrationEventHandler<HealthMemberEnrolled>,
    IIntegrationEventHandler<DependantAdded>,
    IIntegrationEventHandler<HealthMemberStatusChanged>
{
    public const string SourceDomain = HealthReferences.SourceDomain;
    public const string Monthly = "MONTHLY";

    public async Task Handle(HealthMemberEnrolled enrolled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (enrolled.MonthlyContribution is not > 0 || await billing.AccountForPolicy(SourceDomain, enrolled.MembershipId, cancellationToken) is not null)
        {
            return;
        }

        var account = await billing.Account(enrolled.BillingAccountRef, cancellationToken);
        if (account is null)
        {
            if (!await codes.CollectionMethodExists(enrolled.CollectionMethod, clock.UtcToday, cancellationToken))
            {
                throw new InvalidOperationException($"{enrolled.CollectionMethod} is not a collection method in force; member {enrolled.MemberNumber} cannot be billed.");
            }

            account = BillingAccount.OpenAccount(enrolled.BillingAccountRef, enrolled.PayerPartyId, enrolled.Currency, enrolled.CollectionMethod, null, clock.UtcNow);
            billing.Add(account);
        }
        else if (account.Currency != enrolled.Currency || account.PayerPartyId != enrolled.PayerPartyId)
        {
            throw new InvalidOperationException($"Billing account {account.AccountNumber} is not the payer's account in {enrolled.Currency} for member {enrolled.MemberNumber}.");
        }

        var schedule = account.CreateSchedule(SourceDomain, enrolled.MembershipId, $"{enrolled.PlanCode}/{enrolled.OptionCode}", await Frequency(enrolled.MemberNumber, cancellationToken),
            enrolled.StartDate, null, enrolled.MonthlyContribution.Value);
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"Member {enrolled.MemberNumber} cannot be scheduled: {schedule.Error.Message}");
        }

        await DueInvoices.Issue(account, clock.UtcToday, billing, codes, clock, cancellationToken);
    }

    public Task Handle(DependantAdded added, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Amend(added.MembershipId, added.MemberNumber, added.MonthlyContribution, added.StartDate, cancellationToken);

    public async Task Handle(HealthMemberStatusChanged changed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (changed.Status == "Terminated")
        {
            if (await billing.AccountForPolicy(SourceDomain, changed.MembershipId, cancellationToken) is { } account
                && account.ScheduleFor(SourceDomain, changed.MembershipId) is { } schedule)
            {
                account.EndSchedule(schedule.Id, changed.EffectiveDate.AddDays(1));
            }

            return;
        }

        await Amend(changed.MembershipId, changed.MemberNumber, changed.MonthlyContribution, changed.EffectiveDate, cancellationToken);
    }

    private async Task Amend(Guid membershipId, string memberNumber, decimal? contribution, DateOnly from, CancellationToken cancellationToken)
    {
        if (contribution is not > 0 || await billing.AccountForPolicy(SourceDomain, membershipId, cancellationToken) is not { } account
            || account.ScheduleFor(SourceDomain, membershipId) is not { } schedule)
        {
            return;
        }

        var amended = account.AmendSchedule(schedule.Id, await Frequency(memberNumber, cancellationToken), contribution.Value, from);
        if (amended.IsFailure)
        {
            throw new InvalidOperationException($"Member {memberNumber}'s contribution cannot be amended: {amended.Error.Message}");
        }
    }

    private async Task<BillingFrequency> Frequency(string memberNumber, CancellationToken cancellationToken) =>
        await codes.Frequency(Monthly, clock.UtcToday, cancellationToken)
        ?? throw new InvalidOperationException($"{Monthly} is not a billing frequency in force; member {memberNumber}'s contributions cannot be billed.");
}
