/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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
public sealed class GeneralPolicyBilling(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<GeneralPolicyIssued>,
    IIntegrationEventHandler<GeneralPolicyMigrated>,
    IIntegrationEventHandler<GeneralCoverExtended>,
    IIntegrationEventHandler<GeneralPolicyEndorsed>,
    IIntegrationEventHandler<GeneralPolicyCancelled>
{
    public const string SourceDomain = "GENERAL";

    /// <summary>
    /// OQ-158: a migrated policy's account and schedule. What fell due by today was invoiced in the source system, so nothing is invoiced for it here;
    /// what is still owed of it comes as an outstanding premium in the same migration batch.
    /// </summary>
    public async Task Handle(GeneralPolicyMigrated migrated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, migrated.PolicyId, cancellationToken) is not null)
        {
            return;
        }

        if (!await codes.CollectionMethodExists(migrated.CollectionMethod, businessDate.Today, cancellationToken))
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.CollectionMethodNotInForce, migrated.CollectionMethod);
        }

        var account = BillingAccount.OpenAccount(migrated.BillingAccountRef, migrated.PayerPartyId, migrated.Currency, migrated.CollectionMethod, null, clock.UtcNow);
        billing.Add(account);
        var frequency = await Frequency(migrated.BillingFrequency, migrated.PolicyNumber, cancellationToken);
        var schedule = account.CreateSchedule(SourceDomain, migrated.PolicyId, $"{migrated.ProductCode} v{migrated.ProductVersion}", frequency, migrated.PeriodStart, migrated.PeriodEnd,
            Instalment(migrated.AnnualPremium, frequency), migrated.AnnualPremium);
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"Policy {migrated.PolicyNumber} cannot be scheduled: {schedule.Error.Message}");
        }

        schedule.Value.ScopeTo(migrated.ProductCode, null);
        schedule.Value.TakenOverOn(businessDate.Today);
    }

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
            if (!await codes.CollectionMethodExists(issued.CollectionMethod, businessDate.Today, cancellationToken))
            {
                throw new PrerequisiteMissingException(PrerequisiteCodes.CollectionMethodNotInForce, issued.CollectionMethod);
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
        // With the loading the policy was quoted with, each instalment and the year are loaded, the last instalment taking the rounding.
        var schedule = account.CreateSchedule(SourceDomain, issued.PolicyId, $"{issued.ProductCode} v{issued.ProductVersion}", frequency, issued.PeriodStart,
            issued.PeriodEnd, Instalment(issued.AnnualPremium, frequency, issued.InstalmentLoadingPct), Loaded(issued.AnnualPremium, issued.InstalmentLoadingPct));
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"Policy {issued.PolicyNumber} cannot be scheduled: {schedule.Error.Message}");
        }

        schedule.Value.ScopeTo(issued.ProductCode, issued.OrgNodeCode); // F103
        schedule.Value.UseLoading(issued.InstalmentLoadingPct);

        var invoices = await DueInvoices.Issue(account, businessDate.Today, billing, codes, clock, businessDate, cancellationToken, firstBilling: true);
        if (invoices.IsFailure)
        {
            throw new InvalidOperationException($"Policy {issued.PolicyNumber} cannot be invoiced: {invoices.Error.Message}");
        }
    }

    /// <summary>AmendSchedule from the effective date, at the new annual premium. Instalments already invoiced are not changed.</summary>
    /// <summary>OQ-132 (b): a non-renewal's notice ran past the period end; the schedule runs on to the new end with the extra days' premium.</summary>
    public async Task Handle(GeneralCoverExtended extended, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, extended.PolicyId, cancellationToken) is not { } account
            || account.ScheduleFor(SourceDomain, extended.PolicyId) is not { } schedule)
        {
            throw new InvalidOperationException($"Policy {extended.PolicyNumber} has no premium schedule yet.");
        }

        if (schedule.EndDate is { } end && end >= extended.NewPeriodEnd)
        {
            return; // already extended (a redelivery)
        }

        var result = account.ExtendSchedule(schedule.Id, extended.NewPeriodEnd, extended.ExtraPremium);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Policy {extended.PolicyNumber}'s schedule cannot be extended: {result.Error.Message}");
        }
    }

    public async Task Handle(GeneralPolicyEndorsed endorsed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, endorsed.PolicyId, cancellationToken) is not { } account
            || account.ScheduleFor(SourceDomain, endorsed.PolicyId) is not { } schedule)
        {
            throw new InvalidOperationException($"Policy {endorsed.PolicyNumber} has no premium schedule yet.");
        }

        var frequency = await Frequency(schedule.Frequency, endorsed.PolicyNumber, cancellationToken);
        var amended = account.AmendSchedule(schedule.Id, frequency, Instalment(endorsed.NewAnnualPremium, frequency, schedule.LoadingPct), endorsed.EffectiveDate,
            Loaded(endorsed.NewAnnualPremium, schedule.LoadingPct));
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

        var reason = await codes.AdjustmentReason(cancelled.ReasonCode, businessDate.Today, cancellationToken)
            ?? await codes.AdjustmentReason("CANCELLATION", businessDate.Today, cancellationToken)
            ?? throw new PrerequisiteMissingException(PrerequisiteCodes.AdjustmentReasonNotInForce, "CANCELLATION");
        var prefix = await codes.NumberPrefix("CREDIT_NOTE", businessDate.Today, cancellationToken);
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

            var credit = invoice.RaiseCreditNote(Guid.CreateVersion7(), prefix, amount, reason, Guid.Empty, businessDate.Today, clock.UtcNow);
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
        await codes.Frequency(code, businessDate.Today, cancellationToken)
        ?? throw new PrerequisiteMissingException(PrerequisiteCodes.BillingFrequencyNotInForce, code);

    /// <summary>
    /// The annual premium spread over the year's instalments, with the instalment loading the policy was quoted with, each
    /// rounded half up; the schedule's last instalment takes the rounding, so they total the loaded annual premium.
    /// </summary>
    private static decimal Instalment(decimal annualPremium, BillingFrequency frequency, decimal? loadingPct = null) =>
        Math.Round(annualPremium / frequency.PeriodsPerYear * (1 + (loadingPct ?? 0m) / 100m), 2, MidpointRounding.AwayFromZero);

    /// <summary>The annual premium with the instalment loading, to the cent: what the year's instalments total.</summary>
    private static decimal Loaded(decimal annualPremium, decimal? loadingPct) =>
        Math.Round(annualPremium * (1 + (loadingPct ?? 0m) / 100m), 2, MidpointRounding.AwayFromZero);
}
