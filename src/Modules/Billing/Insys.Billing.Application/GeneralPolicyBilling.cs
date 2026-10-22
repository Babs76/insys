using Insys.Billing.Domain.Billing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.General.Contracts;

namespace Insys.Billing.Application;

/// <summary>
/// Billing's reactions to General's policies (domain-model.json: billing consumes GeneralPolicyIssued,
/// GeneralPolicyEndorsed and GeneralPolicyCancelled). Obligations come only from these authorised transactions (BIL-001).
/// A failure is retried and then dead-lettered; it is never silently dropped.
/// </summary>
public sealed class GeneralPolicyBilling(IBillingRepository billing, IBillingCodes codes, IClock clock) :
    IIntegrationEventHandler<GeneralPolicyIssued>,
    IIntegrationEventHandler<GeneralPolicyEndorsed>,
    IIntegrationEventHandler<GeneralPolicyCancelled>
{
    public const string SourceDomain = "GENERAL";

    /// <summary>
    /// OpenAccount (with the account reference the policy carries, or the payer's existing account) and CreateSchedule
    /// for the policy period, then IssueInvoice for the instalments already due.
    /// </summary>
    public async Task Handle(GeneralPolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, issued.PolicyId, cancellationToken) is not null)
        {
            return;
        }

        var account = await billing.Account(issued.BillingAccountRef, cancellationToken);
        if (account is null)
        {
            if (!await codes.CollectionMethodExists(issued.CollectionMethod, clock.UtcToday, cancellationToken))
            {
                throw new InvalidOperationException($"{issued.CollectionMethod} is not a collection method in force; policy {issued.PolicyNumber} cannot be billed.");
            }

            account = BillingAccount.OpenAccount(issued.BillingAccountRef, issued.PayerPartyId, issued.Currency, issued.CollectionMethod, null, clock.UtcNow);
            billing.Add(account);
        }
        else if (account.Currency != issued.Currency || account.PayerPartyId != issued.PayerPartyId)
        {
            throw new InvalidOperationException($"Billing account {account.AccountNumber} is not the payer's account in {issued.Currency} for policy {issued.PolicyNumber}.");
        }

        var frequency = await Frequency(issued.BillingFrequency, issued.PolicyNumber, cancellationToken);
        // The period end is the last day of cover (OQ-059).
        var schedule = account.CreateSchedule(SourceDomain, issued.PolicyId, $"{issued.ProductCode} v{issued.ProductVersion}", frequency, issued.PeriodStart,
            issued.PeriodEnd, Instalment(issued.AnnualPremium, frequency));
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"Policy {issued.PolicyNumber} cannot be scheduled: {schedule.Error.Message}");
        }

        var invoices = await DueInvoices.Issue(account, clock.UtcToday, billing, codes, clock, cancellationToken);
        if (invoices.IsFailure)
        {
            throw new InvalidOperationException($"Policy {issued.PolicyNumber} cannot be invoiced: {invoices.Error.Message}");
        }
    }

    /// <summary>AmendSchedule from the effective date, at the new annual premium. Instalments already invoiced are not changed.</summary>
    public async Task Handle(GeneralPolicyEndorsed endorsed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, endorsed.PolicyId, cancellationToken) is not { } account
            || account.ScheduleFor(SourceDomain, endorsed.PolicyId) is not { } schedule)
        {
            throw new InvalidOperationException($"Policy {endorsed.PolicyNumber} has no premium schedule yet.");
        }

        var frequency = await Frequency(schedule.Frequency, endorsed.PolicyNumber, cancellationToken);
        var amended = account.AmendSchedule(schedule.Id, frequency, Instalment(endorsed.NewAnnualPremium, frequency), endorsed.EffectiveDate);
        if (amended.IsFailure)
        {
            throw new InvalidOperationException($"Policy {endorsed.PolicyNumber}'s schedule cannot be amended: {amended.Error.Message}");
        }
    }

    /// <summary>
    /// The schedule stops from the cancellation date, and the refund is credited (RaiseCreditNote) against the policy's
    /// invoices, latest first. TODO(spec): the adjustment reason is the cancellation reason where Billing has it, else
    /// CANCELLATION; a refund above what was invoiced is left for a refund request (OQ-059).
    /// </summary>
    public async Task Handle(GeneralPolicyCancelled cancelled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, cancelled.PolicyId, cancellationToken) is not { } account
            || account.ScheduleFor(SourceDomain, cancelled.PolicyId) is not { } schedule)
        {
            return;
        }

        account.EndSchedule(schedule.Id, cancelled.EffectiveDate);
        if (cancelled.RefundAmount <= 0)
        {
            return;
        }

        var reason = await codes.AdjustmentReason(cancelled.ReasonCode, clock.UtcToday, cancellationToken)
            ?? await codes.AdjustmentReason("CANCELLATION", clock.UtcToday, cancellationToken)
            ?? throw new InvalidOperationException($"Neither {cancelled.ReasonCode} nor CANCELLATION is an adjustment reason in force; policy {cancelled.PolicyNumber}'s refund is not credited.");
        var prefix = await codes.NumberPrefix("CREDIT_NOTE", clock.UtcToday, cancellationToken);
        var remaining = cancelled.RefundAmount;
        foreach (var invoice in (await billing.Invoices(account.Id, cancellationToken))
                     .Where(i => i.PolicyRef == cancelled.PolicyId && i.DocumentType == DocumentType.Invoice && i.Status != InvoiceStatus.Credited)
                     .OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Id))
        {
            var amount = Math.Min(remaining, invoice.TotalAmount - invoice.Credited);
            if (amount <= 0)
            {
                continue;
            }

            var credit = invoice.RaiseCreditNote(Guid.CreateVersion7(), prefix, amount, reason, Guid.Empty, clock.UtcToday, clock.UtcNow);
            if (credit.IsSuccess)
            {
                billing.Add(credit.Value);
                remaining -= amount;
            }

            if (remaining <= 0)
            {
                break;
            }
        }
    }

    private async Task<BillingFrequency> Frequency(string code, string policyNumber, CancellationToken cancellationToken) =>
        await codes.Frequency(code, clock.UtcToday, cancellationToken)
        ?? throw new InvalidOperationException($"{code} is not a billing frequency in force; policy {policyNumber} cannot be billed.");

    /// <summary>The annual premium spread over the year's instalments. TODO(spec): instalment rounding is not specified; each is rounded half up (OQ-059).</summary>
    private static decimal Instalment(decimal annualPremium, BillingFrequency frequency) =>
        Math.Round(annualPremium / frequency.PeriodsPerYear, 2, MidpointRounding.AwayFromZero);
}
