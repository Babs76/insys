/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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

namespace Insys.Workflow.Contracts;

/// <summary>The case permissions (§9). TODO: to move to Workflow.Contracts' WorkflowPermissions with the contract owner.</summary>
public static class CasePermissions
{
    public const string Read = "case.read";
    public const string Create = "case.create";
    public const string Update = "case.update";
    public const string Decide = "case.decide";
    public const string Redress = "case.redress";
    public const string ReportRead = "case_report.read";
}

/// <summary>The four families of case (addenda/cases.md §1): each case type belongs to one.</summary>
public static class CaseFamilies
{
    public const string Enquiry = "ENQUIRY";
    public const string Complaint = "COMPLAINT";
    public const string Dispute = "DISPUTE";
    public const string Appeal = "APPEAL";
}

/// <summary>A case's outcome (§4): the PPR's upheld and rejected, partly upheld, an enquiry answered, or withdrawn.</summary>
public static class CaseOutcomes
{
    public const string Upheld = "UPHELD";
    public const string PartlyUpheld = "PARTLY_UPHELD";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";
    public const string Answered = "ANSWERED";
}

/// <summary>The kinds of record a case links to (workflow.case_link); the record ID is the record's own ID.</summary>
public static class CaseLinkTypes
{
    public const string Policy = "POLICY";
    public const string Claim = "CLAIM";
    public const string Membership = "MEMBERSHIP";
    public const string Payment = "PAYMENT";
    public const string Invoice = "INVOICE";
    public const string BillingAccount = "BILLING_ACCOUNT";
    public const string Party = "PARTY";
    public const string Provider = "PROVIDER";
    public const string Remittance = "REMITTANCE";
    public const string PaymentRun = "PAYMENT_RUN";
    public const string Intermediary = "INTERMEDIARY";
}

/// <summary>Public query interface: how many cases concern a provider (F029).</summary>
public interface IProviderCaseCounts
{
    /// <summary>The cases received in the period that the provider raised or is linked to. A count only, never their content.</summary>
    Task<int> Count(Guid tenantId, string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>
/// BR-101: whether a portal user may link one of a module's records to a case they raise. The module answers as its
/// own portal read of the record does, for the current caller: a record that read would not show (another member's
/// claim, or one outside the caller's scope) cannot be linked. Used by portal cases only, so no other portal read
/// (documents, say) gains access through it.
/// </summary>
public interface IPortalCaseLinks
{
    bool Covers(string linkType);

    Task<bool> Visible(string linkType, Guid recordId, CancellationToken cancellationToken);
}

/// <summary>Redress on a case is approved by a second person and paid through Payments (OQ-104).</summary>
public static class CaseApprovals
{
    public const string RedressApprovalType = "CASE_REDRESS";
    public const string RedressBusinessRefType = "CASE_REDRESS";

    /// <summary>The business reference type documents and notes use for a case.</summary>
    public const string CaseBusinessRefType = "CASE";

    /// <summary>The payment purpose of a redress payment (PaymentInitiated, PaymentSucceeded).</summary>
    public const string RedressPaymentPurpose = "CASE_REDRESS";
}

/// <param name="Reference">The record's own number, for display (a claim number, a policy number).</param>
public sealed record CaseLinkRef(string LinkType, Guid RecordId, string Reference);

/// <summary>A case was opened (received). Identifiers only (INT-007): never the case's text.</summary>
[IntegrationEvent("workflow.CaseOpened")]
public sealed record CaseOpened(Guid CaseId, string CaseNumber, string CaseTypeCode, string Family, Guid? ComplainantPartyId, IReadOnlyList<CaseLinkRef> Links)
    : IIntegrationEvent;

/// <summary>The case was acknowledged to the complainant (§7: CASE_ACKNOWLEDGED). The acknowledgement is the confirmation (OQ-107).</summary>
[IntegrationEvent("workflow.CaseAcknowledged")]
public sealed record CaseAcknowledged(Guid CaseId, string CaseNumber, string Family, Guid? ComplainantPartyId, DateTimeOffset? ExpectedBy) : IIntegrationEvent;

/// <summary>Progress or a delay the complainant is told of (§7: CASE_UPDATE): a timeline extended, or a step overdue.</summary>
[IntegrationEvent("workflow.CaseUpdated")]
public sealed record CaseUpdated(Guid CaseId, string CaseNumber, string Family, Guid? ComplainantPartyId, string Reason, DateTimeOffset? ExpectedBy) : IIntegrationEvent;

[IntegrationEvent("workflow.CaseEscalated")]
public sealed record CaseEscalated(Guid CaseId, string CaseNumber, string Family, int Level) : IIntegrationEvent;

/// <summary>
/// The case was decided (§4). A claim's module reopens the claim for reassessment when a dispute or appeal linking it is
/// upheld or partly upheld (F011, F012, F013, OQ-103); Communications sends CASE_DECISION.
/// </summary>
[IntegrationEvent("workflow.CaseDecided")]
public sealed record CaseDecided(Guid CaseId, string CaseNumber, string CaseTypeCode, string Family, string Outcome, Guid? ComplainantPartyId,
    IReadOnlyList<CaseLinkRef> Links) : IIntegrationEvent;

[IntegrationEvent("workflow.CaseReferredToOmbud")]
public sealed record CaseReferredToOmbud(Guid CaseId, string CaseNumber, string SchemeCode) : IIntegrationEvent;

[IntegrationEvent("workflow.CaseClosed")]
public sealed record CaseClosed(Guid CaseId, string CaseNumber, string Family, string? Outcome) : IIntegrationEvent;

[IntegrationEvent("workflow.CaseReopened")]
public sealed record CaseReopened(Guid CaseId, string CaseNumber, string Family) : IIntegrationEvent;

/// <summary>
/// Redress on a case was approved by a second person (OQ-104): Payments pays it out to the payee's verified account,
/// with purpose CASE_REDRESS and business reference CASE_REDRESS / the redress ID, as it pays refunds.
/// </summary>
[IntegrationEvent("workflow.CaseRedressApproved")]
public sealed record CaseRedressApproved(Guid RedressId, Guid CaseId, string CaseNumber, string RedressType, decimal Amount, string Currency, Guid PayeePartyId,
    Guid? BankAccountId) : IIntegrationEvent;

/// <summary>What a case notice's merge fields need (§7), read by Communications when it renders one; never put in an event.</summary>
public sealed record CaseNoticeData(string CaseNumber, string CaseTypeCode, string Family, DateTimeOffset? ExpectedBy, string? DecisionReasons, string? ReviewOptions,
    string? OmbudDetails, DateTimeOffset? RepresentationsDeadline, Guid? ComplainantPartyId, string? RaisedByIntermediaryCode, string? RaisedByProviderId);

/// <summary>Public query interface: a case's notice data for the current tenant, or null.</summary>
public interface ICaseNotices
{
    Task<CaseNoticeData?> ForNotice(Guid caseId, CancellationToken cancellationToken);
}

/// <summary>
/// Where a claimant takes a claim decision (OQ-101, OQ-102), from the tenant's dispute case type for claims in force: its
/// appeal route (PPR_NFO for an insurer, CMS for a medical scheme), the internal review open to them and the ombud schemes.
/// </summary>
public sealed record ClaimAppealRoute(string AppealRoute, string? ReviewOptions, string? OmbudDetails);

/// <summary>A claim's appeal route for a claim decision notice, in the current tenant; null when no dispute case type for claims is in force.</summary>
public interface IClaimAppealRoutes
{
    /// <param name="domain">LIFE, GENERAL or HEALTH: a Health claim takes a CMS route where the tenant has one (a medical scheme).</param>
    Task<ClaimAppealRoute?> For(string domain, CancellationToken cancellationToken);
}
