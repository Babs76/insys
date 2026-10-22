using Insys.Billing.Domain.Billing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Health.Contracts;

namespace Insys.Billing.Application;

/// <summary>
/// Billing's reactions to Health memberships (process "Health care to provider payment": CreateSchedule for
/// contributions). A membership with a known monthly contribution (scheme members) is billed monthly from its start:
/// the member's part to the payer's account, the employer's share to the scheme sponsor's account on a schedule of its
/// own (source domain HEALTH_ER). A change of members amends both; termination ends both. TODO(spec): the monthly
/// billing frequency code and the contribution of individual members (no scheme) are not specified (OQ-061).
/// </summary>
public sealed class HealthContributionBilling(IBillingRepository billing, IBillingCodes codes, IClock clock) :
    IIntegrationEventHandler<HealthMemberEnrolled>,
    IIntegrationEventHandler<DependantAdded>,
    IIntegrationEventHandler<HealthMemberStatusChanged>
{
    public const string SourceDomain = HealthReferences.SourceDomain;
    public const string EmployerSourceDomain = HealthReferences.EmployerSourceDomain;
    public const string Monthly = "MONTHLY";

    public async Task Handle(HealthMemberEnrolled enrolled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (enrolled.MonthlyContribution is > 0 && await billing.AccountForPolicy(SourceDomain, enrolled.MembershipId, cancellationToken) is null)
        {
            var account = await billing.Account(enrolled.BillingAccountRef, cancellationToken);
            if (account is null)
            {
                account = await Open(enrolled.BillingAccountRef, enrolled.PayerPartyId, enrolled, cancellationToken);
            }
            else if (account.Currency != enrolled.Currency || account.PayerPartyId != enrolled.PayerPartyId)
            {
                throw new InvalidOperationException($"Billing account {account.AccountNumber} is not the payer's account in {enrolled.Currency} for member {enrolled.MemberNumber}.");
            }

            await Schedule(account, SourceDomain, enrolled, enrolled.MonthlyContribution.Value, cancellationToken);
        }

        if (enrolled is { EmployerContribution: > 0, SponsorPartyId: { } sponsor }
            && await billing.AccountForPolicy(EmployerSourceDomain, enrolled.MembershipId, cancellationToken) is null)
        {
            var account = await billing.OpenAccountOf(sponsor, enrolled.Currency, cancellationToken)
                          ?? await Open(Guid.CreateVersion7(), sponsor, enrolled, cancellationToken);
            await Schedule(account, EmployerSourceDomain, enrolled, enrolled.EmployerContribution.Value, cancellationToken);
        }
    }

    public async Task Handle(DependantAdded added, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await Amend(SourceDomain, added.MembershipId, added.MemberNumber, added.MonthlyContribution, added.StartDate, cancellationToken);
        await Amend(EmployerSourceDomain, added.MembershipId, added.MemberNumber, added.EmployerContribution, added.StartDate, cancellationToken);
    }

    public async Task Handle(HealthMemberStatusChanged changed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (changed.Status == "Terminated")
        {
            foreach (var domain in new[] { SourceDomain, EmployerSourceDomain })
            {
                if (await billing.AccountForPolicy(domain, changed.MembershipId, cancellationToken) is { } account
                    && account.ScheduleFor(domain, changed.MembershipId) is { } schedule)
                {
                    account.EndSchedule(schedule.Id, changed.EffectiveDate.AddDays(1));
                }
            }

            return;
        }

        await Amend(SourceDomain, changed.MembershipId, changed.MemberNumber, changed.MonthlyContribution, changed.EffectiveDate, cancellationToken);
        await Amend(EmployerSourceDomain, changed.MembershipId, changed.MemberNumber, changed.EmployerContribution, changed.EffectiveDate, cancellationToken);
    }

    private async Task<BillingAccount> Open(Guid id, Guid payerPartyId, HealthMemberEnrolled enrolled, CancellationToken cancellationToken)
    {
        if (!await codes.CollectionMethodExists(enrolled.CollectionMethod, clock.UtcToday, cancellationToken))
        {
            throw new InvalidOperationException($"{enrolled.CollectionMethod} is not a collection method in force; member {enrolled.MemberNumber} cannot be billed.");
        }

        var account = BillingAccount.OpenAccount(id, payerPartyId, enrolled.Currency, enrolled.CollectionMethod, null, clock.UtcNow);
        billing.Add(account);
        return account;
    }

    private async Task Schedule(BillingAccount account, string sourceDomain, HealthMemberEnrolled enrolled, decimal amount, CancellationToken cancellationToken)
    {
        var schedule = account.CreateSchedule(sourceDomain, enrolled.MembershipId, $"{enrolled.PlanCode}/{enrolled.OptionCode}", await Frequency(enrolled.MemberNumber, cancellationToken),
            enrolled.StartDate, null, amount);
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"Member {enrolled.MemberNumber} cannot be scheduled: {schedule.Error.Message}");
        }

        await DueInvoices.Issue(account, clock.UtcToday, billing, codes, clock, cancellationToken);
    }

    private async Task Amend(string sourceDomain, Guid membershipId, string memberNumber, decimal? contribution, DateOnly from, CancellationToken cancellationToken)
    {
        if (contribution is not > 0 || await billing.AccountForPolicy(sourceDomain, membershipId, cancellationToken) is not { } account
            || account.ScheduleFor(sourceDomain, membershipId) is not { } schedule)
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
