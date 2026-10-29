/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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

namespace Insys.Integration.Contracts;

/// <summary>A partner's answer to a call: accepted with its reference, or why not.</summary>
public sealed record PartnerCallResult(bool Accepted, string? PartnerRef, string? Error);

/// <summary>
/// Partner adapter to reinsurers (§8, §17): bordereaux and recovery bills. The platform makes no real calls (PROMPT
/// §11); the stub implementation accepts each and records it in integration.external_call_log (INT-006).
/// </summary>
public interface IReinsurerAdapter
{
    Task<PartnerCallResult> SendBordereau(Guid tenantId, string partnerCode, string treatyCode, string periodCode, string content, CancellationToken cancellationToken);

    Task<PartnerCallResult> BillRecovery(Guid tenantId, string partnerCode, string treatyCode, string claimNumber, decimal amount, string currency,
        CancellationToken cancellationToken);
}

/// <summary>The partner types an adapter call goes to (integration.mst_partner_register.partner_type).</summary>
public static class PartnerTypes
{
    public const string IdentityVerification = "IDENTITY_VERIFICATION";
    public const string Screening = "SCREENING";
}

/// <summary>A provider's answer to an identity check (F001): verified with its reference, or why not.</summary>
public sealed record IdentityVerificationResult(bool Verified, string? ProviderRef, string? Reason);

/// <summary>
/// Partner adapter to identity verification providers (F001, addenda/due-diligence.md §4.1): an identity document's number
/// is checked against the provider's source (a population register, through a vendor). The number is sent and never
/// logged. The stub verifies every number except one ending 0000, which it answers NO_MATCH, so tests can see both.
/// </summary>
public interface IIdentityVerificationAdapter
{
    Task<IdentityVerificationResult> Verify(Guid tenantId, string partnerCode, string docType, string country, string number, string? firstNames, string? surname,
        DateOnly? birthDate, CancellationToken cancellationToken);
}

/// <summary>The lists a screening runs against (F003): SANCTIONS, PEP and, where the tenant switched it on, ADVERSE_MEDIA.</summary>
public static class ScreeningLists
{
    public const string Sanctions = "SANCTIONS";
    public const string Pep = "PEP";
    public const string AdverseMedia = "ADVERSE_MEDIA";
}

/// <summary>Who is screened: a person (with a birth date) or an organisation, by name and country.</summary>
public sealed record ScreeningSubject(bool IsOrganisation, string Name, DateOnly? BirthDate, string? Country);

/// <summary>A match on a list: the list, the name matched, how strongly, and the provider's reference for it.</summary>
public sealed record ScreeningMatch(string ListType, string ListName, string MatchedName, decimal Score, string ProviderRef);

/// <summary>A screening's answer: the provider's reference, the list version screened against, and any matches.</summary>
public sealed record ScreeningResult(string ProviderRef, string ListVersion, IReadOnlyList<ScreeningMatch> Matches);

/// <summary>
/// Partner adapter to screening providers (F003, addenda/due-diligence.md §4.3): a party is screened against sanctions,
/// PEP and adverse media lists. Only the partner and operation are logged, never the subject. The stub matches names
/// containing SANCTIONLIST, PEPLIST or MEDIALIST (any case) on the list named, and reports the list version a test set
/// (StubScreeningLists), so tests can see hits and list updates.
/// </summary>
public interface IScreeningAdapter
{
    Task<ScreeningResult> Screen(Guid tenantId, string partnerCode, ScreeningSubject subject, IReadOnlyCollection<string> listTypes, CancellationToken cancellationToken);

    /// <summary>The version of the provider's lists now; a party screened against an older one is screened again (OQ-125).</summary>
    Task<string> ListVersion(Guid tenantId, string partnerCode, CancellationToken cancellationToken);
}

/// <summary>Public query interface: the tenant's active partner of a type in force on the day, if any (integration.mst_partner_register).</summary>
public interface IPartnerDirectory
{
    Task<string?> ActivePartner(Guid tenantId, string partnerType, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>Partner adapter to regulators: returns are submitted through it; the stub accepts and records each call.</summary>
public interface IRegulatorAdapter
{
    Task<PartnerCallResult> SubmitReturn(Guid tenantId, string regulator, string returnCode, string periodCode, JsonElement values, CancellationToken cancellationToken);
}

/// <summary>
/// Adapter to the insurer's general ledger (SRS §15.1, §2.2: the GL itself stays outside). An export batch of summarised
/// journal lines is sent through it; the stub accepts each and records the call (INT-006).
/// </summary>
public interface IGeneralLedgerAdapter
{
    Task<PartnerCallResult> SendGlExport(Guid tenantId, string layoutCode, string targetSystem, string periodCode, string content, CancellationToken cancellationToken);
}

