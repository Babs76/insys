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
using Insys.Life.Contracts;

namespace Insys.Billing.Application;

/// <summary>
/// Billing's reactions to Life's policies (processes "Life proposal to policy" and "Missed premium to lapse"):
/// OpenAccount, CreateSchedule and IssueInvoice on issue; the schedule stops when the policy lapses and resumes when it
/// is reinstated. Obligations come only from these authorised transactions (BIL-001).
/// </summary>
public sealed class LifePolicyBilling(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<LifePolicyIssued>,
    IIntegrationEventHandler<LifePolicyLapsed>,
    IIntegrationEventHandler<LifePolicyReinstated>
{
    public const string SourceDomain = LifeSubjects.SourceDomain;

    /// <summary>The premium is per instalment at the policy's frequency (Rating prices it so), from inception to the end of the term.</summary>
    public async Task Handle(LifePolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
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
        var schedule = account.CreateSchedule(SourceDomain, issued.PolicyId, $"{issued.ProductCode} v{issued.ProductVersion}", frequency, issued.InceptionDate,
            issued.TermEndDate, issued.Premium);
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"Policy {issued.PolicyNumber} cannot be scheduled: {schedule.Error.Message}");
        }

        schedule.Value.ScopeTo(issued.ProductCode, issued.OrgNodeCode); // F103

        var invoices = await DueInvoices.Issue(account, businessDate.Today, billing, codes, clock, businessDate, cancellationToken, firstBilling: true);
        if (invoices.IsFailure)
        {
            throw new InvalidOperationException($"Policy {issued.PolicyNumber} cannot be invoiced: {invoices.Error.Message}");
        }
    }

    /// <summary>Nothing more is billed from the lapse date; invoices already issued stay as they are.</summary>
    public async Task Handle(LifePolicyLapsed lapsed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, lapsed.PolicyId, cancellationToken) is { } account
            && account.ScheduleFor(SourceDomain, lapsed.PolicyId) is { } schedule)
        {
            account.EndSchedule(schedule.Id, lapsed.LapseDate);
        }
    }

    /// <summary>
    /// The schedule resumes from the reinstatement date to the end of the policy's term.
    /// TODO(spec): whether arrears from before the lapse are collected on reinstatement is not specified.
    /// </summary>
    public async Task Handle(LifePolicyReinstated reinstated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(SourceDomain, reinstated.PolicyId, cancellationToken) is not { } account
            || account.ScheduleFor(SourceDomain, reinstated.PolicyId) is not { } schedule)
        {
            throw new InvalidOperationException($"Policy {reinstated.PolicyNumber} has no premium schedule.");
        }

        var frequency = await Frequency(schedule.Frequency, reinstated.PolicyNumber, cancellationToken);
        var resumed = account.ResumeSchedule(schedule.Id, frequency, reinstated.ReinstatedOn, reinstated.TermEndDate);
        if (resumed.IsFailure)
        {
            throw new InvalidOperationException($"Policy {reinstated.PolicyNumber}'s schedule cannot resume: {resumed.Error.Message}");
        }

        await DueInvoices.Issue(account, businessDate.Today, billing, codes, clock, businessDate, cancellationToken, firstBilling: true);
    }

    private async Task<BillingFrequency> Frequency(string code, string policyNumber, CancellationToken cancellationToken) =>
        await codes.Frequency(code, businessDate.Today, cancellationToken)
        ?? throw new PrerequisiteMissingException(PrerequisiteCodes.BillingFrequencyNotInForce, code);
}
