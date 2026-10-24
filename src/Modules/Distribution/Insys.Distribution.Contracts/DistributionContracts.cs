/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Distribution.Contracts;

public static class DistributionPermissions
{
    public const string IntermediaryRead = "intermediary.read";
    public const string CommissionRead = "commission.read";
    public const string CommissionUpdate = "commission.update";
    public const string StatementRead = "commission_statement.read";
    public const string StatementCreate = "commission_statement.create";
}

public static class DistributionApprovals
{
    public const string StatementBusinessRefType = "COMMISSION_STATEMENT";
    public const string StatementApprovalType = "COMMISSION_STATEMENT";
}

/// <summary>Commission was accrued on an invoice, at the plan version in force on its issue date (DST-002, DST-003). Finance posts it.</summary>
[IntegrationEvent("commission.CommissionAccrued")]
public sealed record CommissionAccrued(Guid EntitlementId, string IntermediaryCode, string SourceDomain, Guid PolicyRef, Guid InvoiceId, string PlanCode, int PlanVersion,
    decimal Amount, string Currency, DateOnly AccruedOn) : IIntegrationEvent;

/// <summary>An entitlement was clawed back (always referencing it). Finance posts the reversal.</summary>
[IntegrationEvent("commission.CommissionClawedBack")]
public sealed record CommissionClawedBack(Guid ClawbackId, Guid EntitlementId, string IntermediaryCode, string SourceDomain, Guid PolicyRef, decimal Amount, string Currency,
    string Reason, DateOnly ClawedBackOn) : IIntegrationEvent;

/// <summary>A statement was approved by a second person (DST-004): Payments pays its net amount to the intermediary's verified account.</summary>
/// <param name="BankAccountId">The payee's verified bank account (party.bank_account) the statement was approved with; never its details.</param>
[IntegrationEvent("commission.CommissionStatementApproved")]
public sealed record CommissionStatementApproved(Guid StatementId, string IntermediaryCode, string PeriodCode, Guid PayeePartyId, Guid BankAccountId, decimal NetAmount,
    string Currency) : IIntegrationEvent;

/// <summary>An intermediary's suspension was approved: from <paramref name="EffectiveFrom"/> it starts no new business.</summary>
[IntegrationEvent("commission.IntermediarySuspended")]
public sealed record IntermediarySuspended(string IntermediaryCode, DateOnly EffectiveFrom) : IIntegrationEvent;

/// <summary>Why an intermediary may not sell a product, or null when it may.</summary>
public sealed record IntermediaryRefusal(string Code, string Message);

/// <summary>
/// Public query interface (DST-001): may the intermediary sell the product today? It must be in the register and not
/// suspended or terminated, and hold a permission in force for the product allowing the transaction (empty: any).
/// Read on Distribution's own connection.
/// </summary>
public interface IIntermediaries
{
    Task<IntermediaryRefusal?> CanSell(Guid tenantId, string intermediaryCode, string productCode, string transaction, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether the intermediary is in the register in force, ACTIVE and holding a licence number (for linking a broker portal user to it).</summary>
    Task<bool> IsActiveAndLicensed(Guid tenantId, string intermediaryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// The intermediary and every intermediary below it in the distribution hierarchy: a broker firm's principal sees its
    /// sub-brokers' clients (external-user access, decided 24 October 2026).
    /// </summary>
    Task<IReadOnlySet<string>> WithSubBrokers(Guid tenantId, string intermediaryCode, DateOnly day, CancellationToken cancellationToken);
}

public static class IntermediaryTransactions
{
    public const string NewBusiness = "NEW_BUSINESS";
}

/// <summary>A policy's distribution: its product and the intermediary that sold it, if any.</summary>
public sealed record PolicyDistribution(string SourceDomain, Guid PolicyRef, string ProductCode, string? IntermediaryCode, string? OrgNodeCode = null);

/// <summary>
/// Public query interface each policy domain implements (General, Life): who sold a policy, for commission on its
/// invoices (OQ-062). Read on the domain's own connection.
/// </summary>
public interface IPolicyDistribution
{
    string SourceDomain { get; }

    Task<PolicyDistribution?> For(Guid tenantId, Guid policyRef, CancellationToken cancellationToken);
}
